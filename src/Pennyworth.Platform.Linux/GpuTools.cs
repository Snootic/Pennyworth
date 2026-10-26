using System.Globalization;

namespace Pennyworth.Platform.Linux;

/// <summary>What <c>nvidia-smi</c> knows about one card; keyed by the PCI slot it sits in.</summary>
internal sealed record NvidiaGpu(string Slot, long? MemoryBytes, string? DriverVersion);

/// <summary>One physical device in <c>vulkaninfo</c>'s listing.</summary>
/// <param name="LocalHeapBytes">The largest device-local memory heap: the video memory of a discrete card, null when none is listed.</param>
internal sealed record VulkanGpu(int VendorId, int DeviceId, bool IsDiscrete, long? LocalHeapBytes);

/// <summary>
/// The two helper programs that know a card's video memory when sysfs does not. They are optional:
/// each parser takes the program's text and a missing program is an empty list.
/// </summary>
internal static class GpuTools
{
    public static readonly string[] NvidiaArguments = ["--query-gpu=pci.bus_id,memory.total,driver_version", "--format=csv,noheader,nounits"];

    /// <summary>
    /// <c>nvidia-smi --query-gpu=pci.bus_id,memory.total,driver_version --format=csv,noheader,nounits</c>:
    /// <c>00000000:01:00.0, 12288, 550.54.14</c>, memory in MiB. Rows with "[N/A]" or "[Not Supported]" keep their other fields.
    /// </summary>
    public static IReadOnlyList<NvidiaGpu> ParseNvidiaSmi(string? text)
    {
        var gpus = new List<NvidiaGpu>();
        foreach (string line in (text ?? "").Split('\n'))
        {
            string[] fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 3 || fields[0].Length == 0)
            {
                continue;
            }

            long? memory = long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long mebibytes) && mebibytes > 0
                ? mebibytes * 1024 * 1024
                : null;
            gpus.Add(new NvidiaGpu(PciNames.NormalizeSlot(fields[0]), memory, fields[2].Length > 0 && !fields[2].StartsWith('[') ? fields[2] : null));
        }

        return gpus;
    }

    /// <summary>
    /// Parses the full <c>vulkaninfo</c> listing. Each <c>GPUn:</c> block carries the ids and the device
    /// type, and a <c>memoryHeaps[k]:</c> list whose entries have a <c>size</c> and flags; the heap with
    /// <c>MEMORY_HEAP_DEVICE_LOCAL_BIT</c> is the card's own memory. (An integrated GPU's device-local
    /// heap is just shared system RAM, which is why callers only trust it for discrete cards.)
    /// </summary>
    public static IReadOnlyList<VulkanGpu> ParseVulkanInfo(string? text)
    {
        var gpus = new List<VulkanGpu>();
        int vendor = 0, device = 0;
        bool discrete = false, inGpu = false;
        long? heapSize = null, local = null;

        void Finish()
        {
            if (inGpu)
            {
                gpus.Add(new VulkanGpu(vendor, device, discrete, local));
            }

            (vendor, device, discrete, heapSize, local) = (0, 0, false, null, null);
        }

        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 3 && line.StartsWith("GPU", StringComparison.Ordinal) && line.EndsWith(':') && line[3..^1].All(char.IsAsciiDigit))
            {
                Finish();
                inGpu = true;
            }
            else if (!inGpu)
            {
                continue;
            }
            else if (Value(line, "vendorID") is { } vendorText)
            {
                vendor = Hex(vendorText);
            }
            else if (Value(line, "deviceID") is { } deviceText)
            {
                device = Hex(deviceText);
            }
            else if (Value(line, "deviceType") is { } type)
            {
                discrete = type == "PHYSICAL_DEVICE_TYPE_DISCRETE_GPU";
            }
            else if (line.StartsWith("memoryHeaps[", StringComparison.Ordinal))
            {
                heapSize = null;
            }
            else if (Value(line, "size") is { } size)
            {
                heapSize = long.TryParse(size.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) ? bytes : null;
            }
            else if (line == "MEMORY_HEAP_DEVICE_LOCAL_BIT" && heapSize is { } heap && heap > (local ?? 0))
            {
                local = heap;
            }
        }

        Finish();
        return gpus;
    }

    /// <summary>The text after the <c>=</c> of a <c>key = value</c> line whose key is exactly <paramref name="key"/>.</summary>
    private static string? Value(string line, string key)
    {
        int equals = line.IndexOf('=');
        return equals > 0 && line[..equals].Trim() == key ? line[(equals + 1)..].Trim() : null;
    }

    private static int Hex(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value) ? value : 0;
}
