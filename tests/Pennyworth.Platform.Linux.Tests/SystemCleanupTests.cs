using System.Runtime.Versioning;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class SystemCleanupTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string Root => _tree.PathOf("root");

    private CleanupPolicy Policy(
        Func<ulong, ulong, ulong>? deviceOverride = null,
        uint? owner = null,
        Func<string, TrustVerdict>? verify = null,
        Func<string, string, bool>? isKept = null) =>
        new(verify ?? (_ => TrustVerdict.Trusted), owner ?? _tree.Owner, deviceOverride, isKept ?? ((_, _) => false));

    /// <summary>A preview entry for an existing entry, as the app would have listed it just now.</summary>
    private CleanFile Listed(string relative, long size = 10, string? root = null) =>
        new(root ?? Root, relative, size, TempTree.Ticks(Path.Combine(root ?? Root, relative)));

    private static CleanRequest Request(params CleanFile[] files) => new(HelperProtocol.Version, files);

    private CleanResponse Clean(CleanRequest request, CleanupPolicy? policy = null, CancellationToken cancellationToken = default) =>
        SystemCleanup.Run([Root], request, policy ?? Policy(), cancellationToken);

    private string Seed(string relative, int length = 10) => _tree.Write(Path.Combine("root", relative), length);

    private static void AssertRefused(Action action) =>
        Assert.Equal(HelperExit.Refused, Assert.Throws<HelperException>(action).ExitCode);

    [LinuxFact]
    public void Deletes_the_listed_files_and_counts_them()
    {
        string a = Seed("a.rpm", 100);
        string b = Seed("repo/arch/b.rpm", 200);
        string kept = Seed("repo/arch/unlisted.rpm");

        CleanResponse response = Clean(Request(Listed("a.rpm", 100), Listed("repo/arch/b.rpm", 200)));

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 300, 2, 0), response);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(kept));
    }

    [LinuxFact]
    public void The_modification_time_matches_what_dotnet_reports()
    {
        string path = Seed("a.rpm");
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 3, 5, 6, 7, 8, DateTimeKind.Utc).AddTicks(1234500));

        Assert.Equal(File.GetLastWriteTimeUtc(path).Ticks, TempTree.Ticks(path));
    }

    [LinuxFact]
    public void A_file_changed_since_the_preview_is_kept()
    {
        string path = Seed("a.rpm");
        string same = Seed("b.rpm");
        CleanRequest request = Request(Listed("a.rpm"), Listed("b.rpm"));
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));

        CleanResponse response = Clean(request);

        Assert.Equal(1, response.Deleted);
        Assert.Equal(1, response.Skipped);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(same));
    }

    [LinuxFact]
    public void A_directory_in_the_preview_is_not_deleted()
    {
        string inner = Seed("dir/file.bin");
        string empty = _tree.Mkdir("root/emptydir");

        CleanResponse response = Clean(Request(Listed("dir"), Listed("emptydir")));

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 0, 0, 2), response);
        Assert.True(File.Exists(inner));
        Assert.True(Directory.Exists(empty));
    }

    [LinuxFact]
    public void A_vanished_file_is_counted_as_skipped()
    {
        string gone = Seed("gone.rpm");
        Seed("there.rpm");
        CleanRequest request = Request(Listed("gone.rpm"), Listed("there.rpm"), new CleanFile(Root, "never-existed.rpm", 5, 1));
        File.Delete(gone);

        CleanResponse response = Clean(request);

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 10, 1, 2), response);
    }

    [LinuxFact]
    public void A_symlink_entry_is_removed_as_a_link_and_its_target_is_untouched()
    {
        string outside = _tree.Write("outside/secret.txt", 50);
        string linkPath = Path.Combine(Root, "link.rpm");
        Directory.CreateDirectory(Root);
        File.CreateSymbolicLink(linkPath, outside);

        CleanResponse response = Clean(Request(Listed("link.rpm", 7)));

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 7, 1, 0), response);
        Assert.Null(new FileInfo(linkPath).LinkTarget);
        Assert.False(File.Exists(linkPath));
        Assert.True(File.Exists(outside));
    }

    [LinuxFact]
    public void A_symlinked_intermediate_directory_is_never_followed()
    {
        string outsideFile = _tree.Write("outside/x.rpm", 20);
        Directory.CreateDirectory(Root);
        Directory.CreateSymbolicLink(Path.Combine(Root, "dirlink"), _tree.PathOf("outside"));
        // The preview can name a file through the link: it would have the real file's time.
        var file = new CleanFile(Root, "dirlink/x.rpm", 20, TempTree.Ticks(outsideFile));

        CleanResponse response = Clean(Request(file));

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 0, 0, 1), response);
        Assert.True(File.Exists(outsideFile));
    }

    [LinuxFact]
    public void A_symlinked_directory_listed_as_a_file_is_not_followed_either()
    {
        string outsideFile = _tree.Write("outside/x.rpm", 20);
        Directory.CreateDirectory(Root);
        string link = Path.Combine(Root, "dirlink");
        Directory.CreateSymbolicLink(link, _tree.PathOf("outside"));

        // A symlink entry is removed as a link: the link goes, the directory it pointed at and its content stay.
        CleanResponse response = Clean(Request(Listed("dirlink")));

        Assert.Equal(1, response.Deleted);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(outsideFile));
    }

    [LinuxTheory]
    [InlineData("..")]
    [InlineData("../outside.rpm")]
    [InlineData("a/../b.rpm")]
    [InlineData("a/..")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    [InlineData("a//b.rpm")]
    [InlineData("a/")]
    [InlineData(".")]
    [InlineData("./a.rpm")]
    [InlineData("a/./b.rpm")]
    [InlineData("a\0b.rpm")]
    public void An_invalid_path_refuses_the_whole_request_before_anything_is_deleted(string path)
    {
        string good = Seed("good.rpm");
        _tree.Write("outside.rpm");
        CleanRequest request = Request(Listed("good.rpm"), new CleanFile(Root, path, 1, 1));

        AssertRefused(() => Clean(request));

        Assert.True(File.Exists(good));
        Assert.True(File.Exists(_tree.PathOf("outside.rpm")));
    }

    [LinuxFact]
    public void An_unknown_rule_is_refused()
    {
        Assert.Equal(
            HelperExit.Refused,
            Assert.Throws<HelperException>(() => SystemCleanup.Run("no-such-rule", Request(), TestContext.Current.CancellationToken)).ExitCode);
        Assert.Equal(
            HelperExit.Refused,
            Assert.Throws<HelperException>(() => SystemCleanup.Run("../system-crash-reports", Request(), TestContext.Current.CancellationToken)).ExitCode);
    }

    [LinuxTheory]
    [InlineData("/etc")]
    [InlineData("/var/crash/../../etc")]
    [InlineData("/var/crash/")]
    [InlineData("var/crash")]
    [InlineData("")]
    public void A_root_that_is_not_one_of_the_rules_folders_is_refused(string root)
    {
        // The real rule's folders with the real policy: validation happens before any folder is looked at.
        var request = Request(new CleanFile(root, "x", 1, 1));
        Assert.Equal(
            HelperExit.Refused,
            Assert.Throws<HelperException>(() => SystemCleanup.Run("system-crash-reports", request, TestContext.Current.CancellationToken)).ExitCode);
    }

    [LinuxFact]
    public void A_root_of_another_rule_is_refused()
    {
        var request = Request(new CleanFile("/var/cache/dnf", "x", 1, 1));
        Assert.Equal(
            HelperExit.Refused,
            Assert.Throws<HelperException>(() => SystemCleanup.Run("system-crash-reports", request, TestContext.Current.CancellationToken)).ExitCode);
    }

    [LinuxFact]
    public void A_wrong_protocol_version_and_too_many_files_are_rejected()
    {
        Assert.Equal(HelperExit.Usage, Assert.Throws<HelperException>(() => Clean(new CleanRequest(HelperProtocol.Version + 1, []))).ExitCode);

        CleanFile[] many = Enumerable.Repeat(new CleanFile(Root, "x", 1, 1), HelperProtocol.MaxCleanFiles + 1).ToArray();
        Assert.Equal(HelperExit.TooLarge, Assert.Throws<HelperException>(() => Clean(Request(many))).ExitCode);
    }

    [LinuxFact]
    public void A_negative_size_is_refused()
    {
        Seed("a.rpm");
        AssertRefused(() => Clean(Request(new CleanFile(Root, "a.rpm", -1, 1))));
    }

    [LinuxFact]
    public void A_folder_on_another_device_is_never_entered()
    {
        string across = Seed("mnt/inner.rpm");
        string beside = Seed("ok/inner.rpm");
        _ = FolderIdentity(Path.Combine(Root, "mnt"), out ulong mountInode);
        CleanRequest request = Request(Listed("mnt/inner.rpm"), Listed("ok/inner.rpm"));

        // The test cannot mount anything: it makes one folder look like another device.
        CleanResponse response = Clean(request, Policy(deviceOverride: (device, inode) => inode == mountInode ? device ^ 1 : device));

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 10, 1, 1), response);
        Assert.True(File.Exists(across));
        Assert.False(File.Exists(beside));
    }

    [LinuxFact]
    public void A_file_that_is_itself_a_mount_point_is_left_alone()
    {
        string mounted = Seed("mounted.rpm");
        ulong inode = FileInode(mounted);

        CleanResponse response = Clean(Request(Listed("mounted.rpm")), Policy(deviceOverride: (device, i) => i == inode ? device ^ 1 : device));

        Assert.Equal(1, response.Skipped);
        Assert.True(File.Exists(mounted));
    }

    [LinuxFact]
    public void A_root_that_is_a_symlink_is_refused()
    {
        string target = _tree.Mkdir("target");
        string victim = _tree.Write("target/a.rpm");
        File.CreateSymbolicLink(Root, target);
        CleanRequest request = new(HelperProtocol.Version, [new CleanFile(Root, "a.rpm", 10, TempTree.Ticks(victim))]);

        AssertRefused(() => Clean(request));
        AssertRefused(() => Clean(request, Policy(verify: path => PathTrust.CheckRootOwnedDirectory(path, PathTrust.Lstat))));

        Assert.True(File.Exists(victim));
    }

    [LinuxFact]
    public void A_root_not_owned_by_the_required_owner_is_refused()
    {
        string file = Seed("a.rpm");
        CleanRequest request = Request(Listed("a.rpm"));

        AssertRefused(() => Clean(request, Policy(owner: _tree.Owner + 1)));

        Assert.True(File.Exists(file));
    }

    [LinuxFact]
    public void A_root_the_chain_check_rejects_refuses_the_whole_run_before_any_deletion()
    {
        string first = Seed("a.rpm");
        string otherRoot = _tree.PathOf("other");
        string second = _tree.Write("other/b.rpm");
        CleanRequest request = Request(Listed("a.rpm"), new CleanFile(otherRoot, "b.rpm", 10, TempTree.Ticks(second)));

        Assert.Throws<HelperException>(() => SystemCleanup.Run(
            [Root, otherRoot], request, Policy(verify: path => path == otherRoot ? TrustVerdict.NotTrusted : TrustVerdict.Trusted), CancellationToken.None));

        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [LinuxFact]
    public void A_root_that_does_not_exist_is_skipped()
    {
        var request = Request(new CleanFile(Root, "a.rpm", 10, 1), new CleanFile(Root, "b/c.rpm", 10, 1));

        CleanResponse response = Clean(request, Policy(verify: _ => TrustVerdict.Missing));
        Assert.Equal(new CleanResponse(HelperProtocol.Version, 0, 0, 2), response);

        // Judged present but gone by the time it is opened.
        response = Clean(request);
        Assert.Equal(new CleanResponse(HelperProtocol.Version, 0, 0, 2), response);
    }

    [LinuxFact]
    public void The_production_policy_refuses_a_folder_an_ordinary_user_owns()
    {
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "Root owns the temporary folder.");
        string file = Seed("a.rpm");

        AssertRefused(() => Clean(Request(Listed("a.rpm")), CleanupPolicy.Production));

        Assert.True(File.Exists(file));
    }

    [LinuxFact]
    public void Directories_the_run_emptied_are_removed_deepest_first_and_nothing_else()
    {
        Seed("emptied/f.rpm");
        Seed("deep/x/y/f.rpm");
        Seed("shared/f.rpm");
        Seed("shared/other.rpm");
        Seed("nested/keep/g.rpm");
        Seed("nested/gone/h.rpm");
        _tree.Mkdir("root/preexisting-empty");
        _tree.Mkdir("root/emptied/preexisting-empty-child");

        CleanResponse response = Clean(Request(
            Listed("emptied/f.rpm"), Listed("deep/x/y/f.rpm"), Listed("shared/f.rpm"), Listed("nested/gone/h.rpm")));

        Assert.Equal(4, response.Deleted);
        Assert.False(Directory.Exists(_tree.PathOf("root/deep")), "a chain the run emptied goes, deepest first");
        Assert.True(Directory.Exists(_tree.PathOf("root/emptied")), "a directory that still holds an (empty) directory stays");
        Assert.True(Directory.Exists(_tree.PathOf("root/emptied/preexisting-empty-child")));
        Assert.True(Directory.Exists(_tree.PathOf("root/shared")), "a directory with something left stays");
        Assert.True(File.Exists(_tree.PathOf("root/shared/other.rpm")));
        Assert.False(Directory.Exists(_tree.PathOf("root/nested/gone")));
        Assert.True(File.Exists(_tree.PathOf("root/nested/keep/g.rpm")));
        Assert.True(Directory.Exists(_tree.PathOf("root/nested")));
        Assert.True(Directory.Exists(_tree.PathOf("root/preexisting-empty")), "an empty directory the run did not touch stays");
    }

    [LinuxFact]
    public void The_root_itself_is_never_removed_even_when_everything_in_it_goes()
    {
        Seed("a.rpm");
        Seed("sub/b.rpm");

        CleanResponse response = Clean(Request(Listed("a.rpm"), Listed("sub/b.rpm")));

        Assert.Equal(2, response.Deleted);
        Assert.True(Directory.Exists(Root));
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [LinuxFact]
    public void Kept_names_are_never_deleted_even_when_listed()
    {
        string lockFile = Seed("lock");
        string partial = Seed("partial/pkg.deb");
        string ordinary = Seed("pkg.deb");
        string lockLike = Seed("lockfile.deb");
        CleanRequest request = Request(Listed("lock"), Listed("partial/pkg.deb"), Listed("pkg.deb"), Listed("lockfile.deb"));

        CleanResponse response = Clean(request, Policy(isKept: (_, path) => SystemCleanupRules.IsKept("/var/cache/apt/archives", path)));

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 20, 2, 2), response);
        Assert.True(File.Exists(lockFile));
        Assert.True(File.Exists(partial));
        Assert.False(File.Exists(ordinary));
        Assert.False(File.Exists(lockLike));
    }

    [LinuxFact]
    public void A_cancelled_run_deletes_nothing_and_counts_the_rest_as_skipped()
    {
        string a = Seed("a.rpm");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        CleanResponse response = Clean(Request(Listed("a.rpm")), cancellationToken: cancelled.Token);

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 0, 0, 1), response);
        Assert.True(File.Exists(a));
    }

    [LinuxFact]
    public void A_file_listed_twice_is_deleted_once_and_counted_once()
    {
        Seed("a.rpm");
        CleanFile file = Listed("a.rpm");

        CleanResponse response = Clean(Request(file, file));

        Assert.Equal(new CleanResponse(HelperProtocol.Version, 10, 1, 1), response);
    }

    [LinuxFact]
    public void A_file_name_that_is_not_valid_utf8_is_never_matched_by_a_listed_path()
    {
        // A preview cannot carry such a name (JSON text), so the helper cannot be steered to it: it is left as it is.
        TempTree.Run("/bin/sh", "-c", "mkdir -p \"$1/root\" && printf x > \"$1/root/$(printf '\\377bad')\"", "sh", _tree.Root);

        CleanResponse response = Clean(Request(new CleanFile(Root, "\uFFFDbad", 1, 1)));

        Assert.Equal(1, response.Skipped);
        Assert.Single(Directory.GetFileSystemEntries(Root));
    }

    private static ulong FolderIdentity(string path, out ulong inode)
    {
        Assert.Equal(StatResult.Ok, Statx.StatPath(path, followLinks: false, out FileStat stat));
        inode = stat.Inode;
        return stat.Device;
    }

    private static ulong FileInode(string path)
    {
        FolderIdentity(path, out ulong inode);
        return inode;
    }
}
