namespace Pennyworth.Platform.Linux;

/// <summary>
/// What the cleanup trusts about the allowlisted folders. Production is <see cref="Production"/>; the other values exist for
/// tests, which run as an ordinary user in a temporary folder. Nothing reads these from argv or the environment, so a caller of
/// the helper can only ever get <see cref="Production"/>.
/// </summary>
/// <param name="VerifyRoot">Judges a folder path before anything is opened (ancestors and owner).</param>
/// <param name="RootOwner">The user id that must own an allowlisted folder (root, 0).</param>
/// <param name="DeviceOverride">A test seam: maps the real (device, inode) of a directory or file to the device the walk should believe, to simulate a mount point.</param>
/// <param name="IsKept">Names the cleanup never deletes even when listed.</param>
internal sealed record CleanupPolicy(
    Func<string, TrustVerdict> VerifyRoot,
    uint RootOwner,
    Func<ulong, ulong, ulong>? DeviceOverride,
    Func<string, string, bool> IsKept)
{
    /// <summary>Real roots: strictly root-owned chain, root-owned folder, no device simulation, the compiled-in kept names.</summary>
    internal static CleanupPolicy Production { get; } = new(
        static path => PathTrust.CheckRootOwnedDirectory(path, PathTrust.Lstat),
        0,
        null,
        SystemCleanupRules.IsKept);
}

/// <summary>
/// <c>clean-system</c>: deletes the files of a preview, and only those, below the compiled-in folders of one rule. The helper runs
/// as root and the folders can contain things other users created (<c>/var/crash</c>), so the caller's list is never an authority:
/// it can only <em>narrow</em> what the helper's own allowlist covers.
/// <list type="bullet">
/// <item>The rule must be known; every file's root must be exactly one of that rule's folders; its path must be relative and
/// made of plain names. Anything else refuses the whole request before a single file is touched.</item>
/// <item>Each folder must be a real directory owned by root under a chain only root can change (see <see cref="PathTrust"/>); it is
/// then opened from <c>/</c> component by component with <c>O_NOFOLLOW</c> and the opened descriptor is checked again.</item>
/// <item>Each file is found by opening every directory below the folder from the folder's descriptor, never leaving the folder's
/// device, and deleted with <c>unlinkat</c> only when it is still a regular file (or a link, removed as a link) with the modification
/// time the preview saw. Anything else is left alone and counted as skipped.</item>
/// <item>Directories the run emptied are then removed, deepest first, with <c>rmdir</c> semantics: a directory that still has
/// something in it, one that was empty before, and the folder itself stay.</item>
/// </list>
/// </summary>
internal static class SystemCleanup
{
    /// <summary>Longest relative path accepted, in characters (PATH_MAX); it also bounds the descriptors one file lookup holds.</summary>
    private const int MaxPathLength = 4096;

    private sealed record Item(string Root, string[] Segments, byte[][] Names, CleanFile File);

    /// <summary>The cleanup of <paramref name="ruleId"/>'s own folders with the production policy; <see cref="HelperException"/> when the request is refused.</summary>
    internal static CleanResponse Run(string ruleId, CleanRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? roots = SystemCleanupRules.FoldersOf(ruleId);
        if (roots is null)
        {
            throw new HelperException(HelperExit.Refused, "unknown cleanup rule");
        }

        return Run(roots, request, CleanupPolicy.Production, cancellationToken);
    }

    /// <summary>The testable core: <paramref name="roots"/> stand in for the compiled-in folders and <paramref name="policy"/> for the production checks. Only tests call this.</summary>
    internal static CleanResponse Run(IReadOnlyList<string> roots, CleanRequest request, CleanupPolicy policy, CancellationToken cancellationToken)
    {
        if (request is null || request.Version != HelperProtocol.Version)
        {
            throw new HelperException(HelperExit.Usage, "unsupported request version");
        }

        if (request.Files is null)
        {
            throw new HelperException(HelperExit.Usage, "request lists no files");
        }

        if (request.Files.Count > HelperProtocol.MaxCleanFiles)
        {
            throw new HelperException(HelperExit.TooLarge, "too many files");
        }

        long skipped = 0;
        var byRoot = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        foreach (CleanFile file in request.Files)
        {
            Item item = Validate(file, roots);
            if (policy.IsKept(item.Root, file.Path))
            {
                skipped++;
                continue;
            }

            if (!byRoot.TryGetValue(item.Root, out List<Item>? items))
            {
                byRoot[item.Root] = items = [];
            }

            items.Add(item);
        }

        // Verify every folder that is to be touched before deleting anything, so a refused folder refuses the whole run.
        var present = new List<string>();
        foreach (string root in roots)
        {
            if (!byRoot.TryGetValue(root, out List<Item>? items))
            {
                continue;
            }

            switch (policy.VerifyRoot(root))
            {
                case TrustVerdict.Trusted:
                    present.Add(root);
                    break;
                case TrustVerdict.Missing:
                    skipped += items.Count;
                    break;
                default:
                    throw new HelperException(HelperExit.Refused, $"{root} is not a root-owned folder in a protected path");
            }
        }

        long freed = 0;
        long deleted = 0;
        foreach (string root in present)
        {
            List<Item> items = byRoot[root];
            int fd = OpenRoot(root, policy, out ulong rootDevice, out bool vanished);
            if (vanished)
            {
                skipped += items.Count;
                continue;
            }

            try
            {
                (long rootFreed, long rootDeleted, long rootSkipped) = CleanRoot(fd, rootDevice, items, policy, cancellationToken);
                freed += rootFreed;
                deleted += rootDeleted;
                skipped += rootSkipped;
            }
            finally
            {
                SafeFs.Close(fd);
            }
        }

        return new CleanResponse(HelperProtocol.Version, freed, deleted, skipped);
    }

    /// <summary>Checks one preview entry against the allowlist and the path rules; any violation is a refusal of the request.</summary>
    private static Item Validate(CleanFile file, IReadOnlyList<string> roots)
    {
        if (file is null || file.Root is null || file.Path is null)
        {
            throw new HelperException(HelperExit.Usage, "malformed file entry");
        }

        bool known = false;
        foreach (string root in roots)
        {
            known |= string.Equals(root, file.Root, StringComparison.Ordinal);
        }

        if (!known)
        {
            throw new HelperException(HelperExit.Refused, "file outside the folders of this rule");
        }

        if (file.Size < 0)
        {
            throw new HelperException(HelperExit.Refused, "negative file size");
        }

        string path = file.Path;
        if (path.Length == 0 || path.Length > MaxPathLength || path.Contains('\0', StringComparison.Ordinal))
        {
            throw new HelperException(HelperExit.Refused, "invalid file path");
        }

        string[] segments = path.Split('/');
        byte[][] names = new byte[segments.Length][];
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || segments[i] is "." or "..")
            {
                throw new HelperException(HelperExit.Refused, "invalid file path");
            }

            names[i] = SafeFs.Z(segments[i]);
        }

        return new Item(file.Root, segments, names, file);
    }

    /// <summary>Opens <paramref name="root"/> without following any link and confirms that what was opened is a directory of the required owner.</summary>
    private static int OpenRoot(string root, CleanupPolicy policy, out ulong device, out bool vanished)
    {
        device = 0;
        vanished = false;
        int fd = SafeFs.OpenDirectoryNoFollow(root, out int errno);
        if (fd < 0)
        {
            if (errno is SafeFs.Enoent)
            {
                vanished = true;
                return -1;
            }

            throw new HelperException(HelperExit.Refused, $"cannot open {root} safely");
        }

        if (SafeFs.StatFd(fd, out StatxBuffer buffer) != 0
            || SafeFs.Format(in buffer) != SafeFs.FormatDirectory
            || buffer.Uid != policy.RootOwner)
        {
            SafeFs.Close(fd);
            throw new HelperException(HelperExit.Refused, $"{root} is not a directory owned by root");
        }

        device = EffectiveDevice(policy, in buffer);
        return fd;
    }

    private static ulong EffectiveDevice(CleanupPolicy policy, in StatxBuffer buffer)
    {
        ulong device = SafeFs.Device(in buffer);
        return policy.DeviceOverride?.Invoke(device, buffer.Inode) ?? device;
    }

    private static (long Freed, long Deleted, long Skipped) CleanRoot(
        int rootFd, ulong rootDevice, List<Item> items, CleanupPolicy policy, CancellationToken cancellationToken)
    {
        long freed = 0;
        long deleted = 0;
        long skipped = 0;
        var touchedDirectories = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < items.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                skipped += items.Count - i;
                break;
            }

            Item item = items[i];
            if (DeleteOne(rootFd, rootDevice, item, policy))
            {
                freed += item.File.Size;
                deleted++;
                RememberDirectories(touchedDirectories, item.Segments);
            }
            else
            {
                skipped++;
            }
        }

        RemoveEmptied(rootFd, rootDevice, touchedDirectories, policy);
        return (freed, deleted, skipped);
    }

    /// <summary>Remembers the directory of a deleted file and every directory above it (below the folder): each held something until now.</summary>
    private static void RememberDirectories(HashSet<string> directories, string[] segments)
    {
        for (int count = segments.Length - 1; count >= 1; count--)
        {
            if (!directories.Add(string.Join('/', segments, 0, count)))
            {
                return; // Its ancestors were remembered with it.
            }
        }
    }

    /// <summary>Deletes the file only when it is still what the preview listed. Never throws for a file that cannot be deleted.</summary>
    private static unsafe bool DeleteOne(int rootFd, ulong rootDevice, Item item, CleanupPolicy policy)
    {
        Span<int> opened = stackalloc int[item.Names.Length];
        int openedCount = 0;
        try
        {
            int parent = OpenDirectories(rootFd, rootDevice, item.Names, item.Names.Length - 1, policy, opened, ref openedCount);
            if (parent < 0)
            {
                return false;
            }

            fixed (byte* leaf = item.Names[^1])
            {
                if (SafeFs.StatAt(parent, leaf, out StatxBuffer buffer) != 0)
                {
                    return false;
                }

                ushort format = SafeFs.Format(in buffer);
                if (format is not (SafeFs.FormatRegular or SafeFs.FormatSymlink)
                    || EffectiveDevice(policy, in buffer) != rootDevice
                    || SafeFs.ModifiedTicks(in buffer) != item.File.ModifiedTicks)
                {
                    return false;
                }

                return SafeFs.UnlinkAt(parent, leaf, directory: false) == 0;
            }
        }
        finally
        {
            for (int i = 0; i < openedCount; i++)
            {
                SafeFs.Close(opened[i]);
            }
        }
    }

    /// <summary>
    /// Opens the first <paramref name="count"/> names below <paramref name="rootFd"/>, each relative to the previous descriptor with
    /// <c>O_NOFOLLOW</c> and each required to be on the folder's device. Returns the last descriptor, or -1; the descriptors opened are
    /// recorded in <paramref name="opened"/> for the caller to close.
    /// </summary>
    private static unsafe int OpenDirectories(
        int rootFd, ulong rootDevice, byte[][] names, int count, CleanupPolicy policy, Span<int> opened, ref int openedCount)
    {
        int current = rootFd;
        for (int i = 0; i < count; i++)
        {
            int next;
            fixed (byte* name = names[i])
            {
                next = SafeFs.OpenDirectoryAt(current, name, out _);
            }

            if (next < 0)
            {
                return -1;
            }

            opened[openedCount++] = next;
            if (SafeFs.StatFd(next, out StatxBuffer buffer) != 0
                || SafeFs.Format(in buffer) != SafeFs.FormatDirectory
                || EffectiveDevice(policy, in buffer) != rootDevice)
            {
                return -1; // A mount point (or something swapped in): never entered.
            }

            current = next;
        }

        return current;
    }

    /// <summary>Removes the remembered directories deepest first; <c>rmdir</c> refuses one that still has content, which is the intent.</summary>
    private static void RemoveEmptied(int rootFd, ulong rootDevice, HashSet<string> directories, CleanupPolicy policy)
    {
        foreach (string directory in directories.OrderByDescending(static d => d.Count(static c => c == '/')).ThenBy(static d => d, StringComparer.Ordinal))
        {
            RemoveDirectory(rootFd, rootDevice, directory.Split('/'), policy);
        }
    }

    private static unsafe void RemoveDirectory(int rootFd, ulong rootDevice, string[] segments, CleanupPolicy policy)
    {
        byte[][] names = new byte[segments.Length][];
        for (int i = 0; i < segments.Length; i++)
        {
            names[i] = SafeFs.Z(segments[i]);
        }

        Span<int> opened = stackalloc int[names.Length];
        int openedCount = 0;
        try
        {
            int parent = OpenDirectories(rootFd, rootDevice, names, names.Length - 1, policy, opened, ref openedCount);
            if (parent >= 0)
            {
                fixed (byte* leaf = names[^1])
                {
                    _ = SafeFs.UnlinkAt(parent, leaf, directory: true);
                }
            }
        }
        finally
        {
            for (int i = 0; i < openedCount; i++)
            {
                SafeFs.Close(opened[i]);
            }
        }
    }
}
