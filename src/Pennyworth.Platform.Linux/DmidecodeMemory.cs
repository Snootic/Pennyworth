using System.Globalization;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The memory modules from the text <c>dmidecode -t 17</c> prints, for when the raw firmware table
/// cannot be read directly. One "Memory Device" block per slot; an empty slot says "No Module Installed".
/// The fields come out the way <see cref="Smbios"/> reports them, so both paths show the same rows.
/// </summary>
internal static class DmidecodeMemory
{
    /// <summary>The populated modules and the number of slots (every Memory Device block counts, empty or not).</summary>
    public static (IReadOnlyList<MemoryModule> Modules, int? Slots) Parse(string text)
    {
        var modules = new List<MemoryModule>();
        int slots = 0;
        Dictionary<string, string>? device = null;

        void Finish()
        {
            if (device is null)
            {
                return;
            }

            slots++;
            if (ToModule(device) is { } module)
            {
                modules.Add(module);
            }

            device = null;
        }

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed == "Memory Device")
            {
                Finish();
                device = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            else if (trimmed.StartsWith("Handle ", StringComparison.Ordinal))
            {
                Finish(); // The next structure, whatever it is, ends this device.
            }
            else if (device is not null && trimmed.IndexOf(':') is var colon and > 0)
            {
                device[trimmed[..colon]] = trimmed[(colon + 1)..].Trim();
            }
        }

        Finish();
        return (modules, slots > 0 ? slots : null);
    }

    private static MemoryModule? ToModule(Dictionary<string, string> device)
    {
        long size = device.TryGetValue("Size", out string? sizeText) ? Size(sizeText) : 0;
        if (size == 0)
        {
            return null;
        }

        string? Value(string key) => device.TryGetValue(key, out string? value) ? Dmi.Clean(value) : null;
        return new MemoryModule(
            Value("Locator") ?? Value("Bank Locator") ?? "?",
            size,
            MemoryType(Value("Type")),
            Speed(Value("Speed")),
            Speed(Value("Configured Memory Speed") ?? Value("Configured Clock Speed")),
            Value("Manufacturer"),
            Value("Part Number"),
            FormFactor(Value("Form Factor")));
    }

    /// <summary>"16 GB", "8192 MB", "512 kB"; 0 for "No Module Installed" and anything else.</summary>
    private static long Size(string text)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long amount))
        {
            return 0;
        }

        long unit = parts[1] switch
        {
            "GB" => 1L << 30,
            "MB" => 1L << 20,
            "kB" => 1L << 10,
            _ => 0,
        };
        return unit > 0 && amount <= long.MaxValue / unit ? amount * unit : 0;
    }

    /// <summary>"3200 MT/s", or "2133 MHz" from older dmidecode, which meant the same number.</summary>
    private static int? Speed(string? text)
    {
        string? number = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int speed) && speed > 0 ? speed : null;
    }

    private static string? MemoryType(string? type) => type switch
    {
        "SDRAM" or "DDR" or "DDR2" or "DDR3" or "DDR4" or "DDR5" or "LPDDR" or "LPDDR2" or "LPDDR3" or "LPDDR4" or "LPDDR5" => type,
        _ => null,
    };

    private static string? FormFactor(string? form) => form switch
    {
        "DIMM" => "DIMM",
        "SODIMM" => "SO-DIMM",
        _ => null,
    };
}
