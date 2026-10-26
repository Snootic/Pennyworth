using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Pennyworth.Core.Storage;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class PosixDirectoryScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pw-posix-scan-").FullName;
    private readonly List<string> _restricted = [];

    public void Dispose()
    {
        foreach (string directory in _restricted)
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // rm, not Directory.Delete: .NET cannot unlink a name that is not valid UTF-8.
        Run("/bin/rm", "-rf", _root);
    }

    [Fact]
    public void Statx_struct_has_the_kernel_layout()
    {
        Assert.Equal(256, Marshal.SizeOf<StatxBuffer>());
        Assert.Equal(16, Marshal.SizeOf<StatxTimestamp>());
        (string Field, int Offset)[] expected =
        [
            (nameof(StatxBuffer.Mask), 0), (nameof(StatxBuffer.BlockSize), 4), (nameof(StatxBuffer.Attributes), 8),
            (nameof(StatxBuffer.LinkCount), 16), (nameof(StatxBuffer.Uid), 20), (nameof(StatxBuffer.Gid), 24),
            (nameof(StatxBuffer.Mode), 28), (nameof(StatxBuffer.Inode), 32), (nameof(StatxBuffer.Size), 40),
            (nameof(StatxBuffer.Blocks), 48), (nameof(StatxBuffer.AttributesMask), 56), (nameof(StatxBuffer.AccessTime), 64),
            (nameof(StatxBuffer.BirthTime), 80), (nameof(StatxBuffer.ChangeTime), 96), (nameof(StatxBuffer.ModifyTime), 112),
            (nameof(StatxBuffer.RdevMajor), 128), (nameof(StatxBuffer.RdevMinor), 132),
            (nameof(StatxBuffer.DeviceMajor), 136), (nameof(StatxBuffer.DeviceMinor), 140),
        ];
        foreach ((string field, int offset) in expected)
        {
            Assert.Equal(offset, (int)Marshal.OffsetOf<StatxBuffer>(field));
        }
    }

    [Fact]
    public void FromBuffer_derives_kind_sizes_and_identity()
    {
        var buffer = new StatxBuffer
        {
            Mode = 0xA1FF, // symlink, rwxrwxrwx
            Blocks = 8,
            Size = 12345,
            Inode = 77,
            LinkCount = 3,
            DeviceMajor = 0,
            DeviceMinor = 38,
        };

        FileStat stat = Statx.FromBuffer(buffer);

        Assert.Equal(FileKind.Symlink, stat.Kind);
        Assert.Equal(4096, stat.AllocatedBytes);
        Assert.Equal(12345, stat.LogicalBytes);
        Assert.Equal(38UL, stat.Device);
        Assert.Equal(77UL, stat.Inode);
        Assert.Equal(3u, stat.LinkCount);
        Assert.Equal(FileKind.Directory, Statx.FromBuffer(new StatxBuffer { Mode = 0x41ED }).Kind);
        Assert.Equal(FileKind.Other, Statx.FromBuffer(new StatxBuffer { Mode = 0x81A4 }).Kind);
        Assert.Equal(FileKind.Other, Statx.FromBuffer(new StatxBuffer { Mode = 0x2000 }).Kind);
    }

    [LinuxFact]
    public void Statx_agrees_with_the_managed_view_of_a_file()
    {
        string path = WriteFile("a.bin", 10_000);

        StatResult result = Statx.StatPath(path, followLinks: false, out FileStat stat);

        Assert.Equal(StatResult.Ok, result);
        Assert.Equal(FileKind.Other, stat.Kind);
        Assert.Equal(10_000, stat.LogicalBytes);
        Assert.True(stat.AllocatedBytes >= 10_000 && stat.AllocatedBytes % 512 == 0);
        Assert.True(stat.HasIdentity);
        Assert.Equal(1u, stat.LinkCount);
    }

    [LinuxFact]
    public void Statx_does_not_follow_links_unless_asked_and_reports_missing_paths()
    {
        string target = WriteFile("t.bin", 4000);
        string link = Path.Combine(_root, "l");
        File.CreateSymbolicLink(link, target);

        Statx.StatPath(link, followLinks: false, out FileStat own);
        Statx.StatPath(link, followLinks: true, out FileStat followed);

        Assert.Equal(FileKind.Symlink, own.Kind);
        Assert.Equal(target.Length, own.LogicalBytes);
        Assert.Equal(FileKind.Other, followed.Kind);
        Assert.Equal(4000, followed.LogicalBytes);
        Assert.Equal(StatResult.NotFound, Statx.StatPath(Path.Combine(_root, "nope"), false, out _));
    }

    [LinuxFact]
    public void Fallback_stat_sizes_files_and_recognises_links_and_directories()
    {
        string file = WriteFile("f.bin", 700);
        string link = Path.Combine(_root, "l");
        File.CreateSymbolicLink(link, file);

        Assert.Equal(new FileStat(FileKind.Other, 700, 700, false, 0, 0, 1), Statx.FallbackStat(file));
        Assert.Equal(FileKind.Symlink, Statx.FallbackStat(link)!.Value.Kind);
        Assert.Equal(FileKind.Directory, Statx.FallbackStat(_root)!.Value.Kind);
        Assert.Null(Statx.FallbackStat(Path.Combine(_root, "missing")));
    }

    [LinuxFact]
    public async Task Counts_every_file_and_rolls_up_sizes()
    {
        WriteFile("a.txt", 1000);
        WriteFile(Path.Combine("docs", "b.txt"), 5000);
        WriteFile(Path.Combine("docs", "deep", "c.txt"), 300);

        StorageTree tree = await ScanAsync();

        Assert.Equal(3, tree.FileCount);
        Assert.Equal(6300, tree.Root.LogicalSize);
        Assert.True(tree.Root.SizeOnDisk >= tree.Root.LogicalSize);
        StorageNode docs = tree.Root.GetChildren().Single(n => n.Name == "docs");
        Assert.Equal(5300, docs.LogicalSize);
        Assert.Equal(Path.Combine(_root, "docs"), docs.FullPath);
    }

    [LinuxFact]
    public async Task Counts_an_empty_directory_and_an_empty_file()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        WriteFile("zero", 0);

        StorageTree tree = await ScanAsync();

        Assert.Equal(1, tree.FileCount);
        Assert.Contains(tree.Root.GetChildren(), n => n.Name == "empty");
        Assert.Equal(0, tree.Root.LogicalSize);
    }

    [LinuxFact]
    public async Task Applies_the_classifier_to_folders_and_inherits_it_below()
    {
        WriteFile(Path.Combine("games", "x", "big.bin"), 2000);
        WriteFile(Path.Combine("other", "small.bin"), 10);

        StorageTree tree = await ScanAsync(new NameClassifier("games", StorageCategory.Games));

        var area = Assert.Single(tree.GetCategoryAreas(StorageCategory.Games));
        Assert.Equal("games", area.Node.Name);
        Assert.Equal(2000, area.Node.LogicalSize);
        Assert.Equal(tree.GetCategoryBytes(StorageCategory.Games), area.Bytes);
        Assert.True(tree.GetCategoryBytes(StorageCategory.Other) > 0);
    }

    [LinuxFact]
    public async Task Classifies_files_in_the_scan_root_only()
    {
        WriteFile("swapfile", 4096);
        WriteFile(Path.Combine("sub", "swapfile"), 4096);

        StorageTree tree = await ScanAsync(new NameClassifier("swapfile", StorageCategory.HiddenSystem));

        Assert.Equal(4096, tree.GetCategoryBytes(StorageCategory.HiddenSystem));
    }

    [LinuxFact]
    public async Task Counts_sparse_files_by_their_stored_size()
    {
        string path = Path.Combine(_root, "disk.img");
        using (var stream = new FileStream(path, FileMode.Create))
        {
            stream.SetLength(1L << 30);
        }

        StorageTree tree = await ScanAsync();

        StorageNode disk = Assert.Single(tree.Root.GetChildren());
        Assert.Equal(1L << 30, disk.LogicalSize);
        Assert.True(disk.SizeOnDisk < 1024 * 1024, $"Expected almost nothing stored, got {disk.SizeOnDisk}.");
    }

    [LinuxFact]
    public async Task Counts_a_hard_linked_file_once()
    {
        string original = WriteFile(Path.Combine("a", "data.bin"), 50_000);
        Directory.CreateDirectory(Path.Combine(_root, "b"));
        Run("/bin/ln", original, Path.Combine(_root, "b", "alias.bin"));
        Run("/bin/ln", original, Path.Combine(_root, "alias2.bin"));

        StorageTree tree = await ScanAsync();

        Assert.Equal(1, tree.FileCount);
        Assert.Equal(50_000, tree.Root.LogicalSize);
    }

    [LinuxFact]
    public async Task Does_not_follow_symlinks_and_counts_the_link_itself()
    {
        WriteFile(Path.Combine("real", "data.bin"), 8000);
        File.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "real"));
        File.CreateSymbolicLink(Path.Combine(_root, "loop"), _root);
        File.CreateSymbolicLink(Path.Combine(_root, "dangling"), Path.Combine(_root, "missing"));

        StorageTree tree = await ScanAsync();

        Assert.Equal(4, tree.FileCount); // data.bin + three links
        Assert.Equal(1, tree.Root.GetChildren().Count(n => n.Name == "real"));
        Assert.All(tree.Root.GetChildren().Where(n => n.Name is "link" or "loop" or "dangling"), n =>
        {
            Assert.False(n.IsDirectory);
            Assert.True(n.LogicalSize < 100);
        });
        Assert.True(tree.Root.LogicalSize < 8000 + 300);
    }

    [LinuxFact]
    public async Task Marks_unreadable_folders_as_locked_and_keeps_going()
    {
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "root ignores permission bits.");
        WriteFile(Path.Combine("secret", "hidden.bin"), 100);
        WriteFile("visible.bin", 100);
        Restrict(Path.Combine(_root, "secret"), UnixFileMode.None);

        StorageTree tree = await ScanAsync();

        StorageNode secret = tree.Root.GetChildren().Single(n => n.Name == "secret");
        Assert.True(secret.IsLocked);
        Assert.Equal(1, tree.FileCount);
    }

    [LinuxFact]
    public async Task Marks_a_listable_but_not_searchable_folder_children_as_locked()
    {
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "root ignores permission bits.");
        Directory.CreateDirectory(Path.Combine(_root, "outer", "inner"));
        WriteFile(Path.Combine("outer", "file.bin"), 100);
        Restrict(Path.Combine(_root, "outer"), UnixFileMode.UserRead);

        StorageTree tree = await ScanAsync();

        StorageNode outer = tree.Root.GetChildren().Single(n => n.Name == "outer");
        Assert.False(outer.IsLocked);
        Assert.True(outer.GetChildren().Single(n => n.Name == "inner").IsLocked);
        Assert.Equal(0, tree.FileCount);
    }

    [LinuxFact]
    public async Task Counts_files_whose_names_are_not_valid_utf8()
    {
        Run("/bin/sh", "-c", "printf x > \"$1/$(printf '\\377bad')\"", "sh", _root);
        WriteFile("good.bin", 10);

        StorageTree tree = await ScanAsync();

        Assert.Equal(2, tree.FileCount);
        Assert.Equal(11, tree.Root.LogicalSize);
    }

    [LinuxFact]
    public async Task Scans_paths_longer_than_a_thousand_characters()
    {
        string directory = _root;
        for (int i = 0; i < 6; i++)
        {
            directory = Path.Combine(directory, new string((char)('a' + i), 200));
        }

        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "deep.bin"), new byte[123]);

        StorageTree tree = await ScanAsync();

        Assert.Equal(1, tree.FileCount);
        Assert.Equal(123, tree.Root.LogicalSize);
    }

    [LinuxFact]
    public async Task Handles_a_root_given_with_a_trailing_slash_and_a_missing_root()
    {
        WriteFile("a.bin", 5);

        StorageTree tree = await new PosixDirectoryScanner(2).ScanAsync(_root + "/", new NameClassifier("x", StorageCategory.Other), null, TestContext.Current.CancellationToken);
        StorageTree missing = await new PosixDirectoryScanner(2).ScanAsync(Path.Combine(_root, "absent"), new NameClassifier("x", StorageCategory.Other), null, TestContext.Current.CancellationToken);

        Assert.Equal(5, tree.Root.LogicalSize);
        Assert.Equal(0, missing.FileCount);
    }

    [LinuxFact]
    public async Task Never_crosses_into_another_file_system()
    {
        // Mounting needs privileges, so use one that is always there: /dev is devtmpfs and holds tmpfs mounts (/dev/shm, /dev/pts).
        StorageTree tree = await new PosixDirectoryScanner(2).ScanAsync("/dev", new NameClassifier("x", StorageCategory.Other), null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(tree.Root.GetChildren(), n => n.Name is "shm" or "pts");
    }

    [LinuxFact]
    public async Task Reports_progress_with_category_totals_at_the_interval()
    {
        for (int i = 0; i < 8; i++)
        {
            WriteFile(Path.Combine($"d{i}", "a.bin"), 100);
        }

        var reports = new List<ScanProgress>();
        var scanner = new PosixDirectoryScanner(workerCount: 1, progressInterval: TimeSpan.FromMilliseconds(40));

        await scanner.ScanAsync(_root, new SlowClassifier(TimeSpan.FromMilliseconds(40)), new SyncProgress(reports), TestContext.Current.CancellationToken);

        Assert.True(reports.Count >= 3, $"Expected periodic reports, got {reports.Count}.");
        ScanProgress last = reports[^1];
        Assert.Equal(8, last.FileCount);
        Assert.Equal(StorageCategories.Count, last.CategoryBytes.Count);
        Assert.True(reports.Zip(reports.Skip(1)).All(pair => pair.First.FileCount <= pair.Second.FileCount));
    }

    [LinuxFact]
    public async Task Stops_when_cancelled_before_starting()
    {
        WriteFile("a.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PosixDirectoryScanner().ScanAsync(_root, new NameClassifier("x", StorageCategory.Other), null, cts.Token));
    }

    [LinuxFact]
    public async Task Stops_promptly_when_cancelled_mid_scan()
    {
        for (int i = 0; i < 300; i++)
        {
            Directory.CreateDirectory(Path.Combine(_root, $"d{i}"));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var stopwatch = Stopwatch.StartNew();

        // Uncancelled this takes 300 * 30 ms on one worker, roughly nine seconds.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PosixDirectoryScanner(workerCount: 1).ScanAsync(_root, new SlowClassifier(TimeSpan.FromMilliseconds(30)), null, cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Took {stopwatch.Elapsed}.");
    }

    private Task<StorageTree> ScanAsync(IPathClassifier? classifier = null) =>
        new PosixDirectoryScanner(workerCount: 4).ScanAsync(
            _root,
            classifier ?? new NameClassifier("none", StorageCategory.Other),
            null,
            TestContext.Current.CancellationToken);

    private string WriteFile(string relativePath, int length)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private void Restrict(string directory, UnixFileMode mode)
    {
        _restricted.Add(directory);
        File.SetUnixFileMode(directory, mode);
    }

    private static void Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class NameClassifier(string name, StorageCategory category) : IPathClassifier
    {
        public StorageCategory Classify(string fullPath, StorageCategory inherited) =>
            string.Equals(Path.GetFileName(fullPath), name, StringComparison.Ordinal) ? category : inherited;
    }

    private sealed class SlowClassifier(TimeSpan delay) : IPathClassifier
    {
        public StorageCategory Classify(string fullPath, StorageCategory inherited)
        {
            Thread.Sleep(delay);
            return inherited;
        }
    }

    private sealed class SyncProgress(List<ScanProgress> reports) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }
}
