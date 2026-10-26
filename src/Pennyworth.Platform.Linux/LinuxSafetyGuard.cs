namespace Pennyworth.Platform.Linux;

/// <summary>
/// The hard rule of what a cleanup rule may never delete inside, whatever the knowledge base says.
/// Deny by default: anything that cannot be judged is refused. Symlinks are resolved first (the
/// folder and every ancestor), so a link in a cache folder that points at <c>/etc</c> is judged as
/// <c>/etc</c>; both the path as written and the resolved path must pass. Comparisons are ordinal.
/// <list type="table">
/// <listheader><term>Rule</term><description>Paths</description></listheader>
/// <item><term>Refused with everything below</term><description><c>/bin /boot /dev /etc /lib /lib32 /lib64 /proc /root /run /sbin /sys /usr /var/lib</c></description></item>
/// <item><term>Refused exactly (their contents may be cleaned)</term><description><c>/</c>, <c>/home</c>, any direct child of <c>/home</c> (another user's home), the user's home, <c>/opt /srv /mnt /media /var</c>, and every mount root</description></item>
/// <item><term>Mount root exception</term><description><c>/tmp</c>, <c>$TMPDIR</c> and <c>/var/tmp</c> may be cleaned even when they are their own mount</description></item>
/// <item><term>Everything else</term><description>allowed, e.g. <c>$HOME/.cache</c>, <c>/var/cache</c>, <c>/var/log</c>, <c>/opt/app/cache</c></description></item>
/// </list>
/// A folder that does not exist is judged by the same textual rules. Nothing here throws.
/// </summary>
internal sealed class LinuxSafetyGuard(XdgPaths paths, Func<IReadOnlyList<string>> mountPoints, Func<string, string?> variables)
{
    private const int MaxLinkHops = 40;

    private static readonly string[] ProtectedTrees =
        ["/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/root", "/run", "/sbin", "/sys", "/usr", "/var/lib"];

    private static readonly string[] ProtectedFolders = ["/", "/home", "/opt", "/srv", "/mnt", "/media", "/var"];

    public bool MayCleanInside(string folder)
    {
        try
        {
            return Judge(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool Judge(string folder)
    {
        string home = paths.Home;
        if (string.IsNullOrEmpty(folder) || !Path.IsPathRooted(folder) || folder.Contains('\0')
            || string.IsNullOrEmpty(home) || !Path.IsPathRooted(home))
        {
            return false;
        }

        string written = Normalize(folder);
        if (Resolve(written) is not { } resolved)
        {
            return false; // Link loop.
        }

        var homes = Both(home).ToList();
        var mounts = new List<string>();
        foreach (string mount in mountPoints())
        {
            if (Path.IsPathRooted(mount))
            {
                mounts.AddRange(Both(mount));
            }
        }

        var temps = Both("/tmp").ToList();
        temps.AddRange(Both("/var/tmp"));
        if (variables("TMPDIR") is { Length: > 0 } tmp && Path.IsPathRooted(tmp))
        {
            temps.AddRange(Both(tmp));
        }

        return Allowed(written, homes, mounts, temps) && Allowed(resolved, homes, mounts, temps);
    }

    private static bool Allowed(string path, List<string> homes, List<string> mounts, List<string> temps)
    {
        if (ProtectedTrees.Any(tree => IsInside(path, tree)))
        {
            return false;
        }

        if (ProtectedFolders.Contains(path) || homes.Contains(path) || IsUserHomeFolder(path))
        {
            return false;
        }

        return temps.Contains(path) || !mounts.Contains(path);
    }

    private static bool IsUserHomeFolder(string path) =>
        path.StartsWith("/home/", StringComparison.Ordinal) && path.IndexOf('/', "/home/".Length) < 0;

    private static bool IsInside(string path, string root) =>
        path == root || path.StartsWith(root + "/", StringComparison.Ordinal);

    /// <summary>A path as written and as it really is (a link in it followed), for comparison against a list.</summary>
    private IEnumerable<string> Both(string path)
    {
        string written = Normalize(path);
        yield return written;
        if (Resolve(written) is { } resolved && resolved != written)
        {
            yield return resolved;
        }
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        return full.Length > 1 ? Path.TrimEndingDirectorySeparator(full) : "/";
    }

    /// <summary>
    /// Like <c>realpath</c> but tolerant of missing tails: follows every link on the way, and
    /// appends what does not exist as written. Null after too many links (a loop).
    /// </summary>
    private static string? Resolve(string absolute)
    {
        var pending = new Stack<string>(absolute.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
        string current = "/";
        int hops = 0;
        while (pending.Count > 0)
        {
            string part = pending.Pop();
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? "/";
                continue;
            }

            string next = current == "/" ? "/" + part : current + "/" + part;
            if (new FileInfo(next).LinkTarget is not { } target)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                return null;
            }

            if (Path.IsPathRooted(target))
            {
                current = "/";
            }

            foreach (string piece in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                pending.Push(piece);
            }
        }

        return current;
    }
}
