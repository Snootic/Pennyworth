using Pennyworth.Scanning;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Measures the bytes of a filesystem that a scan of its folders cannot see, without ever asking for
/// privileges: no password prompt, no <c>pkexec</c>, no polkit. A source the current user cannot read
/// contributes nothing; the report's fields map onto the Windows names this way:
/// <list type="bullet">
/// <item><description>
/// <see cref="HiddenSpaceReport.RestorePoints"/>: space only btrfs snapshots hold, i.e. what deleting
/// all of them would free. It is the exclusive size of the snapshot qgroup that the snapper config of the
/// filesystem names (<c>QGROUP</c>), read from <c>/sys/fs/btrfs/&lt;uuid&gt;/qgroups</c>. <c>btrfs subvolume
/// list</c>, <c>btrfs qgroup show</c> and <c>snapper list</c> all need root, sysfs does not. Null without
/// btrfs, without a snapper config with quotas, or while the quota numbers are marked inconsistent.
/// </description></item>
/// <item><description>
/// <see cref="HiddenSpaceReport.NtfsMetadata"/> (the name is kept for compatibility): filesystem
/// metadata plus reserved blocks. That is the root reserve (<c>f_bfree - f_bavail</c> of <c>statvfs</c>;
/// on btrfs it is the unused part of the metadata chunks), plus, on btrfs, the metadata and system
/// chunks in use (<c>btrfs filesystem usage -b</c>), or, on a filesystem backed by a block device,
/// what that device holds beyond the size <c>statvfs</c> reports (journal, inode tables, bitmaps). The
/// latter assumes the filesystem fills its device: space the filesystem does not use at all is counted.
/// </description></item>
/// </list>
/// Both fields null is <see cref="UnlockOutcome.Failed"/>; anything else is
/// <see cref="UnlockOutcome.Measured"/>. <see cref="UnlockOutcome.Declined"/> never happens.
/// </summary>
internal sealed class LinuxSystemSpaceService : IHiddenSpaceService
{
    private const string MountInfo = "/proc/self/mountinfo";
    private const string BlockDevices = "/sys/dev/block";
    private const string BtrfsSysfs = "/sys/fs/btrfs";
    private const string SnapperConfigs = "/etc/snapper/configs";
    private const string Btrfs = "btrfs";

    private readonly SystemSpaceSources _sources;

    public LinuxSystemSpaceService()
        : this(SystemSpaceSources.Real)
    {
    }

    internal LinuxSystemSpaceService(SystemSpaceSources sources) => _sources = sources;

    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<UnlockResult> MeasureAsync(string driveRoot, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                HiddenSpaceReport report = Measure(driveRoot, cancellationToken);
                return report is { RestorePoints: null, NtfsMetadata: null }
                    ? new UnlockResult(UnlockOutcome.Failed)
                    : new UnlockResult(UnlockOutcome.Measured, report);
            },
            cancellationToken);

    internal HiddenSpaceReport Measure(string driveRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Normalize(driveRoot) is not { } root || _sources.StatVfs(root) is not { } counters)
        {
            return new HiddenSpaceReport(null, null); // Not a path, or not one that exists: nothing to measure.
        }

        IReadOnlyList<MountEntry> mounts = MountTable.Parse(_sources.ReadText(MountInfo) ?? "");
        MountEntry? mount = SystemSpaceParsers.FindMount(mounts, root);

        cancellationToken.ThrowIfCancellationRequested();
        long? overhead = MeasureOverhead(root, mount, counters);

        cancellationToken.ThrowIfCancellationRequested();
        long? snapshots = mount?.FileSystemType == Btrfs ? MeasureSnapshots(mount, mounts) : null;

        return new HiddenSpaceReport(snapshots, overhead);
    }

    private long? MeasureOverhead(string root, MountEntry? mount, VfsCounters counters)
    {
        long? total = SystemSpaceParsers.ReservedBytes(counters);

        if (mount is { FileSystemType: not Btrfs })
        {
            // A btrfs mount has an anonymous device number with no entry here, and its size is not a
            // single device's anyway; every other filesystem reports its own device.
            string? sectors = _sources.ReadText(Path.Combine(BlockDevices, mount.MajorMinor, "size"));
            total = Add(total, SystemSpaceParsers.DeviceOverheadBytes(sectors, counters));
        }
        else if (mount is not null && _sources.RunBtrfsUsage(root) is { } usage)
        {
            total = Add(total, SystemSpaceParsers.ParseBtrfsMetadataBytes(usage));
        }

        return total;
    }

    /// <summary>
    /// Sum of the exclusive bytes of the snapshot qgroups of every snapper config whose subvolume lives on
    /// the same btrfs filesystem as <paramref name="mount"/>.
    /// </summary>
    private long? MeasureSnapshots(MountEntry mount, IReadOnlyList<MountEntry> mounts)
    {
        var qgroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in _sources.ListNames(SnapperConfigs))
        {
            if (_sources.ReadText(Path.Combine(SnapperConfigs, name)) is not { } text
                || SystemSpaceParsers.ParseSnapperConfig(text) is not { } config
                || SystemSpaceParsers.QgroupDirectory(config.Qgroup) is not { } qgroup
                || Normalize(config.Subvolume) is not { } subvolume
                || SystemSpaceParsers.FindMount(mounts, subvolume) is not { FileSystemType: Btrfs } configMount
                || configMount.Source != mount.Source)
            {
                continue;
            }

            qgroups.Add(qgroup);
        }

        if (qgroups.Count == 0 || FindBtrfsFilesystem(mount.Source) is not { } filesystem)
        {
            return null;
        }

        string qgroupRoot = Path.Combine(BtrfsSysfs, filesystem, "qgroups");
        if (_sources.ReadText(Path.Combine(qgroupRoot, "inconsistent"))?.Trim() == "1")
        {
            return null; // The kernel flags its own numbers as stale until the next rescan.
        }

        long? total = null;
        foreach (string qgroup in qgroups)
        {
            total = Add(total, SystemSpaceParsers.ParseCounter(_sources.ReadText(Path.Combine(qgroupRoot, qgroup, "exclusive"))));
        }

        return total;
    }

    /// <summary>The sysfs folder (the filesystem's UUID) whose <c>devices</c> lists the mount source.</summary>
    private string? FindBtrfsFilesystem(string source)
    {
        string device = _sources.DeviceName(source);
        foreach (string name in _sources.ListNames(BtrfsSysfs))
        {
            if (_sources.ListNames(Path.Combine(BtrfsSysfs, name, "devices")).Contains(device))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>The absolute path without a trailing separator (<c>/</c> stays <c>/</c>); null when it is not one.</summary>
    private static string? Normalize(string path) =>
        !string.IsNullOrEmpty(path) && !path.Contains('\0') && Path.IsPathRooted(path)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            : null;

    /// <summary>Adds a measured amount; a part that could not be measured leaves the sum as it was.</summary>
    private static long? Add(long? total, long? part) =>
        part is null ? total : total is null ? part : total.Value > long.MaxValue - part.Value ? long.MaxValue : total.Value + part.Value;
}
