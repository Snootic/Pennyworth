using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// <c>read-firmware-tables</c>: hands the raw SMBIOS table, which only root can read, to the app, which parses it. The helper
/// parses nothing so that all code that interprets the table's format stays in the unprivileged process.
/// </summary>
internal static unsafe partial class FirmwareTables
{
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1;
    private const ushort FormatMask = 0xF000;
    private const ushort FormatRegular = 0x8000;
    private const int Enoent = 2;
    private const int Eacces = 13;
    private const int Enotdir = 20;
    private const int Eperm = 1;

    internal static FirmwareTablesResponse Read(CancellationToken cancellationToken) =>
        Read(HelperProtocol.FirmwareTablePath, cancellationToken);

    /// <summary>
    /// Reads <paramref name="path"/> (always <see cref="HelperProtocol.FirmwareTablePath"/> in production; a parameter only so a
    /// test can point it at a fixture). A symbolic link, a folder, a pipe or anything else that is not a regular file is refused
    /// before it is opened (a pipe would block the open). sysfs reports its binary attributes as regular files of size 0 or 4096,
    /// so the size is never trusted: the file is read to its end under the size cap.
    /// </summary>
    /// <exception cref="HelperException"><see cref="HelperExit.Missing"/> when there is no table, <see cref="HelperExit.Refused"/> when it is not a plain file or not readable, <see cref="HelperExit.TooLarge"/> above <see cref="HelperProtocol.MaxFirmwareTableBytes"/>.</exception>
    internal static FirmwareTablesResponse Read(string path, CancellationToken cancellationToken)
    {
        if (path.Contains('\0'))
        {
            throw new HelperException(HelperExit.Failed, "the firmware tables could not be examined");
        }

        (int errno, ushort mode) = StatPath(path);
        if (errno is Enoent or Enotdir)
        {
            throw new HelperException(HelperExit.Missing, "the firmware tables are not available");
        }

        if (errno is Eacces or Eperm)
        {
            throw new HelperException(HelperExit.Refused, "the firmware tables cannot be read");
        }

        if (errno != 0)
        {
            throw new HelperException(HelperExit.Failed, "the firmware tables could not be examined");
        }

        if ((mode & FormatMask) != FormatRegular)
        {
            throw new HelperException(HelperExit.Refused, "the firmware tables are not a regular file");
        }

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new HelperException(HelperExit.Missing, "the firmware tables are not available");
        }
        catch (UnauthorizedAccessException)
        {
            throw new HelperException(HelperExit.Refused, "the firmware tables cannot be read");
        }

        using (handle)
        {
            // The check above and the open are two steps; this one is on the opened file itself, so a swap in between is caught.
            if (!IsRegularFile(handle))
            {
                throw new HelperException(HelperExit.Refused, "the firmware tables are not a regular file");
            }

            byte[] table = ReadToEnd(handle, cancellationToken);
            if (table.Length == 0)
            {
                throw new HelperException(HelperExit.Missing, "the firmware tables are empty");
            }

            return new FirmwareTablesResponse(HelperProtocol.Version, Convert.ToBase64String(table));
        }
    }

    private static byte[] ReadToEnd(SafeFileHandle handle, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        using var collected = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                return collected.ToArray();
            }

            if (collected.Length + read > HelperProtocol.MaxFirmwareTableBytes)
            {
                throw new HelperException(HelperExit.TooLarge, "the firmware tables are larger than the allowed size");
            }

            collected.Write(chunk, 0, read);
        }
    }

    /// <summary><c>statx</c> of the path itself, never of what a final link points to: the errno (0 on success) and the file mode.</summary>
    private static (int Errno, ushort Mode) StatPath(string path)
    {
        byte[] name = new byte[Encoding.UTF8.GetByteCount(path) + 1];
        Encoding.UTF8.GetBytes(path, name);
        StatxBuffer buffer;
        fixed (byte* pathBytes = name)
        {
            if (StatxCall(Statx.AtFdCwd, pathBytes, AtSymlinkNoFollow, StatxType, &buffer) != 0)
            {
                return (Marshal.GetLastPInvokeError(), 0);
            }
        }

        return (0, buffer.Mode);
    }

    /// <summary>Whether the file behind <paramref name="handle"/> is a regular file (<c>statx</c> with <c>AT_EMPTY_PATH</c>); false when that cannot be told.</summary>
    private static bool IsRegularFile(SafeFileHandle handle)
    {
        StatxBuffer buffer;
        byte empty = 0;
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (StatxCall((int)handle.DangerousGetHandle(), &empty, AtEmptyPath, StatxType, &buffer) != 0)
            {
                return false;
            }
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }

        return (buffer.Mode & FormatMask) == FormatRegular;
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static partial int StatxCall(int dirFd, byte* path, int flags, uint mask, StatxBuffer* buffer);
}
