using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pennyworth.Platform.Linux;

/// <summary>One line of <c>/proc/self/mountinfo</c>, reduced to what system-space measuring uses.</summary>
/// <param name="MajorMinor">The <c>maj:min</c> of the backing device. Btrfs gives every subvolume mount its own anonymous one.</param>
/// <param name="MountPoint">Unescaped mount point.</param>
/// <param name="Source">The mount source (<c>/dev/nvme0n1p2</c>), the same for every subvolume of one btrfs filesystem.</param>
internal sealed record MountEntry(string MajorMinor, string MountPoint, string FsType, string Source);

/// <summary>The two keys of a snapper config that say where its snapshots' space is accounted.</summary>
/// <param name="Subvolume">The mounted subvolume the config snapshots (<c>/</c>).</param>
/// <param name="Qgroup">The btrfs qgroup that holds every snapshot (<c>1/0</c>), empty when quotas are not set up.</param>
internal sealed record SnapperConfig(string Subvolume, string Qgroup);

/// <summary>Pure functions over text: every parser and every piece of arithmetic of the system-space measurement.</summary>
internal static partial class SystemSpaceParsers
{
    private const long SectorBytes = 512;

    /// <summary>Entries of <c>/proc/self/mountinfo</c>; lines that do not parse are skipped.</summary>
    internal static List<MountEntry> ParseMountInfo(string text)
    {
        var entries = new List<MountEntry>();
        foreach (string line in text.Split('\n'))
        {
            // "36 35 98:0 /root /mnt rw,noatime master:1 - ext3 /dev/root rw,errors=continue"
            // Optional fields sit between the mount options and a lone "-"; the filesystem follows it.
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int separator = fields.Length < 9 ? -1 : Array.IndexOf(fields, "-", 6);
            if (separator < 0 || separator + 2 >= fields.Length || !fields[2].Contains(':'))
            {
                continue;
            }

            entries.Add(new MountEntry(fields[2], Unescape(fields[4]), fields[separator + 1], Unescape(fields[separator + 2])));
        }

        return entries;
    }

    /// <summary>
    /// The mount that holds <paramref name="path"/>: the longest mount point that is the path or one of
    /// its parent folders. Where several mounts are stacked on one point the latest one is on top.
    /// </summary>
    internal static MountEntry? FindMount(IReadOnlyList<MountEntry> mounts, string path)
    {
        MountEntry? best = null;
        foreach (MountEntry mount in mounts)
        {
            bool holds = mount.MountPoint == "/"
                || path == mount.MountPoint
                || path.StartsWith(mount.MountPoint + "/", StringComparison.Ordinal);
            if (holds && (best is null || mount.MountPoint.Length >= best.MountPoint.Length))
            {
                best = mount;
            }
        }

        return best;
    }

    /// <summary>
    /// Bytes btrfs spends on metadata and system chunks as <c>btrfs filesystem usage -b</c> prints them:
    /// the <c>Used</c> of every <c>Metadata</c> and <c>System</c> line times how many copies the profile
    /// keeps (DUP and RAID1 write two). Null when the text has no such line, e.g. the command failed.
    /// </summary>
    internal static long? ParseBtrfsMetadataBytes(string output)
    {
        double? statedRatio = null;
        if (MetadataRatio().Match(output) is { Success: true } ratioLine
            && double.TryParse(ratioLine.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            statedRatio = parsed;
        }

        long? total = null;
        foreach (Match chunk in ChunkLine().Matches(output))
        {
            if (!long.TryParse(chunk.Groups["used"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long used))
            {
                continue;
            }

            double copies = CopiesOf(chunk.Groups["profile"].Value) ?? statedRatio ?? 1;
            double bytes = Math.Round(used * copies);
            if (bytes >= long.MaxValue || (total ?? 0) > long.MaxValue - bytes)
            {
                return null;
            }

            total = (total ?? 0) + (long)bytes;
        }

        return total;
    }

    /// <summary>
    /// Bytes the filesystem keeps for root (<c>f_bfree - f_bavail</c>) in <paramref name="counters"/>' own
    /// unit. A filesystem that reports more available than free reserves nothing, and one that reserves
    /// more than its whole size is lying: null.
    /// </summary>
    internal static long? ReservedBytes(VfsCounters counters)
    {
        ulong unit = counters.FragmentSize != 0 ? counters.FragmentSize : counters.BlockSize;
        if (unit == 0)
        {
            return null;
        }

        ulong reserved = counters.FreeBlocks > counters.AvailableBlocks ? counters.FreeBlocks - counters.AvailableBlocks : 0;
        return reserved > counters.Blocks ? null : ToBytes(reserved, unit);
    }

    /// <summary>
    /// What a device holds beyond the blocks <c>statvfs</c> calls the filesystem's size: superblocks,
    /// allocation bitmaps, inode tables, the journal or log. <paramref name="sectorsText"/> is the content
    /// of <c>/sys/dev/block/maj:min/size</c> (512-byte sectors). Null when it is not a number or the
    /// filesystem claims to be larger than its device.
    /// </summary>
    internal static long? DeviceOverheadBytes(string? sectorsText, VfsCounters counters)
    {
        ulong unit = counters.FragmentSize != 0 ? counters.FragmentSize : counters.BlockSize;
        if (unit == 0
            || !long.TryParse(sectorsText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long sectors)
            || sectors > long.MaxValue / SectorBytes
            || ToBytes(counters.Blocks, unit) is not { } fileSystemBytes)
        {
            return null;
        }

        long deviceBytes = sectors * SectorBytes;
        return deviceBytes >= fileSystemBytes ? deviceBytes - fileSystemBytes : null;
    }

    /// <summary>
    /// <c>KEY="value"</c> assignments of a snapper config. A missing <c>SUBVOLUME</c> means it is not a
    /// snapper config at all.
    /// </summary>
    internal static SnapperConfig? ParseSnapperConfig(string text)
    {
        string? subvolume = null;
        string qgroup = "";
        foreach (Match assignment in Assignment().Matches(text))
        {
            string value = assignment.Groups["double"].Success ? assignment.Groups["double"].Value
                : assignment.Groups["single"].Success ? assignment.Groups["single"].Value
                : assignment.Groups["bare"].Value;
            switch (assignment.Groups["key"].Value)
            {
                case "SUBVOLUME":
                    subvolume = value;
                    break;
                case "QGROUP":
                    qgroup = value;
                    break;
            }
        }

        return string.IsNullOrEmpty(subvolume) ? null : new SnapperConfig(subvolume, qgroup);
    }

    /// <summary>
    /// Folder name sysfs gives a qgroup: <c>1/0</c> is <c>1_0</c>. Null for anything that is not
    /// <c>level/id</c>, so a config value never becomes part of a path unchecked.
    /// </summary>
    internal static string? QgroupDirectory(string qgroup) => QgroupId().IsMatch(qgroup) ? qgroup.Replace('/', '_') : null;

    /// <summary>A sysfs byte counter (<c>exclusive</c>); null when absent or not a number.</summary>
    internal static long? ParseCounter(string? text) =>
        long.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : null;

    private static long? ToBytes(ulong blocks, ulong unit) => blocks <= (ulong)long.MaxValue / unit ? (long)(blocks * unit) : null;

    private static double? CopiesOf(string profile) => profile switch
    {
        "single" or "RAID0" => 1,
        "DUP" or "RAID1" or "RAID10" => 2,
        "RAID1C3" => 3,
        "RAID1C4" => 4,
        _ => null,
    };

    /// <summary>mountinfo writes space, tab, newline and backslash as three-digit octal escapes.</summary>
    private static string Unescape(string field)
    {
        if (!field.Contains('\\'))
        {
            return field;
        }

        var text = new StringBuilder(field.Length);
        for (int i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length && IsOctal(field[i + 1]) && IsOctal(field[i + 2]) && IsOctal(field[i + 3]))
            {
                text.Append((char)Convert.ToInt32(field.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                text.Append(field[i]);
            }
        }

        return text.ToString();
    }

    private static bool IsOctal(char c) => c is >= '0' and <= '7';

    [GeneratedRegex(@"^(?:Metadata|System),(?<profile>[A-Za-z0-9]+): Size:\d+, Used:(?<used>\d+)", RegexOptions.Multiline)]
    private static partial Regex ChunkLine();

    [GeneratedRegex(@"^\s*Metadata ratio:\s+([0-9.]+)", RegexOptions.Multiline)]
    private static partial Regex MetadataRatio();

    [GeneratedRegex(@"^[ \t]*(?<key>[A-Z_]+)=(?:""(?<double>[^""\n]*)""|'(?<single>[^'\n]*)'|(?<bare>[^\s#]*))", RegexOptions.Multiline)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^[0-9]+/[0-9]+\z")]
    private static partial Regex QgroupId();
}
