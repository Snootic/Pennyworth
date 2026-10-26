using System.Diagnostics;
using System.Globalization;

namespace Pennyworth.Platform.Linux;

/// <summary>One engine of one DRM client, from <c>/proc/&lt;pid&gt;/fdinfo/&lt;fd&gt;</c>.</summary>
/// <param name="Busy">Cumulative busy time: nanoseconds (i915, amdgpu) or GPU cycles (xe).</param>
/// <param name="Total">Cumulative cycles of the whole engine class (xe), or -1 when <paramref name="Busy"/> is in nanoseconds.</param>
/// <param name="Capacity">How many engines share the class (two video decoders), so that busy time can be 200%.</param>
internal readonly record struct DrmEngine(int Engine, long Busy, long Total, int Capacity);

/// <summary>Gives each distinct name a small number, without allocating when the name was seen before.</summary>
internal sealed class NameTable
{
    private readonly Dictionary<string, int> _ids = [];
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public NameTable()
    {
        _lookup = _ids.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public int Id(ReadOnlySpan<char> name)
    {
        if (!_lookup.TryGetValue(name, out int id))
        {
            id = _ids.Count;
            _ids[name.ToString()] = id;
        }

        return id;
    }
}

/// <summary>
/// GPU load of Intel (and any other) DRM drivers that expose no utilisation file, from the per-client
/// counters the kernel publishes in <c>fdinfo</c> of every open DRM device: how long each engine
/// (render, video, copy, compute) worked for each client. This is how <c>intel_gpu_top</c> and
/// <c>nvtop</c> measure without privileges, and it sees the clients the user may inspect: their own.
/// </summary>
/// <remarks>
/// Finding the clients means looking at every file descriptor of every process (10 000 on a desktop,
/// 30 ms), too much for each second. A sample therefore scans processes for a few milliseconds, where
/// the previous one stopped, and reads only the DRM descriptors already found: a new client is noticed
/// within one round of the process list (about ten samples on a desktop), at a steady cost. A browser
/// or compositor holds several descriptors of one client, and each read costs the driver about 80 µs,
/// so a second descriptor of a client is skipped until its process is scanned again.
/// As everywhere in the monitor, the load is the busiest engine class, the way Task Manager reports it.
/// </remarks>
/// <param name="scanBudget">
/// The most time one sample spends looking for clients, 4 ms by default. At least one process is
/// always scanned, so every process is reached eventually.
/// </param>
internal sealed class DrmClientMeter(ProcFileSystem proc, TimeProvider clock, TimeSpan? scanBudget = null)
{
    private readonly TimeSpan _scanBudget = scanBudget ?? TimeSpan.FromMilliseconds(4);

    private readonly NameTable _engines = new();
    private readonly NameTable _devices = new();
    private readonly Dictionary<(int Pid, int Fd), OpenFile> _files = [];
    private readonly List<(int Pid, int Fd)> _gone = [];
    private readonly List<int> _fds = [];
    private readonly List<DrmEngine> _readings = [];
    private readonly HashSet<(int Device, long Client)> _seen = [];
    private readonly Dictionary<(int Device, int Engine), (long Busy, long Capacity)> _busy = [];
    private Dictionary<(int Device, long Client, int Engine), long> _previous = [];
    private Dictionary<(int Device, long Client, int Engine), long> _current = [];
    private Dictionary<(int Device, int Engine), long> _previousTotal = [];
    private Dictionary<(int Device, int Engine), long> _currentTotal = [];
    private long _lastSample;
    private int _tick;
    private int _next;
    private int _round;

    /// <summary>
    /// Load of the busiest engine class since the previous call, 0 to 100, or null when no DRM client
    /// can be read or none was seen twice yet. The first call only primes the counters.
    /// </summary>
    /// <param name="pids">Process ids to look for clients in.</param>
    public double? Sample(IReadOnlyList<int> pids)
    {
        long now = clock.GetTimestamp();
        double elapsedNanoseconds = _tick == 0 ? 0 : clock.GetElapsedTime(_lastSample, now).TotalNanoseconds;
        _lastSample = now;

        Discover(pids, everything: _tick == 0);
        _tick++;

        _current.Clear();
        _currentTotal.Clear();
        _busy.Clear();
        _seen.Clear();
        _gone.Clear();
        foreach (((int pid, int fd), OpenFile file) in _files)
        {
            if (file.Duplicate)
            {
                // Its client is read through another descriptor; every rescan of the process gives it a new
                // chance. A descriptor no rescan has seen for two rounds is closed.
                if (_round - file.ScannedAt > 2)
                {
                    _gone.Add((pid, fd));
                }

                continue;
            }

            if (!proc.TryRead(file.Path, out ReadOnlySpan<char> text)
                || !TryParseFdInfo(text, _engines, _devices, _readings, out int device, out long client))
            {
                _gone.Add((pid, fd)); // Closed, the process ended, or the number now means something else.
                continue;
            }

            if (!_seen.Add((device, client)))
            {
                file.Duplicate = true; // The same client through a duplicated descriptor, in this process or a forked one.
                continue;
            }

            foreach (DrmEngine engine in _readings)
            {
                _current[(device, client, engine.Engine)] = engine.Busy;
                if (engine.Total >= 0)
                {
                    _currentTotal[(device, engine.Engine)] = engine.Total;
                }

                // A client without a baseline (new, or its counter went backwards) is present but adds no work.
                long worked = _previous.TryGetValue((device, client, engine.Engine), out long before) && engine.Busy >= before ? engine.Busy - before : 0;
                (long busy, _) = _busy.GetValueOrDefault((device, engine.Engine));
                _busy[(device, engine.Engine)] = (busy + worked, engine.Capacity);
            }
        }

        foreach ((int, int) key in _gone)
        {
            _files.Remove(key);
        }

        double? load = null;
        foreach (((int device, int engine), (long busy, long capacity)) in _busy)
        {
            double window = elapsedNanoseconds; // Nanosecond counters run against the wall clock...
            if (_currentTotal.TryGetValue((device, engine), out long total))
            {
                // ...cycle counters against the engine's own cycle count.
                if (!_previousTotal.TryGetValue((device, engine), out long previousTotal) || total <= previousTotal)
                {
                    continue;
                }

                window = total - previousTotal;
            }

            if (window > 0)
            {
                load = Math.Max(load ?? 0, Math.Clamp(busy * 100.0 / (window * Math.Max(capacity, 1)), 0, 100));
            }
        }

        (_previous, _current) = (_current, _previous);
        (_previousTotal, _currentTotal) = (_currentTotal, _previousTotal);
        return load;
    }

    private sealed class OpenFile(string path, int scannedAt)
    {
        public string Path { get; } = path;

        public int ScannedAt { get; set; } = scannedAt;

        public bool Duplicate { get; set; }
    }

    private void Discover(IReadOnlyList<int> pids, bool everything)
    {
        long start = Stopwatch.GetTimestamp();
        for (int scanned = 0; scanned < pids.Count; scanned++)
        {
            if (!everything && scanned > 0 && Stopwatch.GetElapsedTime(start) >= _scanBudget)
            {
                return;
            }

            if (_next >= pids.Count)
            {
                _next = 0;
                _round++;
            }

            Scan(pids[_next++]);
        }
    }

    private void Scan(int pid)
    {
        proc.ListOpenFiles(pid, _fds);
        foreach (int fd in _fds)
        {
            if (_files.TryGetValue((pid, fd), out OpenFile? known))
            {
                known.Duplicate = false;
                known.ScannedAt = _round;
            }
            else if (proc.IsDrmFile(pid, fd))
            {
                _files[(pid, fd)] = new OpenFile(Path.Combine(proc.Root, pid.ToString(CultureInfo.InvariantCulture), "fdinfo", fd.ToString(CultureInfo.InvariantCulture)), _round);
            }
        }
    }

    /// <summary>
    /// Parses one <c>fdinfo</c> file. False when it is not a DRM client's (no <c>drm-client-id</c>).
    /// Engines come from <c>drm-engine-&lt;name&gt;: N ns</c> (i915, amdgpu) or
    /// <c>drm-cycles-&lt;name&gt;</c> with <c>drm-total-cycles-&lt;name&gt;</c> (xe), and
    /// <c>drm-engine-capacity-&lt;name&gt;</c> gives the number of engines in the class.
    /// </summary>
    public static bool TryParseFdInfo(
        ReadOnlySpan<char> text, NameTable engines, NameTable devices, List<DrmEngine> readings, out int device, out long client)
    {
        readings.Clear();
        device = 0;
        client = -1;
        bool isClient = false;
        foreach (ReadOnlySpan<char> line in text.EnumerateLines())
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || !line.StartsWith("drm-", StringComparison.Ordinal))
            {
                continue;
            }

            ReadOnlySpan<char> key = line[..colon];
            ReadOnlySpan<char> value = line[(colon + 1)..].Trim();
            if (key is "drm-pdev")
            {
                device = devices.Id(value);
            }
            else if (key is "drm-client-id")
            {
                isClient = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out client);
            }
            else if (key.StartsWith("drm-engine-capacity-", StringComparison.Ordinal))
            {
                Update(readings, engines.Id(key["drm-engine-capacity-".Length..]), capacity: Number(value));
            }
            else if (key.StartsWith("drm-engine-", StringComparison.Ordinal))
            {
                Update(readings, engines.Id(key["drm-engine-".Length..]), busy: Number(value));
            }
            else if (key.StartsWith("drm-total-cycles-", StringComparison.Ordinal))
            {
                Update(readings, engines.Id(key["drm-total-cycles-".Length..]), total: Number(value));
            }
            else if (key.StartsWith("drm-cycles-", StringComparison.Ordinal))
            {
                Update(readings, engines.Id(key["drm-cycles-".Length..]), busy: Number(value));
            }
        }

        return isClient;
    }

    /// <summary>The leading integer of a value such as "12345 ns".</summary>
    private static long Number(ReadOnlySpan<char> value)
    {
        int end = value.IndexOf(' ');
        return long.TryParse(end < 0 ? value : value[..end], NumberStyles.None, CultureInfo.InvariantCulture, out long number) ? number : 0;
    }

    private static void Update(List<DrmEngine> readings, int engine, long? busy = null, long? total = null, long? capacity = null)
    {
        int at = -1;
        for (int i = 0; i < readings.Count; i++)
        {
            if (readings[i].Engine == engine)
            {
                at = i;
                break;
            }
        }

        DrmEngine current = at >= 0 ? readings[at] : new DrmEngine(engine, 0, -1, 1);
        current = current with
        {
            Busy = busy ?? current.Busy,
            Total = total ?? current.Total,
            Capacity = capacity is { } c ? (int)Math.Clamp(c, 1, 1024) : current.Capacity,
        };
        if (at >= 0)
        {
            readings[at] = current;
        }
        else
        {
            readings.Add(current);
        }
    }
}
