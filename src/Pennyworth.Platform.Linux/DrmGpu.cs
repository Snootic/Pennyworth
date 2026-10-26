using System.Globalization;
using System.Text.RegularExpressions;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>A graphics card and the DRM card number (<c>card0</c>) its connectors hang off.</summary>
internal sealed record DrmCard(int Index, bool IsBootDisplay, GpuSpecs Gpu);

/// <summary>
/// Graphics cards from <c>/sys/class/drm/card*/device</c>. The PCI ids and the driver come from sysfs;
/// the readable name from <c>lspci -mm</c> (else the vendor and device ids); the video memory from the
/// first source that knows it: amdgpu's <c>mem_info_vram_total</c>, <c>nvidia-smi</c>, then the
/// device-local heap Vulkan reports for a discrete card. Intel's xe and i915 export no VRAM size to
/// unprivileged readers (the PCI memory BAR is not it: a 12 GB Arc B580 exposes a 16 GB BAR), so for
/// those Vulkan is the only honest source and the answer is null when it is not installed.
/// </summary>
internal static partial class DrmGpu
{
    private const string DrmFolder = "/sys/class/drm/";
    private const int NvidiaVendor = 0x10DE;

    private sealed record Candidate(int Index, bool IsBoot, string? Slot, int VendorId, int DeviceId, string? Driver, string Name, long? Memory);

    public static IReadOnlyList<DrmCard> Read(
        SpecsRoot fs, Func<IReadOnlyDictionary<string, PciDevice>> pciNames, Func<string, IReadOnlyList<string>, string?> probe)
    {
        var candidates = new List<Candidate>();
        foreach (string entry in fs.Names(DrmFolder))
        {
            if (CardName().Match(entry) is { Success: true } match && ReadCandidate(fs, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), pciNames) is { } candidate
                && !candidates.Any(c => c.Slot is not null && c.Slot == candidate.Slot))
            {
                candidates.Add(candidate);
            }
        }

        IReadOnlyList<NvidiaGpu> nvidia = candidates.Any(c => c.VendorId == NvidiaVendor)
            ? GpuTools.ParseNvidiaSmi(probe("nvidia-smi", GpuTools.NvidiaArguments))
            : [];
        candidates = candidates.Select(c => c with { Memory = c.Memory ?? nvidia.FirstOrDefault(n => n.Slot == c.Slot)?.MemoryBytes }).ToList();

        IReadOnlyList<VulkanGpu> vulkan = candidates.Any(c => c.Memory is null && c.VendorId != 0)
            ? GpuTools.ParseVulkanInfo(probe("vulkaninfo", []))
            : [];
        string? kernel = fs.ReadText("/proc/sys/kernel/osrelease");

        return candidates
            .OrderByDescending(c => c.IsBoot)
            .ThenBy(c => c.Index)
            .Select(c => new DrmCard(
                c.Index,
                c.IsBoot,
                new GpuSpecs(
                    c.Name,
                    c.Memory ?? vulkan.FirstOrDefault(v => v.IsDiscrete && v.VendorId == c.VendorId && v.DeviceId == c.DeviceId)?.LocalHeapBytes,
                    DriverVersion(fs, c.Driver, nvidia.FirstOrDefault(n => n.Slot == c.Slot)?.DriverVersion, kernel),
                    null)))
            .ToList();
    }

    /// <summary>
    /// "xe 7.2.11-1-default"-style text: the kernel driver and its version. A driver built into the kernel
    /// has no version of its own (it is the kernel's), and says so; a driver that publishes one in
    /// <c>/sys/module</c> (nvidia) or reports one through its tool is quoted as is.
    /// </summary>
    internal static string? DriverVersion(SpecsRoot fs, string? driver, string? toolVersion, string? kernelRelease)
    {
        if (driver is null)
        {
            return null;
        }

        string? version = fs.ReadText($"/sys/module/{driver}/version") ?? toolVersion;
        return version is not null ? $"{driver} {version}" : kernelRelease is not null ? $"{driver} (kernel {kernelRelease})" : driver;
    }

    private static Candidate? ReadCandidate(SpecsRoot fs, int index, Func<IReadOnlyDictionary<string, PciDevice>> pciNames)
    {
        string device = $"{DrmFolder}card{index}/device/";
        IReadOnlyDictionary<string, string> uevent = Uevent.Parse(fs.ReadText(device + "uevent"));
        (int vendor, int product) = PciIds(fs, device, uevent);
        string? driver = fs.TargetName(device + "driver") ?? (uevent.TryGetValue("DRIVER", out string? named) ? named : null);
        string? slot = uevent.TryGetValue("PCI_SLOT_NAME", out string? raw) ? PciNames.NormalizeSlot(raw) : null;
        if (vendor == 0 && driver is null)
        {
            return null; // Neither a PCI card nor a driver we can name: nothing to show.
        }

        string name = slot is not null && pciNames().TryGetValue(slot, out PciDevice? pci)
            ? pci.GraphicsName
            : vendor != 0
                ? $"{PciNames.VendorName(vendor) ?? "PCI"} graphics [{vendor:x4}:{product:x4}]"
                : $"GPU ({driver})";
        return new Candidate(
            index,
            fs.ReadText(device + "boot_vga") == "1",
            slot,
            vendor,
            product,
            driver,
            name,
            fs.ReadNumber(device + "mem_info_vram_total") is > 0 and long vram ? vram : null);
    }

    /// <summary>The vendor and device ids: the <c>vendor</c>/<c>device</c> attributes ("0x8086"), else <c>PCI_ID=8086:E20B</c>.</summary>
    private static (int Vendor, int Device) PciIds(SpecsRoot fs, string device, IReadOnlyDictionary<string, string> uevent)
    {
        int vendor = Hex(fs.ReadText(device + "vendor")), product = Hex(fs.ReadText(device + "device"));
        if (vendor == 0 && uevent.TryGetValue("PCI_ID", out string? ids) && ids.Split(':') is [var v, var d])
        {
            (vendor, product) = (Hex(v), Hex(d));
        }

        return (vendor, product);
    }

    private static int Hex(string? text)
    {
        string digits = text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? text[2..] : text ?? "";
        return int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }

    [GeneratedRegex(@"^card(\d+)$")]
    private static partial Regex CardName();
}
