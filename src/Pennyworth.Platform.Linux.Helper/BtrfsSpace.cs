using System.Globalization;
using System.Text.RegularExpressions;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Pure parsers and arithmetic over the text of <c>btrfs subvolume list -s</c>, <c>btrfs qgroup show -pcre --raw</c> and
/// <c>btrfs filesystem usage -b</c>, which only root may run. Nothing here touches the machine, so it is tested with captured output.
/// </summary>
internal static partial class BtrfsSpace
{
    /// <summary>One row of <c>btrfs qgroup show -pcre --raw</c>: the sizes are bytes, the lists are qgroup ids (<c>level/id</c>).</summary>
    private sealed record Qgroup(long Level, ulong Id, long Referenced, long Exclusive, IReadOnlyList<string> Children);

    /// <summary>
    /// What deleting every snapshot would free, from the exclusive sizes of the qgroups that account them. Snapshots are the
    /// subvolume ids of <paramref name="subvolumeListSnapshots"/> (their own qgroups are <c>0/&lt;id&gt;</c>). A tool such as snapper
    /// groups its snapshots under one parent qgroup (<c>1/0</c>), whose exclusive size also counts data shared by several
    /// snapshots only; that parent is the qgroup of level 1 or higher whose members are all snapshot qgroups and who holds the
    /// most of them, and its exclusive size stands for the snapshots it holds. Snapshots outside it count with their own
    /// exclusive size. Without such a parent that is all snapshots. Null when quotas are off, a number cannot be trusted
    /// (a snapshot without a qgroup, an exclusive size above the referenced one, a duplicate row, overflow) or either text is missing.
    /// </summary>
    internal static long? SnapshotBytes(string? subvolumeListSnapshots, string? qgroupShow)
    {
        if (subvolumeListSnapshots is null || qgroupShow is null
            || ParseSnapshotIds(subvolumeListSnapshots) is not { } snapshotIds
            || ParseQgroups(qgroupShow) is not { } qgroups)
        {
            return null;
        }

        if (snapshotIds.Count == 0)
        {
            return 0;
        }

        var snapshotGroups = new Dictionary<string, Qgroup>(StringComparer.Ordinal);
        foreach (ulong id in snapshotIds)
        {
            if (!qgroups.TryGetValue("0/" + id.ToString(CultureInfo.InvariantCulture), out Qgroup? own))
            {
                return null; // Quotas were switched on after the snapshot, or the table is stale: its size is unknown.
            }

            snapshotGroups["0/" + id.ToString(CultureInfo.InvariantCulture)] = own;
        }

        Qgroup? parent = null;
        foreach (Qgroup candidate in qgroups.Values.OrderBy(q => q.Level).ThenBy(q => q.Id))
        {
            if (candidate.Level < 1 || candidate.Children.Count == 0 || !candidate.Children.All(snapshotGroups.ContainsKey))
            {
                continue;
            }

            if (parent is null || candidate.Children.Count > parent.Children.Count)
            {
                parent = candidate;
            }
        }

        long total = 0;
        HashSet<string> covered = [];
        if (parent is not null)
        {
            covered.UnionWith(parent.Children);
            long membersExclusive = 0;
            foreach (string child in parent.Children)
            {
                if (Add(membersExclusive, snapshotGroups[child].Exclusive) is not { } sum)
                {
                    return null;
                }

                membersExclusive = sum;
            }

            if (parent.Exclusive < membersExclusive)
            {
                return null; // A group cannot own less than the sum of what each member owns alone.
            }

            total = parent.Exclusive;
        }

        foreach ((string id, Qgroup own) in snapshotGroups)
        {
            if (covered.Contains(id))
            {
                continue;
            }

            if (Add(total, own.Exclusive) is not { } sum)
            {
                return null;
            }

            total = sum;
        }

        return total;
    }

    /// <summary>
    /// The answer of <c>measure-system-space</c> for a btrfs file system. <paramref name="reservedBytes"/> is the root reserve
    /// from <c>statvfs</c> (on btrfs the unused part of the metadata chunks) and the metadata and system chunks in use come from
    /// <paramref name="usage"/>; either may be missing, and both missing leaves the metadata part null, as the unprivileged
    /// measurement does.
    /// </summary>
    internal static SystemSpaceResponse Build(string fileSystem, long? reservedBytes, string? usage, string? subvolumeListSnapshots, string? qgroupShow) =>
        new(
            HelperProtocol.Version,
            fileSystem,
            SnapshotBytes(subvolumeListSnapshots, qgroupShow),
            AddSaturating(reservedBytes, usage is null ? null : SystemSpaceParsers.ParseBtrfsMetadataBytes(usage)));

    /// <summary>Adds a measured amount; a part that could not be measured leaves the sum as it was, and an overflow stops at the largest value.</summary>
    internal static long? AddSaturating(long? total, long? part) =>
        part is null ? total : total is null ? part : total.Value > long.MaxValue - part.Value ? long.MaxValue : total.Value + part.Value;

    /// <summary>Subvolume ids of <c>btrfs subvolume list -s</c> (<c>ID 258 gen ... path ...</c>); null when the text has content that is no such line. No snapshots is an empty text and an empty set.</summary>
    private static HashSet<ulong>? ParseSnapshotIds(string text)
    {
        var ids = new HashSet<ulong>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (SubvolumeLine().Match(line) is not { Success: true } match
                || !ulong.TryParse(match.Groups["id"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
            {
                return null;
            }

            ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// The rows of <c>btrfs qgroup show -pcre --raw</c>, by qgroup id: <c>id referenced exclusive max-referenced max-exclusive parents children path</c>.
    /// The header and the rule under it are skipped; any other line that is not a row, a row whose exclusive size exceeds its
    /// referenced size, or a duplicate id makes the whole table untrustworthy (null), as does a table without rows (quotas off prints only an error).
    /// </summary>
    private static Dictionary<string, Qgroup>? ParseQgroups(string text)
    {
        var groups = new Dictionary<string, Qgroup>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("Qgroupid", StringComparison.Ordinal) || line[0] == '-')
            {
                continue;
            }

            if (QgroupRow().Match(line) is not { Success: true } row
                || !long.TryParse(row.Groups["level"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long level)
                || !ulong.TryParse(row.Groups["num"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
                || !long.TryParse(row.Groups["rfer"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long referenced)
                || !long.TryParse(row.Groups["excl"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long exclusive)
                || exclusive > referenced)
            {
                return null;
            }

            string key = level.ToString(CultureInfo.InvariantCulture) + "/" + id.ToString(CultureInfo.InvariantCulture);
            string children = row.Groups["child"].Value;
            IReadOnlyList<string> members = children == "---" ? [] : children.Split(',');
            if (!groups.TryAdd(key, new Qgroup(level, id, referenced, exclusive, members)))
            {
                return null;
            }
        }

        return groups.Count == 0 ? null : groups;
    }

    private static long? Add(long left, long right) => left > long.MaxValue - right ? null : left + right;

    [GeneratedRegex(@"^ID (?<id>\d+) gen \d+ ")]
    private static partial Regex SubvolumeLine();

    [GeneratedRegex(@"^(?<level>\d+)/(?<num>\d+)\s+(?<rfer>\d+)\s+(?<excl>\d+)\s+\S+\s+\S+\s+\S+\s+(?<child>[0-9/,]+|---)(?:\s|$)")]
    private static partial Regex QgroupRow();
}
