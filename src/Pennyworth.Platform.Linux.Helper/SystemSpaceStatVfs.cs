using System.Runtime.InteropServices;

namespace Pennyworth.Platform.Linux;

/// <summary>The four <c>statvfs</c> counters system-space arithmetic needs, in filesystem blocks.</summary>
/// <param name="BlockSize"><c>f_bsize</c>: the preferred I/O size, the fallback unit.</param>
/// <param name="FragmentSize"><c>f_frsize</c>: the unit the block counts below are in.</param>
/// <param name="Blocks"><c>f_blocks</c>: size of the filesystem as the kernel reports it.</param>
/// <param name="FreeBlocks"><c>f_bfree</c>: free blocks, including those reserved for root.</param>
/// <param name="AvailableBlocks"><c>f_bavail</c>: free blocks an unprivileged user can use.</param>
internal readonly record struct VfsCounters(ulong BlockSize, ulong FragmentSize, ulong Blocks, ulong FreeBlocks, ulong AvailableBlocks);

internal static unsafe partial class SystemSpaceStatVfs
{
    /// <summary>Null when the path does not exist, is not readable or <c>statvfs</c> fails for any other reason.</summary>
    internal static VfsCounters? Read(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0'))
        {
            return null;
        }

        Native native;
        if (StatVfs(path, &native) != 0)
        {
            return null;
        }

        return new VfsCounters(native.BlockSize, native.FragmentSize, native.Blocks, native.FreeBlocks, native.AvailableBlocks);
    }

    /// <summary>
    /// <c>struct statvfs</c> of glibc and musl on the 64-bit Linux targets (<c>unsigned long</c> is eight
    /// bytes there): eleven counters followed by six reserved ints, 112 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Native
    {
        public ulong BlockSize;
        public ulong FragmentSize;
        public ulong Blocks;
        public ulong FreeBlocks;
        public ulong AvailableBlocks;
        public ulong Files;
        public ulong FreeFiles;
        public ulong AvailableFiles;
        public ulong FilesystemId;
        public ulong Flags;
        public ulong NameMax;
        public fixed int Spare[6];
    }

    [LibraryImport("libc", EntryPoint = "statvfs", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatVfs(string path, Native* buffer);
}
