using System.Globalization;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The memory section. The total is <c>MemTotal</c> from <c>/proc/meminfo</c>: the RAM the kernel can
/// use, a little under what is installed (firmware and the kernel reserve some), like Windows'
/// figure. Slots and modules are only in the SMBIOS tables, which need root, so this read leaves them
/// unknown; <see cref="PkexecMemoryReader"/> reads them when the person asks.
/// </summary>
internal static class MemorySection
{
    /// <summary><c>MemTotal:   32681548 kB</c> in bytes (the kernel's "kB" is 1024 bytes); 0 when absent.</summary>
    public static long ParseTotal(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line["MemTotal:".Length..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long amount))
            {
                long unit = parts.Length > 1 && parts[1].Equals("kB", StringComparison.OrdinalIgnoreCase) ? 1024 : 1;
                return amount <= long.MaxValue / unit ? amount * unit : 0;
            }
        }

        return 0;
    }

    public static MemorySpecs Read(SpecsRoot fs) => new(ParseTotal(fs.ReadText("/proc/meminfo") ?? ""), null, []);
}
