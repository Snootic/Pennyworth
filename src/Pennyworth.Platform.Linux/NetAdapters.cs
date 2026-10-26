using System.Net.NetworkInformation;
using System.Net.Sockets;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Real network adapters from <c>/sys/class/net</c>: connected, with an IPv4 address, and backed by
/// hardware. Linux lists far more interfaces than there are cards (loopback, container bridges and
/// their <c>veth</c> pairs, VPN tunnels), and none of those is "the network" a person means.
/// </summary>
internal static class NetAdapters
{
    private const string Folder = "/sys/class/net/";
    private const int EthernetType = 1; // ARPHRD_ETHER: what both wired and Wi-Fi adapters report.

    /// <summary>Interface-name prefixes of virtual devices (the bridge, container and tunnel drivers').</summary>
    private static readonly string[] VirtualPrefixes = ["docker", "veth", "virbr", "br-", "tun", "tap", "wg", "podman", "lxc"];

    /// <summary>
    /// "Wi-Fi" or "Ethernet" for an interface that is a physical network card, null for everything else.
    /// A physical card has a <c>device</c> link in sysfs (the PCI, USB or SDIO device behind it);
    /// bridges, bonds, tunnels and container interfaces are created by the kernel and have none, so
    /// that link is the real test and the name prefixes only catch a virtual device that is backed by one anyway.
    /// </summary>
    public static string? Classify(string name, bool hasDevice, bool isWireless, long? linkType)
    {
        if (name == "lo" || VirtualPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) || !hasDevice || linkType != EthernetType)
        {
            return null;
        }

        return isWireless ? "Wi-Fi" : "Ethernet";
    }

    /// <summary>Link speed in bits per second from the <c>speed</c> attribute (megabits); sysfs gives -1 for a link that is down or has no fixed rate (Wi-Fi), which is 0 here.</summary>
    public static long SpeedBps(long? megabits) => megabits is > 0 ? megabits.Value * 1_000_000 : 0;

    /// <summary>"AA:BB:CC:DD:EE:FF" in the Windows reader's upper case; null for the all-zero address some virtual and unconfigured devices report.</summary>
    public static string? FormatMac(string? address) =>
        address is { Length: > 0 } && address.Any(c => c != '0' && c != ':') ? address.ToUpperInvariant() : null;

    public static IReadOnlyList<NetworkSpecs> Read(SpecsRoot fs, Func<string, string?> ipv4Of, Func<IReadOnlyDictionary<string, PciDevice>> pciNames)
    {
        var adapters = new List<NetworkSpecs>();
        foreach (string name in fs.Names(Folder))
        {
            string folder = $"{Folder}{name}/";
            string? kind = Classify(name, fs.Exists(folder + "device"), fs.Exists(folder + "wireless") || fs.Exists(folder + "phy80211"), fs.ReadNumber(folder + "type"));
            // "unknown" is what drivers without carrier detection report for a link that works.
            if (kind is null || fs.ReadText(folder + "operstate") is not ("up" or "unknown") || ipv4Of(name) is not { } ipv4)
            {
                continue;
            }

            adapters.Add(new NetworkSpecs(
                name,
                Description(fs, folder, name, pciNames),
                kind,
                SpeedBps(fs.ReadNumber(folder + "speed")),
                FormatMac(fs.ReadText(folder + "address")),
                ipv4));
        }

        return adapters;
    }

    /// <summary>The card's name: from lspci for a PCI device, else the USB product string, else the kernel driver, else the interface name.</summary>
    private static string Description(SpecsRoot fs, string folder, string name, Func<IReadOnlyDictionary<string, PciDevice>> pciNames)
    {
        if (fs.TargetName(folder + "device") is { } slot && pciNames().TryGetValue(PciNames.NormalizeSlot(slot), out PciDevice? pci))
        {
            return pci.FullName;
        }

        return fs.ReadBesideTarget(folder + "device", "product") ?? fs.TargetName(folder + "device/driver") ?? name;
    }

    /// <summary>The first IPv4 address of every interface that has one, by interface name.</summary>
    public static Func<string, string?> SystemAddresses()
    {
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork) is { } unicast)
                {
                    addresses[adapter.Name] = unicast.Address.ToString();
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No address table: every adapter is then unconnected as far as this screen can tell.
        }

        return name => addresses.GetValueOrDefault(name);
    }
}
