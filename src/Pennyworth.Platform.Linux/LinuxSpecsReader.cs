using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Reads the hardware from <c>/proc</c>, <c>/sys</c>, <c>/etc/os-release</c>, the udev database and a
/// few optional helper programs (<c>lspci</c>, <c>nvidia-smi</c>, <c>vulkaninfo</c>, <c>stat</c>).
/// Nothing needs root: what only root can read (SMBIOS memory modules, serial numbers) stays unknown
/// instead of being asked for. A missing source empties its own field and nothing else.
/// </summary>
public sealed class LinuxSpecsReader : ISpecsReader
{
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(10);

    private readonly SpecsRoot _fs;
    private readonly Func<string, IReadOnlyList<string>, string?> _probe;
    private readonly Func<string, string?>? _ipv4Of;

    /// <param name="root">Where <c>/proc</c>, <c>/sys</c> and <c>/etc</c> live; null for the real <c>/</c>. A test hands in a fabricated tree.</param>
    /// <param name="probe">Runs a helper program and returns its output, or null when it is missing or fails; defaults to <see cref="ProcessProbe"/>.</param>
    /// <param name="ipv4Of">The IPv4 address of an interface by name, null when it has none; defaults to the system's address table.</param>
    public LinuxSpecsReader(string? root = null, Func<string, IReadOnlyList<string>, string?>? probe = null, Func<string, string?>? ipv4Of = null)
    {
        _fs = new SpecsRoot(root);
        _probe = probe ?? ((file, arguments) => ProcessProbe.Run(file, arguments, HelperTimeout));
        _ipv4Of = ipv4Of;
    }

    public PcSpecs Read()
    {
        // One lspci run names both the graphics cards and the network cards.
        var pci = new Lazy<IReadOnlyDictionary<string, PciDevice>>(() => Safe(() => PciNames.Parse(_probe("lspci", ["-mm"])), new Dictionary<string, PciDevice>()));
        IReadOnlyList<DrmCard> cards = Safe(() => DrmGpu.Read(_fs, () => pci.Value, _probe), []);
        string architecture = RuntimeInformation.OSArchitecture.ToString();

        return new PcSpecs(
            Safe(() => Dmi.ReadSystem(_fs), new SystemSpecs(Environment.MachineName, Environment.UserName, null, null)),
            Safe(() => OsRelease.Read(_fs, _probe), new OsSpecs("Linux", null, "?", architecture, null, TimeSpan.Zero)),
            Safe(() => ProcCpu.Read(_fs), new CpuSpecs("?", null, Environment.ProcessorCount, Environment.ProcessorCount, 1, null, 0, 0, 0, RuntimeInformation.ProcessArchitecture.ToString(), null)),
            Safe(() => MemorySection.Read(_fs), new MemorySpecs(0, null, [])),
            cards.Select(c => c.Gpu).ToArray(),
            Safe(() => DrmDisplay.Read(_fs, cards), []),
            Safe(() => SysfsBlock.Read(_fs), []),
            Safe(() => Dmi.ReadBoard(_fs), new BoardSpecs(null, null, null, null, null)),
            Safe(() => NetAdapters.Read(_fs, _ipv4Of ?? NetAdapters.SystemAddresses(), () => pci.Value), []),
            Safe(() => PowerSupply.Read(_fs), null));
    }

    /// <summary>One part failing (an unreadable attribute, an odd value) must not hide the rest.</summary>
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException
            or ArgumentException or OverflowException or NetworkInformationException)
        {
            return fallback;
        }
    }
}
