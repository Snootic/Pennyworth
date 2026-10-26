using System.Globalization;
using Pennyworth.Monitoring;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Everything about a process that does not change while it lives, remembered per process so a
/// sample of hundreds of processes does not re-read it every second: the paths of its files, its
/// name, and where its executable is. A process is identified by id <b>and</b> start time, so a
/// reused id starts a fresh entry.
/// </summary>
internal sealed class ProcProcessDetails(ProcFileSystem proc, TimeProvider clock) : IProcessDetails
{
    private static readonly long s_servicesMaxAge = TimeSpan.FromSeconds(10).Ticks;

    private readonly Dictionary<int, Entry> _entries = [];
    private int _generation;
    private IReadOnlyDictionary<int, List<string>> _services = new Dictionary<int, List<string>>();
    private long? _servicesReadAt;

    /// <summary>Marks the start of a sample: entries not touched before <see cref="EndSample"/> belong to processes that ended.</summary>
    public void BeginSample() => _generation++;

    public void EndSample()
    {
        foreach ((int pid, Entry entry) in _entries)
        {
            if (entry.Generation != _generation)
            {
                _entries.Remove(pid);
            }
        }
    }

    public Entry Track(int pid)
    {
        if (!_entries.TryGetValue(pid, out Entry? entry))
        {
            string folder = Path.Combine(proc.Root, pid.ToString(CultureInfo.InvariantCulture));
            _entries[pid] = entry = new Entry(Path.Combine(folder, "stat"), Path.Combine(folder, "statm"), Path.Combine(folder, "smaps_rollup"));
        }

        entry.Generation = _generation;
        return entry;
    }

    /// <summary>
    /// Records the identity just read from <c>stat</c> and returns the process name. The name is
    /// re-read every sample (a process can rename itself) but only allocated when it changed.
    /// </summary>
    public static string Identify(Entry entry, long startTime, ReadOnlySpan<char> comm)
    {
        if (entry.StartTime != startTime)
        {
            entry.StartTime = startTime;
            entry.ExePath = null;
            entry.ExeKnown = false;
            entry.PrivateReadable = true;
            entry.PrivateMeasured = false;
        }

        if (!comm.SequenceEqual(entry.Name))
        {
            entry.Name = comm.ToString();
        }

        return entry.Name;
    }

    public string? GetImagePath(int pid)
    {
        if (!_entries.TryGetValue(pid, out Entry? entry))
        {
            return proc.ReadExePath(pid);
        }

        if (!entry.ExeKnown)
        {
            entry.ExePath = proc.ReadExePath(pid); // Null for other users' processes, which stays null.
            entry.ExeKnown = true;
        }

        return entry.ExePath;
    }

    /// <summary>Linux executables carry no product description or publisher, so there is nothing to show.</summary>
    public (string? Description, string? Company) GetVersionInfo(string imagePath) => (null, null);

    // Services start and stop rarely; refreshing every 10 s is plenty.
    public IReadOnlyDictionary<int, List<string>> GetServicesByPid()
    {
        long now = clock.GetTimestamp();
        if (_servicesReadAt is not { } readAt || clock.GetElapsedTime(readAt, now).Ticks > s_servicesMaxAge)
        {
            _services = LinuxServiceReader.Read(proc);
            _servicesReadAt = now;
        }

        return _services;
    }

    internal sealed class Entry(string statPath, string statmPath, string smapsPath)
    {
        public string StatPath { get; } = statPath;

        public string StatmPath { get; } = statmPath;

        public string SmapsPath { get; } = smapsPath;

        public int Generation { get; set; }

        public long StartTime { get; set; } = -1;

        public string Name { get; set; } = "";

        /// <summary>Kernel threads have no memory of their own and are not listed.</summary>
        public bool KernelThread { get; set; }

        public string? ExePath { get; set; }

        public bool ExeKnown { get; set; }

        /// <summary>False once <c>smaps_rollup</c> failed for this process: another user's, which never becomes readable.</summary>
        public bool PrivateReadable { get; set; } = true;

        /// <summary>Whether <see cref="PrivateBytes"/> holds a reading of <c>smaps_rollup</c> yet.</summary>
        public bool PrivateMeasured { get; set; }

        /// <summary>Private memory at the last reading of <c>smaps_rollup</c>.</summary>
        public long PrivateBytes { get; set; }

        /// <summary>Resident memory when <see cref="PrivateBytes"/> was read: what later growth is measured from.</summary>
        public long PrivateResident { get; set; }

        /// <summary>The sample number of that reading.</summary>
        public long PrivateSample { get; set; }
    }
}
