namespace Pennyworth.Platform.Linux;

/// <summary>The safety caps of one <c>measure-folders</c> request: a walk that reaches either stops and reports its folder as not complete.</summary>
/// <param name="MaxEntries">Directory entries examined across the whole request.</param>
/// <param name="MaxDepth">Directory levels below a folder. Each level holds one open descriptor, so this also bounds the descriptors in use.</param>
internal readonly record struct MeasureLimits(long MaxEntries, int MaxDepth)
{
    internal static MeasureLimits Production { get; } = new(50_000_000, 200);
}

/// <summary>
/// <c>measure-folders</c>: how large folders are that the person could not list, counted the way the unprivileged scan counts
/// (<c>PosixDirectoryScanner</c>): allocated bytes are <c>st_blocks * 512</c>, logical bytes <c>st_size</c>, a file with several hard
/// links is counted once for the whole request, a symbolic link is a small file of its own and never followed, directories' own sizes
/// are not counted, and the walk stays on the folder's file system (<c>du -x</c>).
/// <para>
/// The helper runs as root and the paths come from a user, so each folder is opened from <c>/</c> one component at a time with
/// <c>O_NOFOLLOW</c> (a path through a link is left out of the answer rather than followed) and the walk goes down by
/// <c>openat</c> from descriptors it already holds, so a link swapped in while it runs cannot redirect it. Only sizes and counts leave
/// the helper, never a name.
/// </para>
/// </summary>
internal static unsafe class FolderMeasure
{
    /// <summary>Folders of kernel-provided file systems: never worth measuring, and a walk of <c>/proc</c> or <c>/dev</c> would not end sensibly.</summary>
    private static readonly string[] ExcludedRoots = ["/proc", "/sys", "/dev", "/run"];

    private sealed class Counters
    {
        internal long SizeOnDisk;
        internal long LogicalSize;
        internal long Files;
        internal bool Complete = true;
    }

    private sealed class Walker(
        MeasureLimits limits, Func<ulong, ulong, ulong>? deviceOverride, HashSet<(ulong, ulong)> hardLinks, CancellationToken cancellationToken)
    {
        private long _entries;

        /// <summary>True once the request-wide entry cap or a cancellation stopped the walk: nothing more is measured.</summary>
        internal bool Stopped { get; private set; }

        internal ulong EffectiveDevice(in StatxBuffer buffer)
        {
            ulong device = SafeFs.Device(in buffer);
            return deviceOverride?.Invoke(device, buffer.Inode) ?? device;
        }

        /// <summary>Measures the directory <paramref name="fd"/> refers to; takes ownership of the descriptor and closes it.</summary>
        internal void Walk(int fd, ulong device, int depth, Counters counters)
        {
            nint directory = SafeFs.OpenDir(fd);
            if (directory == 0)
            {
                SafeFs.Close(fd);
                counters.Complete = false;
                return;
            }

            try
            {
                List<byte[]>? subdirectories = ListDirectory(directory, device, counters);
                if (subdirectories is null)
                {
                    return;
                }

                int dirFd = SafeFs.DirFd(directory);
                foreach (byte[] name in subdirectories)
                {
                    if (Stopped || cancellationToken.IsCancellationRequested)
                    {
                        Stopped = true;
                        counters.Complete = false;
                        return;
                    }

                    if (depth + 1 > limits.MaxDepth)
                    {
                        counters.Complete = false;
                        continue;
                    }

                    int child;
                    fixed (byte* raw = name)
                    {
                        child = SafeFs.OpenDirectoryAt(dirFd, raw, out int errno);
                        if (child < 0)
                        {
                            if (errno != SafeFs.Enoent)
                            {
                                counters.Complete = false;
                            }

                            continue; // Vanished mid-walk, or not a directory any more: skipped.
                        }
                    }

                    if (SafeFs.StatFd(child, out StatxBuffer buffer) != 0)
                    {
                        SafeFs.Close(child);
                        counters.Complete = false;
                        continue;
                    }

                    if (EffectiveDevice(in buffer) != device)
                    {
                        SafeFs.Close(child);
                        continue; // Swapped for something on another file system since it was listed.
                    }

                    Walk(child, device, depth + 1, counters);
                }
            }
            finally
            {
                SafeFs.CloseDir(directory);
            }
        }

        /// <summary>Counts the files of one directory and returns the names of its subdirectories on the same device; null when the walk stopped.</summary>
        private List<byte[]>? ListDirectory(nint directory, ulong device, Counters counters)
        {
            int dirFd = SafeFs.DirFd(directory);
            var subdirectories = new List<byte[]>();
            while (true)
            {
                byte* entry = SafeFs.ReadDir(directory);
                if (entry == null)
                {
                    if (SafeFs.LastReadFailed())
                    {
                        counters.Complete = false;
                    }

                    return subdirectories;
                }

                byte* name = SafeFs.EntryName(entry);
                if (SafeFs.IsDotEntry(name))
                {
                    continue;
                }

                if (++_entries > limits.MaxEntries || ((_entries & 1023) == 0 && cancellationToken.IsCancellationRequested))
                {
                    Stopped = true;
                    counters.Complete = false;
                    return null;
                }

                int errno = SafeFs.StatAt(dirFd, name, out StatxBuffer buffer);
                if (errno != 0)
                {
                    if (errno != SafeFs.Enoent)
                    {
                        counters.Complete = false;
                    }

                    continue;
                }

                if (SafeFs.Format(in buffer) == SafeFs.FormatDirectory)
                {
                    if (EffectiveDevice(in buffer) == device)
                    {
                        int length = 0;
                        while (name[length] != 0)
                        {
                            length++;
                        }

                        subdirectories.Add(new ReadOnlySpan<byte>(name, length + 1).ToArray());
                    }

                    continue; // Another file system: not part of the folder.
                }

                if (buffer.LinkCount > 1 && !hardLinks.Add((EffectiveDevice(in buffer), buffer.Inode)))
                {
                    continue; // Another name of a file already counted.
                }

                counters.SizeOnDisk += checked((long)buffer.Blocks * 512);
                counters.LogicalSize += (long)buffer.Size;
                counters.Files++;
            }
        }
    }

    /// <summary>Measures the folders of <paramref name="request"/> with the production limits.</summary>
    internal static FoldersResponse Run(FoldersRequest request, CancellationToken cancellationToken) =>
        Run(request, MeasureLimits.Production, null, cancellationToken);

    /// <summary>The testable core: smaller <paramref name="limits"/> and a device seam (see <see cref="CleanupPolicy.DeviceOverride"/>). Only tests call this.</summary>
    internal static FoldersResponse Run(
        FoldersRequest request, MeasureLimits limits, Func<ulong, ulong, ulong>? deviceOverride, CancellationToken cancellationToken)
    {
        if (request is null || request.Version != HelperProtocol.Version)
        {
            throw new HelperException(HelperExit.Usage, "unsupported request version");
        }

        if (request.Paths is null)
        {
            throw new HelperException(HelperExit.Usage, "request lists no folders");
        }

        if (request.Paths.Count > HelperProtocol.MaxFolders)
        {
            throw new HelperException(HelperExit.TooLarge, "too many folders");
        }

        var walker = new Walker(limits, deviceOverride, [], cancellationToken);
        var folders = new List<FolderSize>(request.Paths.Count);
        foreach (string path in request.Paths)
        {
            if (walker.Stopped || cancellationToken.IsCancellationRequested)
            {
                break; // Folders not reached are left out: absent means not measured.
            }

            if (path is null || !IsAcceptable(path))
            {
                continue;
            }

            int fd = SafeFs.OpenDirectoryNoFollow(path, out _);
            if (fd < 0)
            {
                continue;
            }

            if (SafeFs.StatFd(fd, out StatxBuffer buffer) != 0)
            {
                SafeFs.Close(fd);
                continue;
            }

            var counters = new Counters();
            walker.Walk(fd, walker.EffectiveDevice(in buffer), 0, counters);
            folders.Add(new FolderSize(path, counters.SizeOnDisk, counters.LogicalSize, counters.Files, counters.Complete));
        }

        return new FoldersResponse(HelperProtocol.Version, folders);
    }

    /// <summary>An absolute, normalized path that is not in a kernel-provided file system.</summary>
    private static bool IsAcceptable(string path)
    {
        if (!PathTrust.IsNormalizedAbsolute(path))
        {
            return false;
        }

        foreach (string excluded in ExcludedRoots)
        {
            if (path == excluded || path.StartsWith(excluded + "/", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
