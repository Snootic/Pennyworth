using System.Buffers;
using System.Globalization;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;

namespace Pennyworth.Platform.Linux;

/// <summary>Whole-machine memory counters of <c>/proc/meminfo</c>, in bytes. Absent counters are 0.</summary>
/// <param name="Available">The kernel's own estimate of what can be allocated without swapping; derived on kernels that predate it.</param>
/// <param name="Zswap">RAM the compressed swap cache occupies, not the size of what it holds.</param>
internal readonly record struct MemInfo(long Total, long Available, long Free, long Buffers, long Cached, long Reclaimable, long Zswap);

/// <summary>Cumulative CPU time of <c>/proc/stat</c> in jiffies, laid out the way <c>CpuTracker.UpdateSystem</c> expects.</summary>
/// <param name="Idle">Idle plus I/O wait: a core waiting on a disk is not busy.</param>
/// <param name="Kernel">System, interrupt, soft-interrupt and stolen time, <b>including</b> <paramref name="Idle"/>.</param>
/// <param name="User">User plus nice time. Guest time is already part of it.</param>
/// <param name="Cores">Number of <c>cpuN</c> lines, 0 when the file lists none.</param>
internal readonly record struct CpuTimes(long Idle, long Kernel, long User, int Cores);

/// <summary>The fields of <c>/proc/&lt;pid&gt;/stat</c> the monitor uses.</summary>
/// <param name="Comm">Executable name, at most 15 bytes. Points into the parsed text.</param>
/// <param name="Flags">Kernel task flags; <see cref="ProcFileSystem.KernelThreadFlag"/> marks kernel threads.</param>
/// <param name="UserTicks">Time in user mode, in <see cref="ProcFileSystem.TicksPerSecond"/>.</param>
/// <param name="StartTime">Ticks after boot when the process started; with the pid it identifies the process.</param>
internal readonly ref struct ProcessStat(
    ReadOnlySpan<char> comm, int parentPid, long flags, long userTicks, long systemTicks, long startTime)
{
    public ReadOnlySpan<char> Comm { get; } = comm;

    public int ParentPid { get; } = parentPid;

    public long Flags { get; } = flags;

    public long UserTicks { get; } = userTicks;

    public long SystemTicks { get; } = systemTicks;

    public long StartTime { get; } = startTime;
}

/// <summary>
/// Reads the kernel's process and memory files, and parses them. Parsers are static and work on
/// text, so they are tested against captured files; the reader keeps two buffers for the lifetime
/// of the instance, because a sample reads a few thousand small files per second.
/// The reader is not thread-safe, and the text it returns is overwritten by the next read.
/// </summary>
internal sealed partial class ProcFileSystem : IDisposable
{
    /// <summary>
    /// <c>USER_HZ</c>: the unit of every CPU time in <c>/proc</c>. It is a fixed part of the kernel ABI
    /// (100 on every architecture .NET runs on), not the kernel's internal tick rate.
    /// </summary>
    public const int TicksPerSecond = 100;

    /// <summary><c>PF_KTHREAD</c>.</summary>
    public const long KernelThreadFlag = 0x00200000;

    // Big enough for meminfo (about 1.5 KB) and for the leading cpu lines of /proc/stat on hundreds
    // of cores. Everything read through here has what it needs at the top, so a longer file is
    // truncated rather than grown.
    private const int BufferSize = 32 * 1024;

    private static readonly SearchValues<char> s_whitespace = SearchValues.Create(" \t\r\n");

    private static readonly byte[] s_dri = "/dev/dri/"u8.ToArray();

    private static readonly EnumerationOptions s_enumeration = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
    };

    private static readonly FileSystemEnumerable<int>.FindPredicate s_isProcess =
        static (ref FileSystemEntry entry) => entry.IsDirectory && IsNumber(entry.FileName);

    private static readonly FileSystemEnumerable<int>.FindPredicate s_isNumber =
        static (ref FileSystemEntry entry) => IsNumber(entry.FileName);

    private static readonly FileSystemEnumerable<int>.FindTransform s_toNumber =
        static (ref FileSystemEntry entry) => int.Parse(entry.FileName, NumberStyles.None, CultureInfo.InvariantCulture);

    private byte[]? _bytes = ArrayPool<byte>.Shared.Rent(BufferSize);
    private char[]? _chars = ArrayPool<char>.Shared.Rent(BufferSize);

    public ProcFileSystem(string root)
    {
        Root = root;
    }

    /// <summary>The mount of the process file system, <c>/proc</c> on a real machine.</summary>
    public string Root { get; }

    /// <summary>
    /// Reads a whole small text file. False when it cannot be opened or read: the process ended,
    /// the file is not readable by this user (<c>smaps_rollup</c> of someone else's process) or the
    /// kernel does not provide it.
    /// </summary>
    public bool TryRead(string path, out ReadOnlySpan<char> text)
    {
        text = default;
        byte[] bytes = _bytes ?? throw new ObjectDisposedException(nameof(ProcFileSystem));
        char[] chars = _chars!;
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int length = 0;
            while (length < bytes.Length)
            {
                int read = RandomAccess.Read(handle, bytes.AsSpan(length), length);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            text = chars.AsSpan(0, Encoding.UTF8.GetChars(bytes.AsSpan(0, length), chars));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Process ids: the numeric folders of the root. Unreadable roots give an empty list.</summary>
    public void ListProcessIds(List<int> into) => ListNumbers(Root, s_isProcess, into);

    /// <summary>File descriptors a process has open. Empty for processes of other users.</summary>
    public void ListOpenFiles(int pid, List<int> into) => ListNumbers(Path.Combine(Root, pid.ToString(CultureInfo.InvariantCulture), "fd"), s_isNumber, into);

    private static void ListNumbers(string directory, FileSystemEnumerable<int>.FindPredicate predicate, List<int> into)
    {
        into.Clear();
        try
        {
            foreach (int number in new FileSystemEnumerable<int>(directory, s_toNumber, s_enumeration) { ShouldIncludePredicate = predicate })
            {
                into.Add(number);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The process ended while it was listed.
        }
    }

    /// <summary>
    /// Where <c>/proc/&lt;pid&gt;/exe</c> points, or null when the link cannot be read (another
    /// user's process: <c>EACCES</c> is the normal answer, not a failure) or the process is gone.
    /// The kernel appends " (deleted)" to a binary replaced on disk while the process runs; that is removed.
    /// </summary>
    public string? ReadExePath(int pid)
    {
        Span<byte> target = stackalloc byte[4096];
        int length = ReadLinkOf(pid, -1, target);
        return length <= 0 ? null : CleanExePath(Encoding.UTF8.GetString(target[..length]));
    }

    /// <summary>True when <paramref name="fd"/> of <paramref name="pid"/> is a DRM device node (<c>/dev/dri/*</c>).</summary>
    public bool IsDrmFile(int pid, int fd)
    {
        Span<byte> target = stackalloc byte[64]; // Only the prefix matters; readlink truncates silently.
        int length = ReadLinkOf(pid, fd, target);
        return length >= s_dri.Length && target[..s_dri.Length].SequenceEqual(s_dri);
    }

    private int ReadLinkOf(int pid, int fd, Span<byte> target)
    {
        Span<byte> path = stackalloc byte[Encoding.UTF8.GetMaxByteCount(Root.Length) + 48];
        bool written = fd < 0
            ? Utf8.TryWrite(path, CultureInfo.InvariantCulture, $"{Root}/{pid}/exe\0", out _)
            : Utf8.TryWrite(path, CultureInfo.InvariantCulture, $"{Root}/{pid}/fd/{fd}\0", out _);
        return written ? (int)ReadLink(path, target, target.Length) : -1;
    }

    [LibraryImport("libc", EntryPoint = "readlink")]
    private static partial nint ReadLink(ReadOnlySpan<byte> path, Span<byte> buffer, nint size);

    public void Dispose()
    {
        if (_bytes is { } bytes)
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(_chars!);
            _bytes = null;
            _chars = null;
        }
    }

    /// <summary>Removes the suffix the kernel adds to the link of a binary that was deleted or replaced.</summary>
    public static string CleanExePath(string path) =>
        path.EndsWith(" (deleted)", StringComparison.Ordinal) ? path[..^" (deleted)".Length] : path;

    private static bool IsNumber(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || name.Length > 10)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    // ---- Parsers -----------------------------------------------------------------------------

    /// <summary>
    /// Parses <c>/proc/meminfo</c>. Sizes are written in kB, meaning KiB. Returns false when the text
    /// has no <c>MemTotal</c>.
    /// </summary>
    public static bool TryParseMemInfo(ReadOnlySpan<char> text, out MemInfo info)
    {
        // Total, Available, Free, Buffers, Cached, Reclaimable, Zswap.
        Span<long> value = stackalloc long[7];
        value[1] = -1;
        foreach (ReadOnlySpan<char> line in text.EnumerateLines())
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            int index = line[..colon] switch
            {
                "MemTotal" => 0,
                "MemAvailable" => 1,
                "MemFree" => 2,
                "Buffers" => 3,
                "Cached" => 4,
                "SReclaimable" => 5,
                "Zswap" => 6,
                _ => -1,
            };
            if (index < 0)
            {
                continue;
            }

            ReadOnlySpan<char> rest = line[(colon + 1)..];
            if (NextNumber(ref rest, out long number))
            {
                ReadOnlySpan<char> unit = NextToken(ref rest);
                value[index] = unit is "kB" ? number * 1024 : number;
            }
        }

        long total = value[0], available = value[1], free = value[2], buffers = value[3], cached = value[4];

        if (total <= 0)
        {
            info = default;
            return false;
        }

        if (available < 0)
        {
            available = Math.Min(free + buffers + cached, total); // Kernels before 3.14.
        }

        info = new MemInfo(total, available, free, buffers, cached, value[5], value[6]);
        return true;
    }

    /// <summary>
    /// Parses the aggregate <c>cpu</c> line of <c>/proc/stat</c> (columns user, nice, system, idle,
    /// iowait, irq, softirq, steal; older kernels have fewer) and counts the <c>cpuN</c> lines after it.
    /// </summary>
    public static bool TryParseCpuStat(ReadOnlySpan<char> text, out CpuTimes times)
    {
        times = default;
        bool found = false;
        long idle = 0, kernel = 0, user = 0;
        int cores = 0;
        Span<long> column = stackalloc long[8];
        foreach (ReadOnlySpan<char> line in text.EnumerateLines())
        {
            if (line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                column.Clear();
                ReadOnlySpan<char> rest = line[4..];
                for (int i = 0; i < column.Length && NextNumber(ref rest, out long value); i++)
                {
                    column[i] = value;
                }

                user = column[0] + column[1];
                idle = column[3] + column[4];
                kernel = column[2] + column[5] + column[6] + column[7] + idle;
                found = true;
            }
            else if (line.StartsWith("cpu", StringComparison.Ordinal) && line.Length > 3 && char.IsAsciiDigit(line[3]))
            {
                cores++;
            }
            else if (found)
            {
                break; // The cpu lines are contiguous, and what follows (intr, ...) can be very long.
            }
        }

        if (found)
        {
            times = new CpuTimes(idle, kernel, user, cores);
        }

        return found;
    }

    /// <summary>
    /// Parses <c>/proc/&lt;pid&gt;/stat</c>. The executable name sits in parentheses and may itself
    /// contain spaces and parentheses (<c>(Web Content)</c>, <c>(tmux: server)</c>, <c>(a) (b)</c>), so
    /// the fields that follow are located from the <b>last</b> closing parenthesis.
    /// </summary>
    public static bool TryParseProcessStat(ReadOnlySpan<char> text, out ProcessStat stat)
    {
        stat = default;
        int open = text.IndexOf('(');
        int close = text.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return false;
        }

        // Fields 3..22: state ppid pgrp session tty tpgid flags minflt cminflt majflt cmajflt
        // utime stime cutime cstime priority nice threads itrealvalue starttime.
        // (Its rss field is a per-CPU approximation, several MiB off; statm has the exact value.)
        const int Count = 20;
        ReadOnlySpan<char> rest = text[(close + 1)..];
        Span<long> field = stackalloc long[Count];
        NextToken(ref rest); // state, a letter
        for (int i = 1; i < Count; i++)
        {
            if (!NextNumber(ref rest, out field[i]))
            {
                return false;
            }
        }

        stat = new ProcessStat(
            text[(open + 1)..close],
            (int)Math.Clamp(field[1], 0, int.MaxValue),
            field[6],
            Math.Max(field[11], 0),
            Math.Max(field[12], 0),
            Math.Max(field[19], 0));
        return true;
    }

    /// <summary>
    /// Memory only this process holds: <c>Private_Clean</c> + <c>Private_Dirty</c> of
    /// <c>smaps_rollup</c>, in bytes. Null when the text has neither.
    /// </summary>
    public static long? ParsePrivateBytes(ReadOnlySpan<char> smapsRollup)
    {
        long? sum = null;
        foreach (ReadOnlySpan<char> line in smapsRollup.EnumerateLines())
        {
            if (line.StartsWith("Private_Clean:", StringComparison.Ordinal) || line.StartsWith("Private_Dirty:", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> rest = line[(line.IndexOf(':') + 1)..];
                if (NextNumber(ref rest, out long kilobytes))
                {
                    sum = (sum ?? 0) + (kilobytes * 1024);
                }
            }
        }

        return sum;
    }

    /// <summary>
    /// Pages resident in RAM, the second column of <c>/proc/&lt;pid&gt;/statm</c>: exactly the
    /// <c>VmRSS</c> of <c>status</c>, but in a file a tenth the size. (The <c>rss</c> in <c>stat</c> is
    /// read from a per-CPU counter that can be a few MiB off.) Null when the text has no such column.
    /// </summary>
    public static long? ParseResidentPages(ReadOnlySpan<char> statm)
    {
        ReadOnlySpan<char> rest = statm;
        return NextNumber(ref rest, out _) && NextNumber(ref rest, out long pages) && pages >= 0 ? pages : null;
    }

    /// <summary>Bytes of RAM a zram device occupies: the third column (<c>mem_used_total</c>) of <c>mm_stat</c>.</summary>
    public static long ParseZramBytes(ReadOnlySpan<char> mmStat)
    {
        ReadOnlySpan<char> rest = mmStat;
        long used = 0;
        for (int column = 0; column < 3; column++)
        {
            if (!NextNumber(ref rest, out used))
            {
                return 0;
            }
        }

        return used;
    }

    /// <summary>One value of a flat key-value file such as <c>memory.stat</c> ("inactive_file 1234"). False when absent.</summary>
    public static bool TryParseKeyed(ReadOnlySpan<char> text, ReadOnlySpan<char> key, out long value)
    {
        foreach (ReadOnlySpan<char> line in text.EnumerateLines())
        {
            if (line.StartsWith(key, StringComparison.Ordinal) && line.Length > key.Length && line[key.Length] == ' ')
            {
                ReadOnlySpan<char> rest = line[key.Length..];
                return NextNumber(ref rest, out value);
            }
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// A cgroup limit file (<c>memory.max</c>, <c>memory.high</c>): a number of bytes, or "max" for
    /// no limit, which is null.
    /// </summary>
    public static long? ParseLimit(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> rest = text;
        return NextNumber(ref rest, out long value) ? value : null;
    }

    /// <summary>
    /// The systemd service a process belongs to, from the text of <c>/proc/&lt;pid&gt;/cgroup</c>, or
    /// null when it runs outside any service (a login session, a desktop app's <c>.scope</c>, a container).
    /// </summary>
    /// <remarks>
    /// systemd names a unit's cgroup after the unit, so the path is the attribution and needs no
    /// D-Bus call and no privileges. Both the unified line (<c>0::/path</c>) and the legacy
    /// <c>name=systemd</c> controller are understood.
    /// </remarks>
    public static string? ParseServiceUnit(ReadOnlySpan<char> cgroupFile)
    {
        string? unified = null;
        foreach (ReadOnlySpan<char> line in cgroupFile.EnumerateLines())
        {
            int first = line.IndexOf(':');
            int second = first < 0 ? -1 : first + 1 + line[(first + 1)..].IndexOf(':');
            if (second <= first)
            {
                continue;
            }

            ReadOnlySpan<char> controllers = line[(first + 1)..second];
            if (controllers is "name=systemd")
            {
                return ServiceUnitOfPath(line[(second + 1)..]);
            }

            if (controllers.IsEmpty)
            {
                unified = ServiceUnitOfPath(line[(second + 1)..]);
            }
        }

        return unified;
    }

    /// <summary>
    /// The innermost unit of a cgroup path when that unit is a service. Folders that are not units
    /// (a service that delegates its subtree) are looked through; any other unit type ends the
    /// search, so <c>user@1000.service/app.slice/app-firefox-1.scope</c> is not attributed to
    /// <c>user@1000.service</c>, but <c>user@1000.service/app.slice/dbus-broker.service</c> is attributed to the broker.
    /// </summary>
    public static string? ServiceUnitOfPath(ReadOnlySpan<char> path)
    {
        while (!path.IsEmpty)
        {
            int slash = path.LastIndexOf('/');
            ReadOnlySpan<char> segment = path[(slash + 1)..];
            path = slash < 0 ? default : path[..slash];
            if (segment.IsEmpty)
            {
                continue;
            }

            if (segment.EndsWith(".service", StringComparison.Ordinal))
            {
                return UnescapeUnitName(segment);
            }

            if (IsOtherUnit(segment))
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsOtherUnit(ReadOnlySpan<char> segment) =>
        segment.EndsWith(".scope", StringComparison.Ordinal)
        || segment.EndsWith(".slice", StringComparison.Ordinal)
        || segment.EndsWith(".socket", StringComparison.Ordinal)
        || segment.EndsWith(".mount", StringComparison.Ordinal)
        || segment.EndsWith(".swap", StringComparison.Ordinal)
        || segment.EndsWith(".target", StringComparison.Ordinal)
        || segment.EndsWith(".path", StringComparison.Ordinal)
        || segment.EndsWith(".timer", StringComparison.Ordinal)
        || segment.EndsWith(".automount", StringComparison.Ordinal)
        || segment.EndsWith(".device", StringComparison.Ordinal);

    /// <summary>Undoes systemd's <c>\xNN</c> escaping of unit names: <c>dev-disk-by\x2duuid</c> is <c>dev-disk-by-uuid</c>.</summary>
    public static string UnescapeUnitName(ReadOnlySpan<char> name)
    {
        if (!name.Contains('\\'))
        {
            return name.ToString();
        }

        var result = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '\\' && i + 3 < name.Length && name[i + 1] == 'x'
                && byte.TryParse(name.Slice(i + 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte code))
            {
                result.Append((char)code);
                i += 3;
            }
            else
            {
                result.Append(name[i]);
            }
        }

        return result.ToString();
    }

    /// <summary>The path of the unified (v2) hierarchy from <c>/proc/self/cgroup</c> ("0::/a/b"), or null.</summary>
    public static string? ParseUnifiedCgroupPath(ReadOnlySpan<char> cgroupFile)
    {
        foreach (ReadOnlySpan<char> line in cgroupFile.EnumerateLines())
        {
            if (line.StartsWith("0::", StringComparison.Ordinal))
            {
                return line[3..].ToString();
            }
        }

        return null;
    }

    private static ReadOnlySpan<char> NextToken(ref ReadOnlySpan<char> rest)
    {
        rest = rest.TrimStart();
        int end = rest.IndexOfAny(s_whitespace);
        ReadOnlySpan<char> token = end < 0 ? rest : rest[..end];
        rest = end < 0 ? default : rest[end..];
        return token;
    }

    private static bool NextNumber(ref ReadOnlySpan<char> rest, out long value) =>
        long.TryParse(NextToken(ref rest), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
