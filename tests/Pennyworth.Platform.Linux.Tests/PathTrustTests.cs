using System.Diagnostics;
using System.Runtime.Versioning;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>
/// A real temporary folder for the tests of the root helper's file-system code: they run as an ordinary user, so the
/// "owned by root" checks are replaced by injected ones, but every open, stat and unlink is the real thing.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class TempTree : IDisposable
{
    internal TempTree()
    {
        Root = Real(Directory.CreateTempSubdirectory("pw-helper-").FullName);
        Owner = PathTrust.Lstat(Root).Uid;
    }

    /// <summary>The folder with every link in its path resolved, so a <c>O_NOFOLLOW</c> open of it works wherever the temp folder lives.</summary>
    internal string Root { get; }

    /// <summary>The user running the test: who owns everything created here.</summary>
    internal uint Owner { get; }

    internal string PathOf(string relative) => Path.Combine(Root, relative);

    internal string Write(string relative, int length = 10)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    internal string Mkdir(string relative)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>The modification time of the entry itself (a link is not followed) as the helper reads it.</summary>
    internal static long Ticks(string path)
    {
        Assert.Equal(0, SafeFs.StatPathNoFollow(path, out StatxBuffer buffer));
        return SafeFs.ModifiedTicks(in buffer);
    }

    internal static void Run(string file, params string[] arguments)
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

    public void Dispose()
    {
        // rm, not Directory.Delete: .NET cannot unlink a name that is not valid UTF-8.
        Run("/bin/rm", "-rf", Root);
    }

    private static string Real(string path)
    {
        string current = "/";
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null)
            {
                current = info.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
            }
        }

        return current;
    }
}

[SupportedOSPlatform("linux")]
public sealed class PathTrustTests
{
    private const uint Executable = 0x1ED; // 0755
    private const string Helper = "/usr/libexec/pennyworth/pennyworth-helper";

    private static EntryInfo Dir(uint uid = 0, uint mode = 0x1ED) => new(EntryKind.Directory, uid, mode);

    private static EntryInfo File(uint uid = 0, uint mode = Executable) => new(EntryKind.File, uid, mode);

    private static Func<string, EntryInfo> Chain(params (string Path, EntryInfo Info)[] entries)
    {
        Dictionary<string, EntryInfo> map = entries.ToDictionary(e => e.Path, e => e.Info);
        return path => map.TryGetValue(path, out EntryInfo info) ? info : new EntryInfo(EntryKind.Missing, 0, 0);
    }

    private static (string, EntryInfo)[] GoodChain() =>
    [
        ("/", Dir()),
        ("/usr", Dir()),
        ("/usr/libexec", Dir()),
        ("/usr/libexec/pennyworth", Dir()),
        (Helper, File()),
    ];

    private static Func<string, EntryInfo> With(string path, EntryInfo replacement) =>
        Chain(GoodChain().Select(e => e.Item1 == path ? (path, replacement) : e).ToArray());

    [Fact]
    public void A_root_owned_chain_is_trusted() =>
        Assert.Equal(TrustVerdict.Trusted, PathTrust.CheckExecutable(Helper, Chain(GoodChain())));

    [Theory]
    [InlineData("/usr/libexec", 1000u, 0x1EDu)] // owned by a user
    [InlineData("/usr/libexec", 0u, 0x1FDu)] // 0775: group writable
    [InlineData("/usr/libexec/pennyworth", 0u, 0x1EFu)] // 0757: world writable
    [InlineData("/usr/libexec/pennyworth", 0u, 0x3FFu)] // 1777: sticky does not make it safe
    [InlineData("/", 0u, 0x1FDu)] // even the top
    public void An_ancestor_only_root_may_not_change_is_required(string ancestor, uint uid, uint mode) =>
        Assert.Equal(TrustVerdict.NotTrusted, PathTrust.CheckExecutable(Helper, With(ancestor, Dir(uid, mode))));

    [Theory]
    [InlineData(1000u, 0x1EDu)] // owned by a user
    [InlineData(0u, 0x1EFu)] // world writable
    [InlineData(0u, 0x1FDu)] // group writable
    [InlineData(0u, 0x1A4u)] // 0644: not executable
    public void The_final_file_must_be_root_owned_unwritable_and_executable(uint uid, uint mode) =>
        Assert.Equal(TrustVerdict.NotTrusted, PathTrust.CheckExecutable(Helper, With(Helper, File(uid, mode))));

    [Fact]
    public void A_symlink_in_the_middle_is_not_trusted() =>
        Assert.Equal(TrustVerdict.NotTrusted, PathTrust.CheckExecutable(Helper, With("/usr/libexec", new EntryInfo(EntryKind.Symlink, 0, 0x1FF))));

    [Fact]
    public void A_symlink_as_the_final_entry_is_not_trusted() =>
        Assert.Equal(TrustVerdict.NotTrusted, PathTrust.CheckExecutable(Helper, With(Helper, new EntryInfo(EntryKind.Symlink, 0, 0x1FF))));

    [Theory]
    [InlineData("Directory")]
    [InlineData("Other")]
    public void A_final_entry_that_is_not_a_regular_file_is_not_trusted(string kind) =>
        Assert.Equal(TrustVerdict.NotTrusted, PathTrust.CheckExecutable(Helper, With(Helper, new EntryInfo(Enum.Parse<EntryKind>(kind), 0, Executable))));

    [Fact]
    public void An_ancestor_that_is_a_file_is_not_trusted() =>
        Assert.Equal(TrustVerdict.NotTrusted, PathTrust.CheckExecutable(Helper, With("/usr/libexec", File())));

    [Fact]
    public void A_missing_component_is_reported_as_missing()
    {
        (string, EntryInfo)[] withoutHelperDirectory = GoodChain().Where(e => !e.Item1.StartsWith("/usr/libexec/pennyworth", StringComparison.Ordinal)).ToArray();
        Assert.Equal(TrustVerdict.Missing, PathTrust.CheckExecutable(Helper, Chain(withoutHelperDirectory)));
        Assert.Equal(TrustVerdict.Missing, PathTrust.CheckExecutable(Helper, Chain(GoodChain().Where(e => e.Item1 != Helper).ToArray())));
    }

    [Fact]
    public void A_violation_above_a_missing_component_wins()
    {
        Func<string, EntryInfo> lstat = Chain(("/", Dir()), ("/usr", Dir(uid: 1000)));
        Assert.Equal(TrustVerdict.NotTrusted, PathTrust.CheckExecutable(Helper, lstat));
    }

    [Theory]
    [InlineData("")]
    [InlineData("usr/libexec/pennyworth/pennyworth-helper")]
    [InlineData("/usr/libexec/../libexec/pennyworth/pennyworth-helper")]
    [InlineData("/usr//libexec/pennyworth/pennyworth-helper")]
    [InlineData("/usr/libexec/pennyworth/pennyworth-helper/")]
    public void A_path_that_is_not_absolute_and_normalized_is_not_trusted(string path) =>
        Assert.Equal(TrustVerdict.NotTrusted, PathTrust.CheckExecutable(path, Chain(GoodChain())));

    [Fact]
    public void A_root_owned_folder_may_have_any_permission_bits()
    {
        Func<string, EntryInfo> lstat = Chain(("/", Dir()), ("/var", Dir()), ("/var/crash", Dir(0, 0x3FF)));
        Assert.Equal(TrustVerdict.Trusted, PathTrust.CheckRootOwnedDirectory("/var/crash", lstat));
    }

    [Theory]
    [InlineData("/var/crash", "Symlink", 0u)]
    [InlineData("/var/crash", "File", 0u)]
    [InlineData("/var/crash", "Directory", 1000u)]
    [InlineData("/var", "Directory", 1000u)]
    public void A_cleanup_folder_must_be_a_real_root_owned_directory_below_a_root_owned_chain(string path, string kind, uint uid)
    {
        EntryInfo replaced = new(Enum.Parse<EntryKind>(kind), uid, 0x1ED);
        Func<string, EntryInfo> lstat = Chain(
            ("/", Dir()),
            ("/var", path == "/var" ? replaced : Dir()),
            ("/var/crash", path == "/var/crash" ? replaced : Dir()));
        Assert.Equal(TrustVerdict.NotTrusted, PathTrust.CheckRootOwnedDirectory("/var/crash", lstat));
    }

    [Fact]
    public void A_world_writable_ancestor_of_a_cleanup_folder_is_not_trusted()
    {
        Func<string, EntryInfo> lstat = Chain(("/", Dir()), ("/var", Dir(0, 0x1FF)), ("/var/crash", Dir()));
        Assert.Equal(TrustVerdict.NotTrusted, PathTrust.CheckRootOwnedDirectory("/var/crash", lstat));
    }

    [Fact]
    public void A_missing_cleanup_folder_is_reported_as_missing() =>
        Assert.Equal(TrustVerdict.Missing, PathTrust.CheckRootOwnedDirectory("/var/crash", Chain(("/", Dir()), ("/var", Dir()))));

    [LinuxFact]
    public void Lstat_reports_kind_owner_and_permission_bits_without_following_links()
    {
        using var tree = new TempTree();
        string file = tree.Write("file.bin");
        System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string directory = tree.Mkdir("dir");
        string link = tree.PathOf("link");
        System.IO.File.CreateSymbolicLink(link, directory);

        EntryInfo fileInfo = PathTrust.Lstat(file);
        Assert.Equal(EntryKind.File, fileInfo.Kind);
        Assert.Equal(tree.Owner, fileInfo.Uid);
        Assert.Equal(0x180u, fileInfo.Mode);
        Assert.Equal(EntryKind.Directory, PathTrust.Lstat(directory).Kind);
        Assert.Equal(EntryKind.Symlink, PathTrust.Lstat(link).Kind);
        Assert.Equal(EntryKind.Missing, PathTrust.Lstat(tree.PathOf("nothing")).Kind);
        Assert.Equal(EntryKind.Missing, PathTrust.Lstat(tree.PathOf("file.bin/below")).Kind);
    }

    [LinuxFact]
    public void The_real_chain_of_a_user_owned_folder_is_not_trusted_unless_root()
    {
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "Root owns the temporary folder.");
        using var tree = new TempTree();
        Assert.Equal(TrustVerdict.NotTrusted, PathTrust.CheckRootOwnedDirectory(tree.Root, PathTrust.Lstat));
        Assert.Equal(TrustVerdict.NotTrusted, PathTrust.CheckRootOwnedDirectory(tree.PathOf("nothing"), PathTrust.Lstat)); // the user-owned ancestor is found before the gap
        Assert.Equal(TrustVerdict.Missing, PathTrust.CheckRootOwnedDirectory("/pennyworth-test-nothing-here/x", PathTrust.Lstat));
    }
}
