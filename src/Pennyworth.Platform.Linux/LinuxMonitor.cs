using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Live memory, CPU, GPU and per-app usage on Linux, read from <c>/proc</c> and <c>/sys</c> without
/// any privilege. The counterpart of the Windows <c>SystemMonitor</c>; grouping and CPU arithmetic
/// are the same shared <see cref="AppGrouper"/> and <see cref="CpuTracker"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per process, <c>stat</c> gives identity (pid and start time), CPU time and parent, and <c>statm</c>
/// the resident memory (<c>VmRSS</c>). Private memory comes from <c>smaps_rollup</c> where the kernel
/// allows it (the user's own processes); resident memory stands in elsewhere. Kernel threads are not
/// listed. When <c>/proc</c> yields no process at all (<c>hidepid</c>, a container without it) the list
/// comes from <see cref="Process.GetProcesses"/>, with no private memory.
/// </para>
/// <para>
/// <c>smaps_rollup</c> makes the kernel walk the page tables of the process, about 1.5 ms on average
/// and 15 ms for a browser, so reading it for every process every second would cost a quarter of a core.
/// A reading is therefore refreshed when it is 60 samples old or the resident size moved by 64 MiB,
/// new and large processes first, within a time budget per sample. Between readings private memory
/// follows the resident size: the reading plus the growth since.
/// </para>
/// <para>
/// Most entries in <c>/proc</c> are kernel threads (600 of 820 on a desktop). Once an id is known to
/// be one, it is looked at again only every 16th sample: ids are handed out cyclically, so a new
/// process cannot take that id within seconds.
/// </para>
/// <para>
/// A source that fails degrades its own field (zeros, or a null GPU load) and never fails the
/// sample. Not thread-safe: call <see cref="Sample"/> from one thread, about once per second.
/// </para>
/// </remarks>
public sealed class LinuxMonitor : ISystemMonitor, IDisposable
{
    private const int PrivateMaxAge = 60;
    private const int KernelThreadRecheck = 16;
    private const long PrivateChangeThreshold = 64L << 20;

    private static readonly long s_ticksPerJiffy = TimeSpan.TicksPerSecond / ProcFileSystem.TicksPerSecond;

    // New processes first, the biggest first among them (they head the list); then the oldest reading.
    private static readonly Comparison<(int Index, ProcProcessDetails.Entry Entry, long Resident)> s_byUrgency = static (a, b) =>
    {
        if (a.Entry.PrivateMeasured != b.Entry.PrivateMeasured)
        {
            return a.Entry.PrivateMeasured ? 1 : -1;
        }

        return a.Entry.PrivateMeasured ? a.Entry.PrivateSample.CompareTo(b.Entry.PrivateSample) : b.Resident.CompareTo(a.Resident);
    };

    private readonly CpuTracker _cpu = new();
    private readonly AppGrouper _grouper;
    private readonly ProcFileSystem _proc;
    private readonly ProcProcessDetails _details;
    private readonly LinuxGpuMeter _gpu;
    private readonly CgroupMemoryLimit _cgroup;
    private readonly TimeProvider _clock;
    private readonly string _sysRoot;
    private readonly string _memInfoPath;
    private readonly string _statPath;
    private readonly List<int> _pids = [];
    private readonly List<int> _processIds = [];
    private readonly List<ProcessSample> _processes = new(512);
    private readonly List<(int Index, ProcProcessDetails.Entry Entry, long Resident)> _due = new(64);
    private readonly TimeSpan _privateBudget;
    private long _sampleNumber;
    private readonly long _pageSize = Environment.SystemPageSize;
    private long _lastSample;
    private int _cores = Environment.ProcessorCount;

    /// <param name="procRoot">The process file system; <c>/proc</c> unless a test stands in for it.</param>
    /// <param name="sysRoot">The <c>/sys</c> mount (cgroups, zram, GPUs).</param>
    /// <param name="clock">Measures the time between samples.</param>
    /// <param name="privateMemoryBudget">
    /// The most time one sample spends reading <c>smaps_rollup</c> (at least one process is always read,
    /// so every process is reached eventually). Defaults to 10 ms.
    /// </param>
    public LinuxMonitor(
        IEnumerable<ProcessExplanation> explanations,
        string procRoot = "/proc",
        string sysRoot = "/sys",
        TimeProvider? clock = null,
        TimeSpan? privateMemoryBudget = null)
    {
        _privateBudget = privateMemoryBudget ?? TimeSpan.FromMilliseconds(10);
        _clock = clock ?? TimeProvider.System;
        _sysRoot = sysRoot;
        _proc = new ProcFileSystem(procRoot);
        _details = new ProcProcessDetails(_proc, _clock);
        _grouper = new AppGrouper(explanations, _details);
        _gpu = new LinuxGpuMeter(_proc, sysRoot, _clock);
        _cgroup = new CgroupMemoryLimit(_proc, Path.Combine(sysRoot, "fs", "cgroup"), _clock);
        _memInfoPath = Path.Combine(procRoot, "meminfo");
        _statPath = Path.Combine(procRoot, "stat");

        ReadMachineCpu();
        ReadProcesses();
        _cpu.UpdateProcesses(_processes, TimeSpan.Zero, _cores);
        _gpu.Sample(_processIds); // Rates need two samples; the first one primes the counters.
        _lastSample = _clock.GetTimestamp();
    }

    public MonitorSample Sample()
    {
        long now = _clock.GetTimestamp();
        TimeSpan elapsed = _clock.GetElapsedTime(_lastSample, now);
        _lastSample = now;

        ReadProcesses();
        double total = ReadMachineCpu();
        var cpuByPid = _cpu.UpdateProcesses(_processes, elapsed, _cores);
        return new MonitorSample(ReadMemory(), total, _gpu.Sample(_processIds), _grouper.Group(_processes, cpuByPid));
    }

    public void Dispose() => _proc.Dispose();

    private double ReadMachineCpu()
    {
        if (!_proc.TryRead(_statPath, out ReadOnlySpan<char> text) || !ProcFileSystem.TryParseCpuStat(text, out CpuTimes times))
        {
            return 0;
        }

        if (times.Cores > 0)
        {
            _cores = times.Cores;
        }

        return _cpu.UpdateSystem(times.Idle, times.Kernel, times.User);
    }

    private void ReadProcesses()
    {
        _processes.Clear();
        _due.Clear();
        _sampleNumber++;
        _proc.ListProcessIds(_pids);
        _details.BeginSample();
        int readable = 0;
        foreach (int pid in _pids)
        {
            ProcProcessDetails.Entry entry = _details.Track(pid);
            if (entry.KernelThread && (pid + _sampleNumber) % KernelThreadRecheck != 0)
            {
                readable++;
                continue;
            }

            if (!_proc.TryRead(entry.StatPath, out ReadOnlySpan<char> text) || !ProcFileSystem.TryParseProcessStat(text, out ProcessStat stat))
            {
                continue; // Ended since it was listed, or hidden from this user.
            }

            readable++;
            entry.KernelThread = (stat.Flags & ProcFileSystem.KernelThreadFlag) != 0;
            if (entry.KernelThread)
            {
                continue;
            }

            string name = ProcProcessDetails.Identify(entry, stat.StartTime, stat.Comm);
            int parent = stat.ParentPid;
            long cpuTime = (stat.UserTicks + stat.SystemTicks) * s_ticksPerJiffy;
            long startTime = stat.StartTime;
            long resident = _proc.TryRead(entry.StatmPath, out text) && ProcFileSystem.ParseResidentPages(text) is { } pages ? pages * _pageSize : 0;

            if (entry.PrivateReadable
                && (!entry.PrivateMeasured
                    || _sampleNumber - entry.PrivateSample >= PrivateMaxAge
                    || Math.Abs(resident - entry.PrivateResident) >= PrivateChangeThreshold))
            {
                _due.Add((_processes.Count, entry, resident));
            }

            _processes.Add(new ProcessSample(pid, parent, name, EstimatePrivate(entry, resident), resident, cpuTime, startTime));
        }

        _details.EndSample();
        if (readable == 0)
        {
            ReadProcessesWithoutProc();
        }
        else
        {
            RefreshPrivateMemory();
        }

        _processIds.Clear();
        foreach (ProcessSample process in _processes)
        {
            _processIds.Add(process.Pid);
        }
    }

    /// <summary>The last <c>smaps_rollup</c> reading plus the growth in resident memory since, or the resident size if there is none.</summary>
    private static long EstimatePrivate(ProcProcessDetails.Entry entry, long resident) =>
        entry.PrivateMeasured ? Math.Clamp(entry.PrivateBytes + (resident - entry.PrivateResident), 0, resident) : resident;

    /// <summary>
    /// Reads <c>smaps_rollup</c> for the processes that are due, most urgent first, until the budget
    /// is spent. A refusal (another user's process) is remembered for the life of the process.
    /// </summary>
    private void RefreshPrivateMemory()
    {
        _due.Sort(s_byUrgency);
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < _due.Count; i++)
        {
            if (i > 0 && Stopwatch.GetElapsedTime(start) >= _privateBudget)
            {
                return;
            }

            (int index, ProcProcessDetails.Entry entry, long resident) = _due[i];
            if (_proc.TryRead(entry.SmapsPath, out ReadOnlySpan<char> text) && ProcFileSystem.ParsePrivateBytes(text) is { } bytes)
            {
                entry.PrivateBytes = bytes;
                entry.PrivateResident = resident;
                entry.PrivateSample = _sampleNumber;
                entry.PrivateMeasured = true;
                _processes[index] = _processes[index] with { PrivateMemory = Math.Min(bytes, resident) };
            }
            else
            {
                entry.PrivateReadable = false;
            }
        }
    }

    /// <summary>
    /// The list when <c>/proc</c> shows nothing: ids and names from the runtime, which reads
    /// <c>/proc</c> its own way. No private memory is known, so none is claimed.
    /// </summary>
    private void ReadProcessesWithoutProc()
    {
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or PlatformNotSupportedException or IOException)
        {
            return;
        }

        foreach (Process process in all)
        {
            using (process)
            {
                try
                {
                    _processes.Add(new ProcessSample(process.Id, 0, process.ProcessName, 0, Safe(() => process.WorkingSet64), Safe(() => process.TotalProcessorTime.Ticks), Safe(() => process.StartTime.Ticks)));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or IOException)
                {
                    // Ended while it was read.
                }
            }
        }
    }

    private static long Safe(Func<long> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or IOException)
        {
            return 0;
        }
    }

    private MemoryStatus ReadMemory()
    {
        if (!_proc.TryRead(_memInfoPath, out ReadOnlySpan<char> text) || !ProcFileSystem.TryParseMemInfo(text, out MemInfo info))
        {
            return default;
        }

        long total = info.Total;
        long available = info.Available;
        _cgroup.Apply(ref total, ref available);

        // Buffers and Cached include shared memory and files in use, which the kernel cannot hand back,
        // so their sum can exceed what is available. Only what is available beyond the free memory
        // can be cached, so the split never shows free memory the kernel does not have.
        long cached = Math.Clamp(info.Buffers + info.Cached + info.Reclaimable, 0, Math.Max(available - info.Free, 0));
        return new MemoryStatus(total, available, cached, info.Zswap + ReadZramBytes());
    }

    /// <summary>RAM held by zram devices, which is how a zram swap compresses memory.</summary>
    private long ReadZramBytes()
    {
        long bytes = 0;
        try
        {
            foreach (string device in Directory.EnumerateDirectories(Path.Combine(_sysRoot, "block"), "zram*"))
            {
                if (_proc.TryRead(Path.Combine(device, "mm_stat"), out ReadOnlySpan<char> text))
                {
                    bytes += ProcFileSystem.ParseZramBytes(text);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No block devices to look at: nothing is compressed that we can see.
        }

        return bytes;
    }
}
