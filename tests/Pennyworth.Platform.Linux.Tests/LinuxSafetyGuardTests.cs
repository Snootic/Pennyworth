using System.Runtime.Versioning;
using Pennyworth.Cleanup;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public class LinuxSafetyGuardTests
{
    private static readonly string[] Mounts = ["/", "/home", "/boot/efi", "/mnt/wd-blue", "/var", "/tmp", "/mnt/exram/"];

    private static LinuxSafetyGuard Guard(string home = "/home/ana", Func<string, string?>? variables = null, params string[] mounts) =>
        new(new XdgPaths(name => name == "HOME" ? home : null), () => mounts.Length == 0 ? Mounts : mounts, variables ?? (_ => null));

    [Theory]
    [InlineData("/", false)]
    [InlineData("/etc", false)]
    [InlineData("/etc/ssh", false)]
    [InlineData("/etc/ssh/../ssh", false)]
    [InlineData("/usr", false)]
    [InlineData("/usr/share", false)]
    [InlineData("/usr/local/share/x", false)]
    [InlineData("/boot", false)]
    [InlineData("/boot/efi", false)]
    [InlineData("/bin", false)]
    [InlineData("/lib64/x", false)]
    [InlineData("/proc/1", false)]
    [InlineData("/sys/kernel", false)]
    [InlineData("/dev/shm", false)]
    [InlineData("/run/user/1000", false)]
    [InlineData("/root", false)]
    [InlineData("/root/.cache", false)]
    [InlineData("/var/lib", false)]
    [InlineData("/var/lib/flatpak", false)]
    [InlineData("/home", false)]
    [InlineData("/home/", false)]
    [InlineData("/home/ana", false)]
    [InlineData("/home/ana/", false)]
    [InlineData("/home/bob", false)]
    [InlineData("/var", false)]
    [InlineData("/opt", false)]
    [InlineData("/srv", false)]
    [InlineData("/mnt", false)]
    [InlineData("/media", false)]
    [InlineData("/mnt/wd-blue", false)]
    [InlineData("/mnt/exram", false)]
    [InlineData("relative/dir", false)]
    [InlineData("", false)]
    [InlineData("/etcetera", true)]
    [InlineData("/home/ana/.cache", true)]
    [InlineData("/home/ana/.cache/mozilla", true)]
    [InlineData("/home/bob/.cache", true)]
    [InlineData("/tmp", true)]
    [InlineData("/tmp/x", true)]
    [InlineData("/var/tmp", true)]
    [InlineData("/var/cache", true)]
    [InlineData("/var/log", true)]
    [InlineData("/opt/app/cache", true)]
    [InlineData("/mnt/wd-blue/Trash-junk", true)]
    [InlineData("/boot/efi/EFI/leftover", false)]
    public void Judges_the_table(string folder, bool allowed) =>
        Assert.Equal(allowed, Guard().MayCleanInside(folder));

    [Fact]
    public void The_temp_directories_are_cleanable_even_as_a_mount_root()
    {
        var guard = Guard("/home/ana", name => name == "TMPDIR" ? "/scratch/tmp" : null, "/", "/tmp", "/var/tmp", "/scratch/tmp", "/scratch/other");

        Assert.True(guard.MayCleanInside("/tmp"));
        Assert.True(guard.MayCleanInside("/var/tmp"));
        Assert.True(guard.MayCleanInside("/scratch/tmp"));
        Assert.False(guard.MayCleanInside("/scratch/other"));
    }

    [Fact]
    public void A_temp_variable_cannot_unlock_the_home_or_the_system_trees()
    {
        var home = Guard("/home/ana", name => name == "TMPDIR" ? "/home/ana" : null);
        var etc = Guard("/home/ana", name => name == "TMPDIR" ? "/etc" : null);

        Assert.False(home.MayCleanInside("/home/ana"));
        Assert.False(etc.MayCleanInside("/etc"));
    }

    [Fact]
    public void Refuses_everything_when_the_environment_is_empty_for_system_folders()
    {
        var guard = new LinuxSafetyGuard(new XdgPaths(name => name == "HOME" ? "/home/ana" : null), () => [], _ => null);

        Assert.False(guard.MayCleanInside("/etc"));
    }

    [Fact]
    public void Refuses_when_the_mount_list_cannot_be_read()
    {
        var guard = new LinuxSafetyGuard(new XdgPaths(name => name == "HOME" ? "/home/ana" : null), () => throw new IOException("no"), _ => null);

        Assert.False(guard.MayCleanInside("/home/ana/.cache"));
    }
}

[SupportedOSPlatform("linux")]
public sealed class LinuxSafetyGuardLinkTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pw-guard-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LinuxSafetyGuard GuardAt(string home, params string[] mounts) =>
        new(new XdgPaths(name => name == "HOME" ? home : null), () => mounts, _ => null);

    [LinuxFact]
    public void Refuses_a_symlink_in_the_cache_that_points_into_a_protected_tree()
    {
        string cache = Path.Combine(_root, "home", ".cache");
        Directory.CreateDirectory(cache);
        string link = Path.Combine(cache, "sneaky");
        Directory.CreateSymbolicLink(link, "/etc");
        string relative = Path.Combine(cache, "rel");
        Directory.CreateSymbolicLink(relative, "../../../../../../../../etc/ssh");
        var guard = GuardAt(Path.Combine(_root, "home"));

        Assert.False(guard.MayCleanInside(link));
        Assert.False(guard.MayCleanInside(Path.Combine(link, "ssh")), "a link ancestor counts");
        Assert.False(guard.MayCleanInside(Path.Combine(link, "not-there-yet")));
        Assert.False(guard.MayCleanInside(relative));
        Assert.True(guard.MayCleanInside(cache));
    }

    [LinuxFact]
    public void Refuses_a_link_to_the_home_and_a_home_reached_through_a_link()
    {
        string realHome = Path.Combine(_root, "real-home");
        Directory.CreateDirectory(realHome);
        string homeLink = Path.Combine(_root, "home-link");
        Directory.CreateSymbolicLink(homeLink, realHome);
        string toHome = Path.Combine(_root, "to-home");
        Directory.CreateSymbolicLink(toHome, realHome);

        Assert.False(GuardAt(realHome).MayCleanInside(toHome));
        Assert.False(GuardAt(homeLink).MayCleanInside(realHome), "the configured home is itself a link");
        Assert.True(GuardAt(homeLink).MayCleanInside(Path.Combine(realHome, ".cache")));
    }

    [LinuxFact]
    public void Refuses_a_link_loop_and_a_link_to_a_mount_root()
    {
        string a = Path.Combine(_root, "a");
        string b = Path.Combine(_root, "b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);
        string mount = Path.Combine(_root, "mount");
        Directory.CreateDirectory(mount);
        string toMount = Path.Combine(_root, "to-mount");
        Directory.CreateSymbolicLink(toMount, mount);
        var guard = GuardAt("/home/ana", mount);

        Assert.False(guard.MayCleanInside(a));
        Assert.False(guard.MayCleanInside(toMount));
        Assert.True(guard.MayCleanInside(Path.Combine(toMount, "cache")));
    }

    [LinuxFact]
    public void Never_throws_on_odd_input()
    {
        var guard = GuardAt("/home/ana");

        Assert.False(guard.MayCleanInside("/home/ana/\0x"));
        Assert.True(guard.MayCleanInside("/home/ana/.cache/" + new string('x', 200)));
    }

    [LinuxFact]
    public void The_environment_built_for_linux_refuses_etc_with_no_variables()
    {
        var paths = new XdgPaths(name => name == "HOME" ? "/home/ana" : null);
        CleanupEnvironment env = LinuxCleanupEnvironment.Create(paths, new EnvironmentScope(paths, _ => null), () => [], uid: 1000);

        Assert.False(env.MayCleanInside("/etc"));
        Assert.Equal("/home/ana", env.Variables("HOME"));
        Assert.NotNull(env.RecycleBin);
        Assert.Equal(StringComparer.Ordinal, env.PathComparison);
    }
}
