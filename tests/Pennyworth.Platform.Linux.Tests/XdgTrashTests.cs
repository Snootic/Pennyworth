using System.Runtime.Versioning;
using Pennyworth.Cleanup;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public class XdgTrashParsingTests
{
    [Fact]
    public void Decodes_percent_escapes_as_utf8()
    {
        var info = TrashInfo.Parse("[Trash Info]\nPath=/home/ana/Meus%20Docs/caf%C3%A9%25.txt\nDeletionDate=2024-03-05T07:08:09\n");

        Assert.Equal("/home/ana/Meus Docs/café%.txt", info?.Path);
        Assert.Equal(new DateTime(2024, 3, 5, 7, 8, 9), info?.DeletedLocal);
    }

    [Theory]
    [InlineData("100%", "100%")]
    [InlineData("a%zzb", "a%zzb")]
    [InlineData("a%2", "a%2")]
    [InlineData("%2Fx", "/x")]
    public void Keeps_a_malformed_escape_literally(string raw, string expected) =>
        Assert.Equal(expected, TrashInfo.PercentDecode(raw));

    [Fact]
    public void Ignores_other_groups_comments_and_a_second_value()
    {
        var info = TrashInfo.Parse("[Other]\nPath=/wrong\n[Trash Info]\n# Path=/comment\r\nPath=/right\r\nPath=/second\r\nDeletionDate=2024-01-01T00:00:00\r\n[Later]\nPath=/also-wrong\n");

        Assert.Equal("/right", info?.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Path=/no-group")]
    [InlineData("[Trash Info]\nDeletionDate=2024-01-01T00:00:00")]
    [InlineData("[Trash Info]\nPath=")]
    public void Rejects_a_sidecar_without_a_path(string text) => Assert.Null(TrashInfo.Parse(text));

    [Fact]
    public void A_bad_date_leaves_the_time_unknown_but_keeps_the_path()
    {
        var info = TrashInfo.Parse("[Trash Info]\nPath=/x\nDeletionDate=yesterday\n");

        Assert.Equal("/x", info?.Path);
        Assert.Null(info?.DeletedLocal);
    }
}

[SupportedOSPlatform("linux")]
public sealed class XdgTrashTests : IDisposable
{
    private const int Uid = 4242;

    private readonly string _root = Directory.CreateTempSubdirectory("pw-trash-").FullName;
    private readonly string _data;
    private readonly string _volume;
    private readonly XdgTrash _trash;

    public XdgTrashTests()
    {
        _data = Path.Combine(_root, "data");
        _volume = Path.Combine(_root, "vol");
        Directory.CreateDirectory(_volume);
        var paths = new XdgPaths(name => name switch { "HOME" => _root, "XDG_DATA_HOME" => _data, _ => null });
        _trash = new XdgTrash(paths, () => [_volume], Uid);
    }

    public void Dispose()
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private string Home => Path.Combine(_data, "Trash");

    private string Add(string trash, string name, string content, string infoText)
    {
        Directory.CreateDirectory(Path.Combine(trash, "files"));
        Directory.CreateDirectory(Path.Combine(trash, "info"));
        string payload = Path.Combine(trash, "files", name);
        File.WriteAllText(payload, content);
        File.WriteAllText(Path.Combine(trash, "info", name + ".trashinfo"), infoText);
        return payload;
    }

    private static string Info(string path, string date = "2024-03-05T07:08:09") => $"[Trash Info]\nPath={path}\nDeletionDate={date}\n";

    private void MakeShared(bool sticky)
    {
        string shared = Path.Combine(_volume, ".Trash");
        Directory.CreateDirectory(shared);
        File.SetUnixFileMode(shared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute
            | (sticky ? UnixFileMode.StickyBit : 0));
    }

    [Fact]
    public void Lists_the_home_trash_with_decoded_original_and_utc_time()
    {
        string payload = Add(Home, "a b.txt", "12345", Info("/home/ana/a%20b.txt"));

        CleanupFile item = Assert.Single(_trash.List());

        Assert.Equal(payload, item.Path);
        Assert.Equal(5, item.Size);
        Assert.Equal("/home/ana/a b.txt", item.OriginalPath);
        Assert.Equal(Path.Combine(Home, "info", "a b.txt.trashinfo"), item.CompanionPath);
        Assert.Equal(DateTime.SpecifyKind(new DateTime(2024, 3, 5, 7, 8, 9), DateTimeKind.Local).ToUniversalTime(), item.ChangedUtc);
        Assert.Equal(DateTimeKind.Utc, item.ChangedUtc.Kind);
    }

    [Fact]
    public void Reads_a_per_volume_trash_and_resolves_a_relative_path_against_the_topdir()
    {
        Add(Path.Combine(_volume, ".Trash-" + Uid), "r.txt", "x", Info("docs/r.txt"));
        Add(Path.Combine(_volume, ".Trash-" + Uid), "abs.txt", "x", Info("/elsewhere/abs.txt"));

        var items = _trash.List().ToDictionary(i => Path.GetFileName(i.Path));

        Assert.Equal(Path.Combine(_volume, "docs", "r.txt"), items["r.txt"].OriginalPath);
        Assert.Equal("/elsewhere/abs.txt", items["abs.txt"].OriginalPath);
    }

    [Fact]
    public void Uses_the_shared_trash_only_with_the_sticky_bit()
    {
        MakeShared(sticky: false);
        Add(Path.Combine(_volume, ".Trash", Uid.ToString()), "x.txt", "x", Info("x.txt"));
        Assert.Empty(_trash.List());

        MakeShared(sticky: true);
        Assert.Single(_trash.List());
    }

    [Fact]
    public void Refuses_a_shared_trash_that_is_a_symlink()
    {
        string real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        File.SetUnixFileMode(real, UnixFileMode.StickyBit | (UnixFileMode)0b111_111_111);
        Add(Path.Combine(real, Uid.ToString()), "x.txt", "x", Info("x.txt"));
        Directory.CreateSymbolicLink(Path.Combine(_volume, ".Trash"), real);

        Assert.Empty(_trash.List());
    }

    [Fact]
    public void Skips_items_missing_their_sidecar_or_their_payload()
    {
        Add(Home, "ok.txt", "x", Info("/ok"));
        File.WriteAllText(Path.Combine(Home, "files", "no-sidecar.txt"), "x");
        File.WriteAllText(Path.Combine(Home, "info", "no-payload.txt.trashinfo"), Info("/gone"));

        Assert.Equal(["ok.txt"], _trash.List().Select(i => Path.GetFileName(i.Path)));
    }

    [Fact]
    public void Sizes_a_directory_recursively_without_following_links_and_sorts_largest_first()
    {
        string small = Add(Home, "small", "1", Info("/small"));
        string dir = Path.Combine(Home, "files", "tree");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a"), new string('a', 100));
        File.WriteAllText(Path.Combine(dir, "sub", "b"), new string('b', 50));
        string outside = Path.Combine(_root, "outside.bin");
        File.WriteAllText(outside, new string('z', 1000));
        File.CreateSymbolicLink(Path.Combine(dir, "link"), outside);
        Directory.CreateSymbolicLink(Path.Combine(dir, "dirlink"), _root);
        File.WriteAllText(Path.Combine(Home, "info", "tree.trashinfo"), Info("/tree"));

        var items = _trash.List();

        Assert.Equal(["tree", "small"], items.Select(i => Path.GetFileName(i.Path)));
        Assert.Equal(150, items[0].Size);
    }

    [Fact]
    public void Delete_removes_payload_and_sidecar_and_nothing_else()
    {
        string payload = Add(Home, "a.txt", "x", Info("/a"));
        Add(Home, "b.txt", "x", Info("/b"));
        File.WriteAllText(Path.Combine(Home, "directorysizes"), "keep");
        string victim = Path.Combine(_root, "victim.txt");
        File.WriteAllText(victim, "mine");
        CleanupFile item = _trash.List().Single(i => i.Path == payload);

        Assert.True(_trash.Delete(item));

        Assert.False(File.Exists(payload));
        Assert.False(File.Exists(item.CompanionPath));
        Assert.True(File.Exists(Path.Combine(Home, "files", "b.txt")));
        Assert.True(File.Exists(Path.Combine(Home, "info", "b.txt.trashinfo")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Home, "directorysizes")));
        Assert.True(File.Exists(victim));
        Assert.False(_trash.Delete(item), "already gone");
    }

    [Fact]
    public void Delete_removes_a_directory_with_read_only_folders_and_links_without_following_them()
    {
        string dir = Path.Combine(Home, "files", "tree");
        string locked = Path.Combine(dir, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "f"), "x");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious"), "x");
        Directory.CreateSymbolicLink(Path.Combine(dir, "link"), outside);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(Home, "info"));
        File.WriteAllText(Path.Combine(Home, "info", "tree.trashinfo"), Info("/tree"));

        Assert.True(_trash.Delete(_trash.List().Single()));

        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(outside, "precious")));
        Assert.Empty(_trash.List());
    }

    [Fact]
    public void Delete_unlinks_a_symlink_payload_not_its_target()
    {
        string target = Path.Combine(_root, "target.txt");
        File.WriteAllText(target, "x");
        Directory.CreateDirectory(Path.Combine(Home, "files"));
        Directory.CreateDirectory(Path.Combine(Home, "info"));
        File.CreateSymbolicLink(Path.Combine(Home, "files", "l"), target);
        File.WriteAllText(Path.Combine(Home, "info", "l.trashinfo"), Info("/l"));

        Assert.True(_trash.Delete(_trash.List().Single()));

        Assert.True(File.Exists(target));
        Assert.Empty(_trash.List());
    }

    [Fact]
    public void Delete_refuses_a_path_outside_a_trash_files_folder()
    {
        Add(Home, "a.txt", "x", Info("/a"));
        string stray = Path.Combine(_root, "files", "stray.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllText(stray, "x");
        string inInfo = Path.Combine(Home, "info", "a.txt.trashinfo");
        string inSubfolder = Path.Combine(Home, "files", "sub", "deep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(inSubfolder)!);
        File.WriteAllText(inSubfolder, "x");

        Assert.False(_trash.Delete(new CleanupFile(stray, 1, DateTime.MinValue)));
        Assert.False(_trash.Delete(new CleanupFile(inInfo, 1, DateTime.MinValue)));
        Assert.False(_trash.Delete(new CleanupFile(inSubfolder, 1, DateTime.MinValue)));
        Assert.False(_trash.Delete(new CleanupFile(Path.Combine(Home, "files", "..", "info", "a.txt.trashinfo"), 1, DateTime.MinValue)));
        Assert.False(_trash.Delete(new CleanupFile(Path.Combine(Home, "files"), 1, DateTime.MinValue)));
        Assert.True(File.Exists(stray));
        Assert.True(File.Exists(inInfo));
        Assert.True(File.Exists(inSubfolder));
    }

    [Fact]
    public void Delete_refuses_a_sidecar_that_does_not_belong_to_the_payload()
    {
        string payload = Add(Home, "a.txt", "x", Info("/a"));
        string other = Path.Combine(_root, "other.trashinfo");
        File.WriteAllText(other, "x");

        Assert.False(_trash.Delete(new CleanupFile(payload, 1, DateTime.MinValue, "/a", other)));

        Assert.True(File.Exists(payload));
        Assert.True(File.Exists(other));
    }

    [LinuxFact]
    public void Round_trip_in_a_temp_data_home()
    {
        string payload = Add(Home, "t.txt", "hello", Info("/tmp/t.txt"));

        CleanupFile item = Assert.Single(_trash.List());
        Assert.True(_trash.Delete(item));

        Assert.False(File.Exists(payload));
        Assert.Empty(_trash.List());
    }

    [LinuxFact]
    public void Lists_the_real_trash_without_throwing()
    {
        var trash = new XdgTrash(new XdgPaths(), () => [], XdgTrash.CurrentUid());

        Assert.NotNull(trash.List());
    }

    [LinuxFact]
    public void The_current_uid_is_the_one_the_shell_reports()
    {
        string? shell = ProcessProbe.Run("/usr/bin/id", ["-u"], TimeSpan.FromSeconds(10));

        Assert.Equal(shell?.Trim(), XdgTrash.CurrentUid().ToString());
    }
}
