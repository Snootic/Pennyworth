using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The processor section: <c>/proc/cpuinfo</c> for the name and the core/thread/socket layout, the
/// per-CPU cache directories in sysfs for the cache sizes and <c>cpufreq</c> for the speed.
/// </summary>
internal static partial class ProcCpu
{
    private const string CpuFolder = "/sys/devices/system/cpu/";

    /// <param name="Cores">Distinct (package, core) pairs; null when cpuinfo names no cores (ARM).</param>
    internal sealed record CpuInfo(string? Name, string? Vendor, int Threads, int? Cores, int Sockets);

    /// <param name="Type">"Data", "Instruction" or "Unified".</param>
    /// <param name="SharedCpus">The CPUs sharing this cache ("0,14"): what tells two cores' private caches apart.</param>
    internal sealed record CacheInstance(int Level, string Type, string SharedCpus, long Bytes);

    /// <summary>
    /// Parses <c>/proc/cpuinfo</c>. Each <c>processor</c> line starts a logical CPU, so the thread count
    /// is the number of online CPUs; cores are the distinct <c>physical id</c>/<c>core id</c> pairs
    /// (two sockets may both have a core 0), and sockets the distinct <c>physical id</c> values.
    /// </summary>
    public static CpuInfo Parse(string text)
    {
        string? name = null, vendor = null;
        int threads = 0;
        var cores = new HashSet<(string Package, string Core)>();
        var packages = new HashSet<string>();
        string package = "0";

        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "processor":
                    threads++;
                    package = "0";
                    break;
                case "physical id":
                    package = value;
                    packages.Add(value);
                    break;
                case "core id":
                    cores.Add((package, value));
                    break;
                // Intel/AMD, then the names other architectures use; the first one found wins.
                case "model name" or "cpu model" or "Hardware" or "Processor" when name is null && value.Length > 0 && !char.IsAsciiDigit(value[0]):
                    name = Whitespace().Replace(value, " ");
                    break;
                case "vendor_id" when vendor is null && value.Length > 0:
                    vendor = value;
                    break;
            }
        }

        return new CpuInfo(name, vendor, threads, cores.Count > 0 ? cores.Count : null, Math.Max(packages.Count, 1));
    }

    /// <summary>"32K", "35840K", "1M" as sysfs prints cache sizes, in bytes; 0 when unreadable.</summary>
    public static long ParseCacheSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        string trimmed = text.Trim();
        long unit = char.ToUpperInvariant(trimmed[^1]) switch { 'K' => 1024L, 'M' => 1024L * 1024, 'G' => 1024L * 1024 * 1024, _ => 1 };
        string digits = unit == 1 ? trimmed : trimmed[..^1];
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value <= long.MaxValue / unit ? value * unit : 0;
    }

    /// <summary>
    /// Total cache per level over every core, like the Windows figure. Sysfs lists the caches each CPU
    /// sees, so a shared L3 (or an L2 shared by two threads) shows up once per CPU: instances are
    /// counted once per distinct (level, type, sharing set). Data and instruction L1 add up.
    /// </summary>
    public static (long L1, long L2, long L3) SumCaches(IEnumerable<CacheInstance> caches)
    {
        long[] totals = new long[4];
        foreach (CacheInstance cache in caches.DistinctBy(c => (c.Level, c.Type, c.SharedCpus)))
        {
            if (cache.Level is >= 1 and <= 3)
            {
                totals[cache.Level] += cache.Bytes;
            }
        }

        return (totals[1], totals[2], totals[3]);
    }

    /// <summary>The "@ 2.60GHz" a processor name carries, in MHz: the rated base speed.</summary>
    public static int? NominalMhz(string? name)
    {
        Match match = NominalSpeed().Match(name ?? "");
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? (int)Math.Round(value * (match.Groups[2].Value is "G" or "g" ? 1000 : 1))
            : null;
    }

    public static CpuSpecs Read(SpecsRoot fs)
    {
        CpuInfo info = Parse(fs.ReadText("/proc/cpuinfo") ?? "");
        int threads = info.Threads > 0 ? info.Threads : Environment.ProcessorCount;
        (long l1, long l2, long l3) = SumCaches(ReadCaches(fs));
        return new CpuSpecs(
            info.Name ?? "?",
            info.Vendor,
            Math.Min(info.Cores ?? threads, threads),
            threads,
            info.Sockets,
            BaseMhz(fs, info.Name),
            l1,
            l2,
            l3,
            RuntimeInformation.ProcessArchitecture.ToString(),
            null); // The socket designation ("LGA2011-3") is only in the SMBIOS tables, which need root.
    }

    /// <summary>
    /// The rated speed: <c>base_frequency</c> where the driver publishes it (intel_pstate), else the
    /// "@ 2.60GHz" in the name, else the highest frequency cpufreq allows. That last one is the boost
    /// clock, not the base clock, but on a processor whose name has no speed it is the closest figure
    /// the system offers without root.
    /// </summary>
    private static int? BaseMhz(SpecsRoot fs, string? name)
    {
        if (fs.ReadNumber(CpuFolder + "cpu0/cpufreq/base_frequency") is > 0 and long kilohertz)
        {
            return (int)(kilohertz / 1000);
        }

        return NominalMhz(name) ?? (fs.ReadNumber(CpuFolder + "cpu0/cpufreq/cpuinfo_max_freq") is > 0 and long max ? (int)(max / 1000) : null);
    }

    private static IEnumerable<CacheInstance> ReadCaches(SpecsRoot fs)
    {
        foreach (string cpu in fs.Names(CpuFolder).Where(n => CpuDirectory().IsMatch(n)))
        {
            foreach (string index in fs.Names($"{CpuFolder}{cpu}/cache").Where(n => n.StartsWith("index", StringComparison.Ordinal)))
            {
                string folder = $"{CpuFolder}{cpu}/cache/{index}/";
                if (fs.ReadNumber(folder + "level") is { } level && fs.ReadText(folder + "size") is { } size)
                {
                    yield return new CacheInstance((int)level, fs.ReadText(folder + "type") ?? "", fs.ReadText(folder + "shared_cpu_list") ?? cpu, ParseCacheSize(size));
                }
            }
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"@\s*(\d+(?:\.\d+)?)\s*([GM])Hz", RegexOptions.IgnoreCase)]
    private static partial Regex NominalSpeed();

    [GeneratedRegex(@"^cpu\d+$")]
    private static partial Regex CpuDirectory();
}
