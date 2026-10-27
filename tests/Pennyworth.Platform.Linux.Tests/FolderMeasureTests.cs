using System.Runtime.Versioning;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class FolderMeasureTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static FoldersRequest Request(params string[] paths) => new(HelperProtocol.Version, paths);

    private static FoldersResponse Measure(params string[] paths) =>
        FolderMeasure.Run(Request(paths), TestContext.Current.CancellationToken);

    private static FileStat StatOf(string path)
    {
        Assert.Equal(StatResult.Ok, Statx.StatPath(path, followLinks: false, out FileStat stat));
        return stat;
    }

    [LinuxFact]
    public void Counts_allocated_and_logical_bytes_like_the_scan()
    {
        string a = _tree.Write("f/a.bin", 5000);
        string b = _tree.Write("f/sub/deeper/b.bin", 123);

        FolderSize folder = Assert.Single(Measure(_tree.PathOf("f")).Folders);

        Assert.Equal(_tree.PathOf("f"), folder.Path);
        Assert.Equal(5123, folder.LogicalSize);
        Assert.Equal(StatOf(a).AllocatedBytes + StatOf(b).AllocatedBytes, folder.SizeOnDisk);
        Assert.Equal(2, folder.Files);
        Assert.True(folder.Complete);
    }

    [LinuxFact]
    public void A_sparse_file_costs_what_it_stores_not_what_it_claims()
    {
        string path = _tree.PathOf("sparse/hole.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Create))
        {
            stream.SetLength(64L * 1024 * 1024);
        }

        FolderSize folder = Assert.Single(Measure(_tree.PathOf("sparse")).Folders);

        Assert.Equal(64L * 1024 * 1024, folder.LogicalSize);
        Assert.Equal(StatOf(path).AllocatedBytes, folder.SizeOnDisk);
        Assert.True(folder.SizeOnDisk < folder.LogicalSize);
    }

    [LinuxFact]
    public void Hard_links_are_counted_once_in_a_folder_and_across_the_request()
    {
        string original = _tree.Write("one/data.bin", 4096);
        TempTree.Run("/bin/ln", original, _tree.PathOf("one/alias.bin"));
        Directory.CreateDirectory(_tree.PathOf("two"));
        TempTree.Run("/bin/ln", original, _tree.PathOf("two/other-alias.bin"));
        _tree.Write("two/own.bin", 100);

        FoldersResponse response = Measure(_tree.PathOf("one"), _tree.PathOf("two"));

        Assert.Equal(2, response.Folders.Count);
        Assert.Equal(1, response.Folders[0].Files);
        Assert.Equal(4096, response.Folders[0].LogicalSize);
        Assert.Equal(1, response.Folders[1].Files); // Only its own file: the linked data was counted in the first folder.
        Assert.Equal(100, response.Folders[1].LogicalSize);
    }

    [LinuxFact]
    public void A_symlink_is_a_small_file_and_is_never_followed()
    {
        string big = _tree.Write("outside/big.bin", 100_000);
        _tree.Write("outside/dir/inside.bin", 100_000);
        Directory.CreateDirectory(_tree.PathOf("f"));
        string fileLink = _tree.PathOf("f/file-link");
        string directoryLink = _tree.PathOf("f/dir-link");
        File.CreateSymbolicLink(fileLink, big);
        Directory.CreateSymbolicLink(directoryLink, _tree.PathOf("outside/dir"));

        FolderSize folder = Assert.Single(Measure(_tree.PathOf("f")).Folders);

        Assert.Equal(2, folder.Files);
        Assert.Equal(StatOf(fileLink).LogicalBytes + StatOf(directoryLink).LogicalBytes, folder.LogicalSize);
        Assert.Equal(StatOf(fileLink).AllocatedBytes + StatOf(directoryLink).AllocatedBytes, folder.SizeOnDisk);
        Assert.True(folder.Complete);
    }

    [LinuxFact]
    public void A_file_name_that_is_not_valid_utf8_is_counted()
    {
        _tree.Write("f/good.bin", 10);
        TempTree.Run("/bin/sh", "-c", "printf xxxxx > \"$1/f/$(printf '\\377bad')\" && mkdir \"$1/f/$(printf 'd\\377ir')\" && printf yyy > \"$1/f/$(printf 'd\\377ir')/inner\"", "sh", _tree.Root);

        FolderSize folder = Assert.Single(Measure(_tree.PathOf("f")).Folders);

        Assert.Equal(3, folder.Files);
        Assert.Equal(10 + 5 + 3, folder.LogicalSize);
        Assert.True(folder.Complete);
    }

    [LinuxFact]
    public void The_walk_never_leaves_the_folders_device()
    {
        _tree.Write("f/own.bin", 10);
        _tree.Write("f/mnt/foreign.bin", 1000);
        ulong mountInode = StatOf(_tree.PathOf("f/mnt")).Inode;

        // The test cannot mount anything: it makes one folder look like another device.
        FoldersResponse response = FolderMeasure.Run(
            Request(_tree.PathOf("f")),
            MeasureLimits.Production,
            (device, inode) => inode == mountInode ? device ^ 1 : device,
            TestContext.Current.CancellationToken);

        FolderSize folder = Assert.Single(response.Folders);
        Assert.Equal(1, folder.Files);
        Assert.Equal(10, folder.LogicalSize);
        Assert.True(folder.Complete);
    }

    [LinuxFact]
    public void A_directory_that_cannot_be_listed_makes_the_folder_incomplete()
    {
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "Root ignores permission bits.");
        _tree.Write("f/own.bin", 10);
        string locked = _tree.Write("f/locked/hidden.bin", 1000);
        string lockedDirectory = Path.GetDirectoryName(locked)!;
        File.SetUnixFileMode(lockedDirectory, UnixFileMode.None);
        try
        {
            FolderSize folder = Assert.Single(Measure(_tree.PathOf("f")).Folders);

            Assert.False(folder.Complete);
            Assert.Equal(1, folder.Files);
            Assert.Equal(10, folder.LogicalSize);
        }
        finally
        {
            File.SetUnixFileMode(lockedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [LinuxFact]
    public void The_entry_cap_stops_the_walk_and_says_so()
    {
        for (int i = 0; i < 6; i++)
        {
            _tree.Write($"f/{i}.bin", 10);
        }

        FoldersResponse response = FolderMeasure.Run(
            Request(_tree.PathOf("f"), _tree.PathOf("f")), new MeasureLimits(MaxEntries: 3, MaxDepth: 10), null, TestContext.Current.CancellationToken);

        FolderSize folder = Assert.Single(response.Folders); // The second folder was never reached.
        Assert.False(folder.Complete);
        Assert.True(folder.Files <= 3);
    }

    [LinuxFact]
    public void The_depth_cap_leaves_the_folder_incomplete()
    {
        _tree.Write("f/a/b/c/deep.bin", 10);
        _tree.Write("f/a/top.bin", 10);

        FoldersResponse response = FolderMeasure.Run(
            Request(_tree.PathOf("f")), new MeasureLimits(MaxEntries: 1000, MaxDepth: 2), null, TestContext.Current.CancellationToken);

        FolderSize folder = Assert.Single(response.Folders);
        Assert.False(folder.Complete);
        Assert.Equal(10, folder.LogicalSize);
    }

    [LinuxFact]
    public void A_cancelled_run_reports_nothing_as_measured()
    {
        _tree.Write("f/a.bin", 10);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        FoldersResponse response = FolderMeasure.Run(Request(_tree.PathOf("f")), cancelled.Token);

        Assert.Empty(response.Folders);
    }

    [LinuxFact]
    public void Folders_in_kernel_file_systems_are_left_out()
    {
        _tree.Mkdir("f");

        FoldersResponse response = Measure("/proc", "/proc/self", "/sys", "/sys/kernel", "/dev", "/dev/shm", "/run", "/run/user", _tree.PathOf("f"));

        FolderSize folder = Assert.Single(response.Folders);
        Assert.Equal(_tree.PathOf("f"), folder.Path);
    }

    [LinuxFact]
    public void A_folder_that_is_a_symlink_or_lies_behind_one_is_left_out()
    {
        _tree.Write("real/sub/a.bin", 10);
        string link = _tree.PathOf("link");
        Directory.CreateSymbolicLink(link, _tree.PathOf("real"));

        FoldersResponse response = Measure(link, link + "/sub", _tree.PathOf("real/sub"));

        FolderSize folder = Assert.Single(response.Folders);
        Assert.Equal(_tree.PathOf("real/sub"), folder.Path);
    }

    [LinuxTheory]
    [InlineData("relative/path")]
    [InlineData("")]
    [InlineData("~")]
    [InlineData("TREE//f")]
    [InlineData("TREE/f/")]
    [InlineData("TREE/f/.")]
    [InlineData("TREE/f/../f")]
    [InlineData("TREE/f/./x")]
    [InlineData("TREE/missing")]
    [InlineData("TREE/file.bin")]
    public void A_path_that_is_not_an_absolute_normalized_existing_folder_is_left_out(string path)
    {
        _tree.Mkdir("f");
        _tree.Write("file.bin");

        FoldersResponse response = Measure(path.Replace("TREE", _tree.Root, StringComparison.Ordinal));

        Assert.Empty(response.Folders);
    }

    [LinuxFact]
    public void A_nul_in_a_path_is_left_out()
    {
        _tree.Mkdir("f");

        Assert.Empty(Measure(_tree.PathOf("f") + "\0/x").Folders);
    }

    [LinuxFact]
    public void Too_many_folders_and_a_wrong_version_are_rejected()
    {
        string[] many = Enumerable.Repeat(_tree.Root, HelperProtocol.MaxFolders + 1).ToArray();

        Assert.Equal(HelperExit.TooLarge, Assert.Throws<HelperException>(() => FolderMeasure.Run(Request(many), TestContext.Current.CancellationToken)).ExitCode);
        Assert.Equal(
            HelperExit.Usage,
            Assert.Throws<HelperException>(() => FolderMeasure.Run(new FoldersRequest(HelperProtocol.Version + 1, []), TestContext.Current.CancellationToken)).ExitCode);
    }

    [LinuxFact]
    public void The_maximum_number_of_folders_is_accepted()
    {
        _tree.Mkdir("f");
        string[] paths = Enumerable.Repeat(_tree.PathOf("f"), HelperProtocol.MaxFolders).ToArray();

        Assert.Equal(HelperProtocol.MaxFolders, Measure(paths).Folders.Count);
    }

    [LinuxFact]
    public void Answers_come_in_request_order_with_only_the_accepted_folders()
    {
        _tree.Write("b/x.bin", 20);
        _tree.Write("a/y.bin", 10);

        FoldersResponse response = Measure(_tree.PathOf("b"), "/proc", _tree.PathOf("a"));

        Assert.Equal([_tree.PathOf("b"), _tree.PathOf("a")], response.Folders.Select(f => f.Path).ToArray());
        Assert.Equal([20L, 10L], response.Folders.Select(f => f.LogicalSize).ToArray());
        Assert.Equal(HelperProtocol.Version, response.Version);
    }
}
