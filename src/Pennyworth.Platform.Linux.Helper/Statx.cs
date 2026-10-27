using System.Runtime.InteropServices;

namespace Pennyworth.Platform.Linux;

/// <summary>What a directory entry is, as far as a size scan cares. Sockets, FIFOs and device nodes are <see cref="Other"/>.</summary>
internal enum FileKind
{
    Other,
    Directory,
    Symlink,
}

internal enum StatResult
{
    Ok,
    NotFound,
    Denied,

    /// <summary>The kernel or libc has no <c>statx</c>; <see cref="Statx.FallbackStat"/> is the only source.</summary>
    Unavailable,
    Failed,
}

/// <param name="AllocatedBytes"><c>st_blocks * 512</c>: what the file occupies on disk (0 for a sparse hole).</param>
/// <param name="LogicalBytes"><c>st_size</c>.</param>
/// <param name="HasIdentity">False when only the managed fallback was available: no device or inode.</param>
internal readonly record struct FileStat(
    FileKind Kind,
    long AllocatedBytes,
    long LogicalBytes,
    bool HasIdentity,
    ulong Device,
    ulong Inode,
    uint LinkCount);

/// <summary>One <c>statx_timestamp</c>: seconds, nanoseconds, padding.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct StatxTimestamp
{
    [FieldOffset(0)] public long Seconds;
    [FieldOffset(8)] public uint Nanoseconds;
}

/// <summary>
/// <c>struct statx</c> from <c>linux/stat.h</c>. The layout is fixed ABI (256 bytes, same on x86-64
/// and arm64), so the offsets are spelled out and checked by a test rather than trusted.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct StatxBuffer
{
    [FieldOffset(0)] public uint Mask;
    [FieldOffset(4)] public uint BlockSize;
    [FieldOffset(8)] public ulong Attributes;
    [FieldOffset(16)] public uint LinkCount;
    [FieldOffset(20)] public uint Uid;
    [FieldOffset(24)] public uint Gid;
    [FieldOffset(28)] public ushort Mode;
    [FieldOffset(32)] public ulong Inode;
    [FieldOffset(40)] public ulong Size;
    [FieldOffset(48)] public ulong Blocks;
    [FieldOffset(56)] public ulong AttributesMask;
    [FieldOffset(64)] public StatxTimestamp AccessTime;
    [FieldOffset(80)] public StatxTimestamp BirthTime;
    [FieldOffset(96)] public StatxTimestamp ChangeTime;
    [FieldOffset(112)] public StatxTimestamp ModifyTime;
    [FieldOffset(128)] public uint RdevMajor;
    [FieldOffset(132)] public uint RdevMinor;
    [FieldOffset(136)] public uint DeviceMajor;
    [FieldOffset(140)] public uint DeviceMinor;
}

/// <summary>
/// <c>statx(2)</c> through libc, which reports allocated blocks, logical size, device and inode in
/// one syscall without following links.
/// </summary>
internal static unsafe partial class Statx
{
    internal const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;

    /// <summary>Do not force a network file system to refresh attributes: a size scan wants the cached values.</summary>
    private const int AtStatxDontSync = 0x4000;

    private const uint StatxType = 0x1;
    private const uint StatxMode = 0x2;
    private const uint StatxNlink = 0x4;
    private const uint StatxIno = 0x100;
    private const uint StatxSize = 0x200;
    private const uint StatxBlocks = 0x400;
    private const uint RequestedMask = StatxType | StatxMode | StatxNlink | StatxIno | StatxSize | StatxBlocks;

    private const int Enoent = 2;
    private const int Eacces = 13;
    private const int Enotdir = 20;
    private const int Enosys = 38;
    private const int Eperm = 1;

    private const ushort FormatMask = 0xF000;
    private const ushort FormatDirectory = 0x4000;
    private const ushort FormatSymlink = 0xA000;

    private static volatile bool s_unavailable;

    /// <summary>True once a call found no <c>statx</c>; from then on callers use <see cref="FallbackStat"/>.</summary>
    internal static bool IsUnavailable => s_unavailable;

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static partial int StatxCall(int dirFd, byte* path, int flags, uint mask, StatxBuffer* buffer);

    /// <summary>
    /// Stats the NUL-terminated <paramref name="name"/> relative to <paramref name="dirFd"/>
    /// (<c>AT_FDCWD</c> for a path). A relative lookup walks one component instead of the whole path
    /// and so has no length limit of its own. Links are not followed unless <paramref name="followLinks"/>.
    /// </summary>
    internal static StatResult Stat(int dirFd, byte* name, bool followLinks, out FileStat stat)
    {
        stat = default;
        if (s_unavailable)
        {
            return StatResult.Unavailable;
        }

        StatxBuffer buffer;
        int flags = AtStatxDontSync | (followLinks ? 0 : AtSymlinkNoFollow);
        int result;
        int errno;
        try
        {
            result = StatxCall(dirFd, name, flags, RequestedMask, &buffer);
            errno = Marshal.GetLastPInvokeError();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            s_unavailable = true;
            return StatResult.Unavailable;
        }

        if (result != 0)
        {
            switch (errno)
            {
                case Enosys:
                    s_unavailable = true;
                    return StatResult.Unavailable;
                case Enoent:
                case Enotdir:
                    return StatResult.NotFound;
                case Eacces:
                case Eperm:
                    return StatResult.Denied;
                default:
                    return StatResult.Failed;
            }
        }

        stat = FromBuffer(in buffer);
        return StatResult.Ok;
    }

    /// <summary>Stats a path string (UTF-8 encoded), following a final link when asked.</summary>
    internal static StatResult StatPath(string path, bool followLinks, out FileStat stat)
    {
        byte[] bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(path) + 1];
        System.Text.Encoding.UTF8.GetBytes(path, bytes);
        fixed (byte* name = bytes)
        {
            return Stat(AtFdCwd, name, followLinks, out stat);
        }
    }

    internal static FileStat FromBuffer(in StatxBuffer buffer)
    {
        FileKind kind = (buffer.Mode & FormatMask) switch
        {
            FormatDirectory => FileKind.Directory,
            FormatSymlink => FileKind.Symlink,
            _ => FileKind.Other,
        };

        return new FileStat(
            kind,
            checked((long)buffer.Blocks * 512),
            (long)buffer.Size,
            HasIdentity: true,
            ((ulong)buffer.DeviceMajor << 32) | buffer.DeviceMinor,
            buffer.Inode,
            buffer.LinkCount);
    }

    /// <summary>
    /// The managed answer for a kernel without <c>statx</c>: logical size stands in for the allocated
    /// size and there is no device or inode, so hard links and mount points cannot be recognised.
    /// Returns null when the path is gone or cannot be read.
    /// </summary>
    internal static FileStat? FallbackStat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            FileAttributes attributes = info.Attributes;
            if ((int)attributes == -1)
            {
                return null;
            }

            if (info.LinkTarget is not null)
            {
                return new FileStat(FileKind.Symlink, 0, 0, false, 0, 0, 1);
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                return new FileStat(FileKind.Directory, 0, 0, false, 0, 0, 1);
            }

            long length = info.Length;
            return new FileStat(FileKind.Other, length, length, false, 0, 0, 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
