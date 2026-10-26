using System.Globalization;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// GPU load, 0 to 100, from whatever the installed GPUs expose to an ordinary user:
/// <list type="bullet">
/// <item>NVIDIA: <c>nvidia-smi</c>, which is the only supported way to read the proprietary driver's load.
/// It is only started when an NVIDIA PCI device exists and the program is on <c>PATH</c>, and a failed run is not
/// repeated for 30 s, so a machine without it never spawns a process per second.</item>
/// <item>AMD: <c>gpu_busy_percent</c> of the DRM card.</item>
/// <item>Intel (i915, xe): no utilisation file exists, so the per-client engine counters of the DRM
/// <c>fdinfo</c> files are used (see <see cref="DrmClientMeter"/>).</item>
/// </list>
/// With several GPUs the busiest one is reported, the way the Windows meter reports its busiest engine.
/// A GPU without a readable source contributes nothing; with no source at all the answer is null.
/// </summary>
internal sealed class LinuxGpuMeter
{
    private const string NvidiaVendor = "0x10de";
    private const string IntelVendor = "0x8086";
    private static readonly TimeSpan s_nvidiaRetry = TimeSpan.FromSeconds(30);
    private static readonly string[] s_nvidiaArguments = ["--query-gpu=utilization.gpu", "--format=csv,noheader,nounits"];

    private readonly ProcFileSystem _proc;
    private readonly TimeProvider _clock;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, string?> _run;
    private readonly List<string> _busyFiles = [];
    private readonly DrmClientMeter? _intel;
    private readonly string? _nvidiaSmi;
    private long? _nvidiaFailedAt;

    /// <param name="sysRoot">The <c>/sys</c> mount.</param>
    /// <param name="path">The <c>PATH</c> to find <c>nvidia-smi</c> in; defaults to the process's own.</param>
    /// <param name="run">Runs a program and returns its output, or null; defaults to <see cref="ProcessProbe.Run"/>.</param>
    public LinuxGpuMeter(
        ProcFileSystem proc,
        string sysRoot,
        TimeProvider clock,
        string? path = null,
        Func<string, IReadOnlyList<string>, TimeSpan, string?>? run = null)
    {
        _proc = proc;
        _clock = clock;
        _run = run ?? ((file, arguments, timeout) => ProcessProbe.Run(file, arguments, timeout));

        bool intel = false;
        foreach (string card in Cards(Path.Combine(sysRoot, "class", "drm")))
        {
            string device = Path.Combine(card, "device");
            string? vendor = ReadLine(Path.Combine(device, "vendor"));
            string busy = Path.Combine(device, "gpu_busy_percent");
            if (File.Exists(busy))
            {
                _busyFiles.Add(busy);
            }
            else if (vendor == IntelVendor)
            {
                intel = true;
            }
        }

        if (intel)
        {
            _intel = new DrmClientMeter(proc, clock);
        }

        if (HasNvidia(Path.Combine(sysRoot, "bus", "pci", "devices")))
        {
            _nvidiaSmi = FindOnPath("nvidia-smi", path ?? Environment.GetEnvironmentVariable("PATH"));
        }
    }

    /// <summary>False when no GPU of a kind this meter can read exists, so <see cref="Sample"/> is always null.</summary>
    public bool IsAvailable => _busyFiles.Count > 0 || _intel is not null || _nvidiaSmi is not null;

    /// <param name="pids">Process ids, for finding GPU clients.</param>
    public double? Sample(IReadOnlyList<int> pids)
    {
        var loads = new List<double>(4);
        foreach (string file in _busyFiles)
        {
            if (_proc.TryRead(file, out ReadOnlySpan<char> text) && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
            {
                loads.Add(percent);
            }
        }

        if (_intel?.Sample(pids) is { } intel)
        {
            loads.Add(intel);
        }

        if (_nvidiaSmi is not null)
        {
            SampleNvidia(loads);
        }

        return Combine(loads);
    }

    private void SampleNvidia(List<double> loads)
    {
        long now = _clock.GetTimestamp();
        if (_nvidiaFailedAt is { } failedAt && _clock.GetElapsedTime(failedAt, now) < s_nvidiaRetry)
        {
            return;
        }

        int before = loads.Count;
        if (_run(_nvidiaSmi!, s_nvidiaArguments, TimeSpan.FromSeconds(2)) is { } output)
        {
            loads.AddRange(ParseNvidiaSmi(output));
        }

        _nvidiaFailedAt = loads.Count > before ? null : now;
    }

    /// <summary>The busiest GPU, clamped to 0 to 100, or null when there is none.</summary>
    internal static double? Combine(IReadOnlyList<double> loads)
    {
        double? busiest = null;
        foreach (double load in loads)
        {
            busiest = Math.Max(busiest ?? 0, Math.Clamp(load, 0, 100));
        }

        return busiest;
    }

    /// <summary>
    /// One GPU per line of <c>nvidia-smi --query-gpu=utilization.gpu --format=csv,noheader,nounits</c>.
    /// A GPU that reports "[N/A]" or "[Not Supported]" is left out.
    /// </summary>
    internal static List<double> ParseNvidiaSmi(string output)
    {
        var loads = new List<double>();
        foreach (ReadOnlySpan<char> line in output.AsSpan().EnumerateLines())
        {
            if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
            {
                loads.Add(percent);
            }
        }

        return loads;
    }

    /// <summary>The first <c>PATH</c> entry that holds <paramref name="program"/>, or null.</summary>
    internal static string? FindOnPath(string program, string? path)
    {
        foreach (string folder in (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(folder, program);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The <c>cardN</c> entries of <c>/sys/class/drm</c>; the <c>cardN-DP-1</c> connectors next to them are not GPUs.</summary>
    private static List<string> Cards(string drmFolder)
    {
        var cards = new List<string>();
        foreach (string folder in Folders(drmFolder, "card*"))
        {
            ReadOnlySpan<char> digits = Path.GetFileName(folder.AsSpan())[4..];
            if (!digits.IsEmpty && digits.IndexOfAnyExceptInRange('0', '9') < 0)
            {
                cards.Add(folder);
            }
        }

        return cards;
    }

    private static bool HasNvidia(string pciFolder)
    {
        foreach (string device in Folders(pciFolder, "*"))
        {
            // Class 0x03xxxx is a display controller (VGA, or a 3D controller without outputs).
            if (ReadLine(Path.Combine(device, "vendor")) == NvidiaVendor
                && ReadLine(Path.Combine(device, "class"))?.StartsWith("0x03", StringComparison.Ordinal) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> Folders(string parent, string pattern)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(parent, pattern)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // No such folder: this machine has no such device.
        }
    }

    private static string? ReadLine(string path)
    {
        try
        {
            return File.ReadLines(path).FirstOrDefault()?.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
