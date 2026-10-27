namespace Pennyworth.Platform.Linux;

/// <summary>What <see cref="SystemSpace"/> reads from the machine; replaced by fabricated text in tests.</summary>
/// <param name="ReadText">The text of a file, null when it cannot be read.</param>
/// <param name="StatVfs">The <c>statvfs</c> counters of a path, null when they cannot be read.</param>
/// <param name="RunTool">Runs a system tool; see <see cref="ToolRunner"/>.</param>
internal sealed record SystemSpaceInputs(Func<string, string?> ReadText, Func<string, VfsCounters?> StatVfs, ToolRunner RunTool)
{
    internal static SystemSpaceInputs Real { get; } = new(ReadFile, SystemSpaceStatVfs.Read, HelperTools.Run);

    private static string? ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// <c>measure-system-space &lt;mountpoint&gt;</c>: the two quantities of a file system that only root can measure, the space snapshots
/// hold and the metadata, in the same terms as the unprivileged <c>LinuxSystemSpaceService</c> so the app can swap one for the other.
/// </summary>
internal static class SystemSpace
{
    private const string BlockDevices = "/sys/dev/block";
    private const string Btrfs = "btrfs";

    internal static SystemSpaceResponse Run(string mountpoint, CancellationToken cancellationToken) =>
        Run(mountpoint, SystemSpaceInputs.Real, cancellationToken);

    /// <summary>
    /// Measures <paramref name="mountpoint"/>, which the caller has already checked is an absolute, normalised mount point. On
    /// btrfs: the snapshot bytes and the reserve plus metadata chunks (<see cref="BtrfsSpace"/>); on anything else no snapshot bytes
    /// and the reserve plus what the backing device holds beyond the file system's size, exactly as the unprivileged measurement does.
    /// The mount point is always the last argument of a tool and, being absolute, never starts with <c>-</c>.
    /// </summary>
    /// <exception cref="HelperException"><see cref="HelperExit.Refused"/> when <paramref name="mountpoint"/> is not a mount point, <see cref="HelperExit.Failed"/> when its size cannot be read.</exception>
    internal static SystemSpaceResponse Run(string mountpoint, SystemSpaceInputs inputs, CancellationToken cancellationToken)
    {
        if (mountpoint.Length == 0 || mountpoint[0] != '/')
        {
            throw new HelperException(HelperExit.Refused, "the mount point must be an absolute path");
        }

        IReadOnlyList<MountEntry> mounts = MountTable.Parse(inputs.ReadText("/proc/self/mountinfo") ?? "");
        if (SystemSpaceParsers.FindMount(mounts, mountpoint) is not { } mount || mount.MountPoint != mountpoint)
        {
            throw new HelperException(HelperExit.Refused, "the path is not a mount point");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.StatVfs(mountpoint) is not { } counters)
        {
            throw new HelperException(HelperExit.Failed, "the size of the file system cannot be read");
        }

        long? reserved = SystemSpaceParsers.ReservedBytes(counters);
        if (mount.FileSystemType != Btrfs)
        {
            // A btrfs mount has an anonymous device number with no entry in sysfs; every other file system reports its own device.
            string? sectors = inputs.ReadText(Path.Combine(BlockDevices, mount.MajorMinor, "size"));
            long? metadata = BtrfsSpace.AddSaturating(reserved, SystemSpaceParsers.DeviceOverheadBytes(sectors, counters));
            return new SystemSpaceResponse(HelperProtocol.Version, mount.FileSystemType, SnapshotBytes: null, metadata);
        }

        string? snapshots = RunBtrfs(inputs, ["subvolume", "list", "-s", mountpoint], cancellationToken);
        string? qgroups = RunBtrfs(inputs, ["qgroup", "show", "-pcre", "--raw", mountpoint], cancellationToken);
        string? usage = RunBtrfs(inputs, ["filesystem", "usage", "-b", mountpoint], cancellationToken);
        return BtrfsSpace.Build(mount.FileSystemType, reserved, usage, snapshots, qgroups);
    }

    private static string? RunBtrfs(SystemSpaceInputs inputs, string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return inputs.RunTool(HelperTools.Btrfs, arguments, cancellationToken);
    }
}
