using System.Text;

namespace Pennyworth.Platform.Linux;

/// <summary>A PCI device as <c>lspci -mm</c> names it.</summary>
internal sealed record PciDevice(string Slot, string Class, string Vendor, string Device)
{
    private static readonly string[] LegalSuffixes =
    [
        " Co., Ltd.", " Co.,Ltd.", " Corporation", " Corp.", " Corp", ", Inc.", " Inc.", " Inc", " Ltd.", " Ltd", " GmbH", " Semiconductor",
    ];

    /// <summary>"Intel Corporation" is "Intel"; "Advanced Micro Devices, Inc. [AMD/ATI]" is "AMD".</summary>
    public string ShortVendor
    {
        get
        {
            string vendor = Vendor.Trim();
            int open = vendor.LastIndexOf('[');
            if (open > 0 && vendor.EndsWith(']'))
            {
                string alias = vendor[(open + 1)..^1];
                vendor = alias.Split('/')[0].Trim();
            }

            bool stripped = true;
            while (stripped)
            {
                stripped = false;
                foreach (string suffix in LegalSuffixes)
                {
                    if (vendor.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && vendor.Length > suffix.Length)
                    {
                        vendor = vendor[..^suffix.Length];
                        stripped = true;
                    }
                }
            }

            return vendor;
        }
    }

    /// <summary>
    /// The marketing name lspci puts in brackets ("Arc B580", "GeForce RTX 3060") or, when there is
    /// none, the chip name ("Battlemage G21").
    /// </summary>
    public string ProductName
    {
        get
        {
            int open = Device.LastIndexOf('[');
            return open >= 0 && Device.EndsWith(']') && open + 1 < Device.Length - 1 ? Device[(open + 1)..^1] : Device;
        }
    }

    /// <summary>A graphics card as people say it: "Intel Arc B580", "NVIDIA GeForce RTX 3060".</summary>
    public string GraphicsName => Join(ShortVendor, ProductName);

    /// <summary>Vendor and the full device text, for a network card: "Realtek RTL8111/8168/8211/8411 PCI Express Gigabit Ethernet Controller".</summary>
    public string FullName => Join(ShortVendor, Device);

    private static string Join(string vendor, string product) =>
        product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? product : $"{vendor} {product}";
}

/// <summary>Reads PCI device names from <c>lspci -mm</c>, the one place a readable name for a vendor/device id pair exists without root.</summary>
internal static class PciNames
{
    /// <summary>
    /// Parses machine-readable lspci output: <c>05:00.0 "VGA compatible controller" "Intel Corporation" "Battlemage G21 [Arc B580]" -p00 ...</c>.
    /// Keyed by the slot as sysfs spells it (<c>0000:05:00.0</c>); lspci leaves the domain out when it is 0000.
    /// </summary>
    public static IReadOnlyDictionary<string, PciDevice> Parse(string? text)
    {
        var devices = new Dictionary<string, PciDevice>(StringComparer.Ordinal);
        foreach (string line in (text ?? "").Split('\n'))
        {
            List<string> fields = Tokens(line);
            if (fields.Count >= 4)
            {
                string slot = NormalizeSlot(fields[0]);
                devices[slot] = new PciDevice(slot, fields[1], fields[2], fields[3]);
            }
        }

        return devices;
    }

    /// <summary>"05:00.0" and "00000000:05:00.0" (nvidia-smi) become "0000:05:00.0"; case folds.</summary>
    public static string NormalizeSlot(string slot)
    {
        string lower = slot.Trim().ToLowerInvariant();
        if (lower.Length > 12)
        {
            return lower[^12..];
        }

        return lower.Count(c => c == ':') == 1 ? "0000:" + lower : lower;
    }

    /// <summary>Quoted fields (with <c>\"</c> and <c>\\</c> escapes) and bare words (the <c>-p00</c> flags).</summary>
    private static List<string> Tokens(string line)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] == ' ')
            {
                i++;
            }
            else if (line[i] == '"')
            {
                var token = new StringBuilder();
                for (i++; i < line.Length && line[i] != '"'; i++)
                {
                    if (line[i] == '\\' && i + 1 < line.Length)
                    {
                        i++;
                    }

                    token.Append(line[i]);
                }

                i++; // The closing quote.
                tokens.Add(token.ToString());
            }
            else
            {
                int end = line.IndexOf(' ', i);
                end = end < 0 ? line.Length : end;
                tokens.Add(line[i..end]);
                i = end;
            }
        }

        return tokens;
    }

    /// <summary>The vendor named by its PCI id, for a machine without lspci.</summary>
    public static string? VendorName(int vendorId) => vendorId switch
    {
        0x8086 => "Intel",
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x1AF4 => "Red Hat (virtio)",
        0x15AD => "VMware",
        0x80EE => "VirtualBox",
        0x1414 => "Microsoft",
        0x1234 => "QEMU",
        0x14E4 => "Broadcom",
        0x5143 => "Qualcomm",
        _ => null,
    };
}
