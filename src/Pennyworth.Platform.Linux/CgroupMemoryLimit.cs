namespace Pennyworth.Platform.Linux;

/// <summary>
/// The memory ceiling cgroup v2 puts on this process: <c>memory.max</c> (hard) and
/// <c>memory.high</c> (throttling) of its own cgroup or any ancestor, whichever is smallest. Inside a
/// container or a limited service the machine's <c>MemTotal</c> is not what the app can use.
/// </summary>
/// <remarks>
/// Where the limit sits rarely changes, so it is looked up every 30 s. Use is measured the way
/// container monitors do: <c>memory.current</c> without the inactive file cache, which the kernel
/// drops before it kills anything.
/// </remarks>
internal sealed class CgroupMemoryLimit(ProcFileSystem proc, string cgroupRoot, TimeProvider clock)
{
    private static readonly long s_lookupInterval = TimeSpan.FromSeconds(30).Ticks;
    private static readonly string[] s_limitFiles = ["memory.max", "memory.high"];

    private long? _lookedUpAt;
    private long _ceiling = long.MaxValue;
    private string? _limitedFolder;

    /// <summary>Lowers <paramref name="total"/> to the ceiling and <paramref name="available"/> to what the limited cgroup has left, when a limit is set and smaller.</summary>
    public void Apply(ref long total, ref long available)
    {
        long now = clock.GetTimestamp();
        if (_lookedUpAt is not { } at || clock.GetElapsedTime(at, now).Ticks > s_lookupInterval)
        {
            Lookup();
            _lookedUpAt = now;
        }

        if (_limitedFolder is null || _ceiling >= total)
        {
            return;
        }

        total = _ceiling;
        long used = 0;
        if (proc.TryRead(Path.Combine(_limitedFolder, "memory.current"), out ReadOnlySpan<char> current)
            && ProcFileSystem.ParseLimit(current) is { } bytes)
        {
            used = bytes;
            if (proc.TryRead(Path.Combine(_limitedFolder, "memory.stat"), out ReadOnlySpan<char> stat)
                && ProcFileSystem.TryParseKeyed(stat, "inactive_file", out long inactive))
            {
                used = Math.Max(used - inactive, 0);
            }
        }

        available = Math.Min(available, Math.Max(_ceiling - used, 0));
    }

    private void Lookup()
    {
        _ceiling = long.MaxValue;
        _limitedFolder = null;
        if (!proc.TryRead(Path.Combine(proc.Root, "self", "cgroup"), out ReadOnlySpan<char> file)
            || ProcFileSystem.ParseUnifiedCgroupPath(file) is not { } path)
        {
            return;
        }

        // Walk from the process's own cgroup up to the root of the hierarchy.
        string relative = path.Trim('/');
        while (true)
        {
            string folder = relative.Length == 0 ? cgroupRoot : Path.Combine(cgroupRoot, relative);
            foreach (string name in s_limitFiles)
            {
                if (proc.TryRead(Path.Combine(folder, name), out ReadOnlySpan<char> text)
                    && ProcFileSystem.ParseLimit(text) is { } limit
                    && limit is > 0 && limit < _ceiling)
                {
                    _ceiling = limit;
                    _limitedFolder = folder;
                }
            }

            if (relative.Length == 0)
            {
                return;
            }

            int slash = relative.LastIndexOf('/');
            relative = slash < 0 ? "" : relative[..slash];
        }
    }
}
