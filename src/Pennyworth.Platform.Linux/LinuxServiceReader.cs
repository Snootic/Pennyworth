using System.Globalization;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Which systemd services run in which process, for expanding a service host in the Memory &amp; CPU list.
/// </summary>
/// <remarks>
/// systemd puts every service in a cgroup named after its unit, and <c>/proc/&lt;pid&gt;/cgroup</c> is
/// readable for every process of every user, so one pass over <c>/proc</c> attributes processes to
/// services with no D-Bus call and no privileges. Unlike Windows, where one host process runs many
/// services, a Linux process is in exactly one service: each list has one unit name.
/// </remarks>
internal static class LinuxServiceReader
{
    /// <summary>Unit names (<c>sshd.service</c>) of the services that run, by process ID. Empty when <c>/proc</c> cannot be read.</summary>
    public static Dictionary<int, List<string>> Read(ProcFileSystem proc)
    {
        var result = new Dictionary<int, List<string>>();
        var pids = new List<int>();
        proc.ListProcessIds(pids);
        foreach (int pid in pids)
        {
            string path = Path.Combine(proc.Root, pid.ToString(CultureInfo.InvariantCulture), "cgroup");
            if (proc.TryRead(path, out ReadOnlySpan<char> text) && ProcFileSystem.ParseServiceUnit(text) is { } unit)
            {
                result[pid] = [unit];
            }
        }

        return result;
    }
}
