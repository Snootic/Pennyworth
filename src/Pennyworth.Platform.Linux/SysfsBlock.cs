using System.Globalization;
using System.Text;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Physical disks from <c>/sys/block</c>. A disk is a block device that has a <c>device</c> link (a
/// controller behind it): loop, ram, zram, device-mapper and md devices have none, and optical drives
/// are left out. The model is the full name udev recorded (sysfs truncates ATA models at 16 characters),
/// the bus is read from where the device hangs in the device tree, and SSD/HDD from the
/// <c>rotational</c> flag.
/// </summary>
/// <remarks>
/// <c>DiskSpecs.Letters</c> carries mount points on Linux ("/", "/home", "/mnt/data"): the mounted
/// volumes on this disk's partitions, one entry per volume (the seven btrfs subvolume mounts of one
/// partition are one volume, shown at its shortest mount point). The UI wording for it is Phase 7.
/// </remarks>
internal static class SysfsBlock
{
    private const string BlockFolder = "/sys/block/";
    private const int SectorBytes = 512; // sysfs counts 512-byte sectors whatever the device's own sector size.

    private static readonly string[] VirtualPrefixes = ["loop", "ram", "zram", "dm-", "md", "sr", "fd", "nbd"];

    /// <summary>The <c>E:KEY=value</c> lines of a udev database file (<c>/run/udev/data/b8:16</c>).</summary>
    public static IReadOnlyDictionary<string, string> ParseUdev(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in (text ?? "").Split('\n'))
        {
            int equals = line.IndexOf('=');
            if (line.StartsWith("E:", StringComparison.Ordinal) && equals > 2)
            {
                values[line[2..equals]] = line[(equals + 1)..];
            }
        }

        return values;
    }

    /// <summary>Decodes udev's <c>\xNN</c> escapes (<c>WDC\x20WD5000AZLX</c>) and trims the padding firmware adds.</summary>
    public static string DecodeUdev(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 3 < text.Length && text[i + 1] == 'x'
                && byte.TryParse(text.AsSpan(i + 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte value))
            {
                bytes.Add(value);
                i += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(text.Substring(i, 1)));
            }
        }

        return Encoding.UTF8.GetString([.. bytes]).Trim();
    }

    /// <summary>True for devices that are not a physical disk by name (loop, ram, zram, dm-*, md*, sr*, fd*, nbd*).</summary>
    public static bool IsVirtualName(string name) => VirtualPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    /// <summary>
    /// The bus from where the device sits in the device tree (<c>.../0000:00:1f.2/ata2/host1/target1:0:0/...</c>).
    /// USB is checked before the others because a USB enclosure can sit behind SCSI or ATA layers.
    /// </summary>
    public static string BusOf(string name, string devicePath)
    {
        const string DeviceTree = "/sys/devices/";
        int tree = devicePath.IndexOf(DeviceTree, StringComparison.Ordinal);
        string[] segments = (tree >= 0 ? devicePath[(tree + DeviceTree.Length)..] : devicePath).Split('/');
        bool Has(Func<string, bool> match) => segments.Any(match);

        if (name.StartsWith("nvme", StringComparison.Ordinal) || Has(s => s == "nvme"))
        {
            return "NVMe";
        }

        if (Has(s => s.StartsWith("usb", StringComparison.Ordinal) && s.Length > 3 && char.IsAsciiDigit(s[3])))
        {
            return "USB";
        }

        if (name.StartsWith("mmcblk", StringComparison.Ordinal) || Has(s => s.StartsWith("mmc", StringComparison.Ordinal)))
        {
            return "SD";
        }

        if (Has(s => s.StartsWith("ata", StringComparison.Ordinal) && s.Length > 3 && char.IsAsciiDigit(s[3])))
        {
            return "SATA";
        }

        if (Has(s => s.StartsWith("virtio", StringComparison.Ordinal)))
        {
            return "Virtual";
        }

        if (Has(s => s.StartsWith("end_device-", StringComparison.Ordinal)))
        {
            return "SAS";
        }

        return Has(s => s.StartsWith("host", StringComparison.Ordinal) && s.Length > 4 && char.IsAsciiDigit(s[4])) ? "SCSI" : "—";
    }

    /// <summary>"nvme2n1" sorts before "nvme10n1": digit runs compare by value.</summary>
    internal static string NaturalKey(string name)
    {
        var key = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length;)
        {
            if (char.IsAsciiDigit(name[i]))
            {
                int end = i;
                while (end < name.Length && char.IsAsciiDigit(name[end]))
                {
                    end++;
                }

                key.Append(name.AsSpan(i, end - i).TrimStart('0').ToString().PadLeft(10, '0'));
                i = end;
            }
            else
            {
                key.Append(name[i++]);
            }
        }

        return key.ToString();
    }

    /// <summary>
    /// Which mount points belong to which disk. Mounts of the same source collapse into one (a btrfs
    /// volume mounted as seven subvolumes, or a bind mount) shown at its shortest mount point; sources
    /// that are not block devices (tmpfs, overlay, fuse) are ignored.
    /// </summary>
    /// <param name="disksOf">The disks a source device lives on: a partition has one, an LVM volume may span several.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> MountPointsByDisk(IEnumerable<MountEntry> mounts, Func<string, IEnumerable<string>> disksOf)
    {
        var byDisk = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (IGrouping<string, MountEntry> volume in mounts.Where(m => m.Source.StartsWith("/dev/", StringComparison.Ordinal)).GroupBy(m => m.Source, StringComparer.Ordinal))
        {
            string mountPoint = volume.Select(m => m.MountPoint).OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).First();
            foreach (string disk in disksOf(volume.Key))
            {
                if (!byDisk.TryGetValue(disk, out List<string>? points))
                {
                    byDisk[disk] = points = [];
                }

                points.Add(mountPoint);
            }
        }

        return byDisk.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
    }

    public static IReadOnlyList<DiskSpecs> Read(SpecsRoot fs)
    {
        var disks = new List<string>();
        var owner = new Dictionary<string, string>(StringComparer.Ordinal); // a disk or one of its partitions -> the disk
        foreach (string name in fs.Names(BlockFolder).OrderBy(NaturalKey, StringComparer.Ordinal))
        {
            if (IsVirtualName(name) || !fs.Exists($"{BlockFolder}{name}/device") || (fs.ReadNumber($"{BlockFolder}{name}/size") ?? 0) <= 0)
            {
                continue; // Not a physical disk, or an empty card reader.
            }

            disks.Add(name);
            owner[name] = name;
            foreach (string child in fs.Names($"{BlockFolder}{name}").Where(c => fs.Exists($"{BlockFolder}{name}/{c}/partition")))
            {
                owner[child] = name;
            }
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> mountPoints = MountPointsByDisk(
            MountTable.Parse(fs.ReadText("/proc/self/mountinfo") ?? ""),
            source => DisksOf(fs, fs.TargetName(source) ?? Path.GetFileName(source), owner, 0));

        return disks.Select((name, number) => ReadDisk(fs, name, number, mountPoints.TryGetValue(name, out IReadOnlyList<string>? points) ? points : [])).ToList();
    }

    /// <summary>The disks under a device: itself or its partition's disk, else (device-mapper, md) the disks of its <c>slaves</c>.</summary>
    private static IEnumerable<string> DisksOf(SpecsRoot fs, string device, Dictionary<string, string> owner, int depth)
    {
        if (owner.TryGetValue(device, out string? disk))
        {
            yield return disk;
            yield break;
        }

        if (depth < 4)
        {
            foreach (string slave in fs.Names($"{BlockFolder}{device}/slaves"))
            {
                foreach (string below in DisksOf(fs, slave, owner, depth + 1))
                {
                    yield return below;
                }
            }
        }
    }

    private static DiskSpecs ReadDisk(SpecsRoot fs, string name, int number, IReadOnlyList<string> mountPoints)
    {
        string folder = $"{BlockFolder}{name}/";
        string? majorMinor = fs.ReadText(folder + "dev");
        IReadOnlyDictionary<string, string> udev = ParseUdev(majorMinor is null ? null : fs.ReadText($"/run/udev/data/b{majorMinor}"));

        string? model = udev.TryGetValue("ID_MODEL_ENC", out string? encoded) ? Blank(DecodeUdev(encoded)) : null;
        model ??= fs.ReadText(folder + "device/model");
        string? vendor = fs.ReadText(folder + "device/vendor");
        if (model is not null && vendor is not null && !vendor.Equals("ATA", StringComparison.OrdinalIgnoreCase) && !model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
        {
            model = $"{vendor} {model}"; // A USB stick says "SanDisk" in one field and "Ultra" in the other; "ATA" is the protocol, not a maker.
        }

        string bus = BusOf(name, fs.Target(folder.TrimEnd('/'))?.FullName ?? "");
        return new DiskSpecs(
            number,
            model ?? name,
            (fs.ReadNumber(folder + "size") ?? 0) * SectorBytes,
            bus,
            fs.ReadNumber(folder + "queue/rotational") switch { 0 => true, 1 => false, _ => null },
            mountPoints);
    }

    private static string? Blank(string text) => text.Length == 0 ? null : text;
}
