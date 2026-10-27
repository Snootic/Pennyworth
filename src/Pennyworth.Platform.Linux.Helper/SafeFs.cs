using System.Runtime.InteropServices;
using System.Text;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The few libc calls the root helper needs to walk and delete without ever following a symbolic link: everything is
/// relative to an open directory descriptor, names are raw bytes (a file name is not necessarily UTF-8) and every
/// open uses <c>O_NOFOLLOW</c>. They are <c>openat</c>, <c>statx</c>, <c>unlinkat</c> and <c>fdopendir</c>/<c>readdir</c>.
/// <para>
/// A path string is never handed to the kernel in one piece for anything that deletes or measures: a path is opened
/// component by component, so a link swapped in while the helper runs cannot redirect it, and the descriptor is the
/// one object the following calls refer to. Every method reports failure as an <c>errno</c> (0 is success).
/// </para>
/// </summary>
internal static unsafe partial class SafeFs
{
    internal const int AtFdCwd = Statx.AtFdCwd;
    internal const int AtSymlinkNoFollow = 0x100;
    internal const int AtRemoveDir = 0x200;
    internal const int AtEmptyPath = 0x1000;

    internal const int Enoent = 2;
    internal const int Enotdir = 20;
    internal const int Eloop = 40;

    private const uint StatxBasicStats = 0x7ff;

    internal const ushort FormatMask = 0xF000;
    internal const ushort FormatRegular = 0x8000;
    internal const ushort FormatDirectory = 0x4000;
    internal const ushort FormatSymlink = 0xA000;

    // struct dirent64: d_ino u64, d_off i64, d_reclen u16, d_type u8, d_name[]. Same on glibc and musl, x64 and arm64.
    private const int DirentNameOffset = 19;

    private const int OpenReadOnly = 0;
    private const int OpenCloseOnExec = 0x80000;

    // The two flags below differ between architecture families (x86 has its own numbers, arm64 and the others
    // use the generic ones), so they are chosen once from the process architecture.
    private static readonly int OpenDirectory = IsX86Family ? 0x10000 : 0x4000;
    private static readonly int OpenNoFollow = IsX86Family ? 0x20000 : 0x8000;

    private static bool IsX86Family => RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static partial int OpenAtCall(int dirFd, byte* path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static partial int StatxCall(int dirFd, byte* path, int flags, uint mask, StatxBuffer* buffer);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static partial int UnlinkAtCall(int dirFd, byte* path, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int CloseCall(int fd);

    [LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static partial nint FdOpenDirCall(int fd);

    [LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static partial byte* ReadDirCall(nint directory);

    [LibraryImport("libc", EntryPoint = "closedir")]
    private static partial int CloseDirCall(nint directory);

    [LibraryImport("libc", EntryPoint = "dirfd")]
    private static partial int DirFdCall(nint directory);

    /// <summary><paramref name="text"/> as NUL-terminated UTF-8, the form every method here takes.</summary>
    internal static byte[] Z(string text)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    /// <summary>Opens the directory <paramref name="name"/> below <paramref name="dirFd"/>: never through a link, and only if it is a directory. Returns the descriptor, or -1 with <paramref name="errno"/>.</summary>
    internal static int OpenDirectoryAt(int dirFd, byte* name, out int errno)
    {
        int fd = OpenAtCall(dirFd, name, OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec, 0);
        errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
        return fd;
    }

    /// <summary>
    /// Opens the absolute, normalized <paramref name="absolutePath"/> one component at a time, each with <c>O_NOFOLLOW</c>:
    /// a link anywhere in the path (not only at the end) makes the open fail with <c>ELOOP</c> instead of being followed.
    /// </summary>
    internal static int OpenDirectoryNoFollow(string absolutePath, out int errno)
    {
        byte[] slash = "/\0"u8.ToArray();
        int current;
        fixed (byte* name = slash)
        {
            current = OpenDirectoryAt(AtFdCwd, name, out errno);
        }

        if (current < 0)
        {
            return -1;
        }

        foreach (string segment in absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Z(segment);
            int next;
            fixed (byte* name = bytes)
            {
                next = OpenDirectoryAt(current, name, out errno);
            }

            Close(current);
            if (next < 0)
            {
                return -1;
            }

            current = next;
        }

        errno = 0;
        return current;
    }

    /// <summary><c>statx</c> of <paramref name="name"/> below <paramref name="dirFd"/>, never following a final link; 0 or an errno.</summary>
    internal static int StatAt(int dirFd, byte* name, out StatxBuffer buffer)
    {
        StatxBuffer result;
        int status = StatxCall(dirFd, name, AtSymlinkNoFollow, StatxBasicStats, &result);
        int errno = status == 0 ? 0 : Marshal.GetLastPInvokeError();
        buffer = status == 0 ? result : default;
        return errno;
    }

    /// <summary><c>statx</c> of the object an open descriptor refers to: what an opened directory really is, not what a path said.</summary>
    internal static int StatFd(int fd, out StatxBuffer buffer)
    {
        byte empty = 0;
        StatxBuffer result;
        int status = StatxCall(fd, &empty, AtEmptyPath, StatxBasicStats, &result);
        int errno = status == 0 ? 0 : Marshal.GetLastPInvokeError();
        buffer = status == 0 ? result : default;
        return errno;
    }

    /// <summary><c>statx</c> of an absolute path without following a final link; used for the ownership checks of <see cref="PathTrust"/>.</summary>
    internal static int StatPathNoFollow(string absolutePath, out StatxBuffer buffer)
    {
        byte[] bytes = Z(absolutePath);
        fixed (byte* name = bytes)
        {
            return StatAt(AtFdCwd, name, out buffer);
        }
    }

    /// <summary>Removes the entry <paramref name="name"/> below <paramref name="dirFd"/> (a directory when <paramref name="directory"/>); a link is removed itself, never followed. 0 or an errno.</summary>
    internal static int UnlinkAt(int dirFd, byte* name, bool directory)
    {
        int status = UnlinkAtCall(dirFd, name, directory ? AtRemoveDir : 0);
        return status == 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    internal static void Close(int fd) => _ = CloseCall(fd);

    /// <summary>Starts listing the directory <paramref name="fd"/> refers to. On success the stream owns the descriptor (<see cref="CloseDir"/> closes it); on failure the caller still owns it.</summary>
    internal static nint OpenDir(int fd) => FdOpenDirCall(fd);

    /// <summary>The next entry of the listing: a pointer to its <c>dirent</c>, or null at the end (or on an error: see <see cref="LastReadFailed"/>).</summary>
    internal static byte* ReadDir(nint directory) => ReadDirCall(directory);

    /// <summary>True when the <c>readdir</c> that just returned null failed instead of reaching the end.</summary>
    internal static bool LastReadFailed() => Marshal.GetLastPInvokeError() != 0;

    internal static void CloseDir(nint directory) => _ = CloseDirCall(directory);

    internal static int DirFd(nint directory) => DirFdCall(directory);

    /// <summary>The NUL-terminated raw name of a <c>dirent</c> returned by <see cref="ReadDir"/>.</summary>
    internal static byte* EntryName(byte* entry) => entry + DirentNameOffset;

    internal static bool IsDotEntry(byte* name) =>
        name[0] == (byte)'.' && (name[1] == 0 || (name[1] == (byte)'.' && name[2] == 0));

    internal static ushort Format(in StatxBuffer buffer) => (ushort)(buffer.Mode & FormatMask);

    /// <summary>The device number the way <see cref="Statx.FromBuffer"/> composes it.</summary>
    internal static ulong Device(in StatxBuffer buffer) => ((ulong)buffer.DeviceMajor << 32) | buffer.DeviceMinor;

    /// <summary>
    /// The last-write time as .NET reports it for the same file (<c>File.GetLastWriteTimeUtc().Ticks</c>): 100 ns steps since 0001-01-01
    /// (the preview compares this, so the two must agree to the tick).
    /// </summary>
    internal static long ModifiedTicks(in StatxBuffer buffer) =>
        621_355_968_000_000_000L + (buffer.ModifyTime.Seconds * 10_000_000L) + (buffer.ModifyTime.Nanoseconds / 100);
}
