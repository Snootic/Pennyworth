using System.Globalization;
using System.Text;

namespace Pennyworth.Platform.Linux;

/// <summary>One line of <c>/proc/self/mountinfo</c>. <see cref="MountPoint"/> is already unescaped.</summary>
internal sealed record MountEntry(
    string MountPoint,
    string FileSystemType,
    string Source,
    string MajorMinor,
    string Root,
    string MountOptions);

/// <summary>Pure parser for <c>/proc/self/mountinfo</c> (see proc_pid_mountinfo(5)).</summary>
internal static class MountTable
{
    private const string MountInfoPath = "/proc/self/mountinfo";

    /// <summary>
    /// Parses the table. Fields are <c>id parent maj:min root mountpoint options [optional...] - fstype source superopts</c>:
    /// the number of optional fields varies (<c>shared:1</c>, <c>master:2</c>...), so the fixed tail is located by the
    /// <c>-</c> separator, never by position. Spaces inside paths are escaped, so splitting on spaces is safe.
    /// Malformed lines are skipped.
    /// </summary>
    public static IReadOnlyList<MountEntry> Parse(string mountinfoText)
    {
        var entries = new List<MountEntry>();
        foreach (string line in mountinfoText.Split('\n'))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10)
                continue;
            int separator = Array.IndexOf(fields, "-", 6);
            if (separator < 0 || separator + 2 >= fields.Length)
                continue;
            entries.Add(new MountEntry(
                MountPoint: Unescape(fields[4]),
                FileSystemType: fields[separator + 1],
                Source: Unescape(fields[separator + 2]),
                MajorMinor: fields[2],
                Root: Unescape(fields[3]),
                MountOptions: fields[5]));
        }
        return entries;
    }

    /// <summary>Every mount point in the table, in file order.</summary>
    public static IReadOnlyList<string> MountPoints(string mountinfoText) =>
        Parse(mountinfoText).Select(e => e.MountPoint).ToList();

    /// <summary>Mount points of the running system; empty when the table cannot be read.</summary>
    public static IReadOnlyList<string> MountPoints() => MountPoints(ReadSystem());

    /// <summary>The raw table of the running system, or an empty string when unreadable.</summary>
    public static string ReadSystem()
    {
        try
        {
            return File.ReadAllText(MountInfoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>Decodes the kernel's three-digit octal escapes (<c>\040</c> space, <c>\011</c> tab, <c>\012</c> newline, <c>\134</c> backslash) as UTF-8 bytes.</summary>
    public static string Unescape(string value) => DecodeEscapes(value, octal: true);

    /// <summary>Decodes udev's <c>\xHH</c> escapes, used for names in <c>/dev/disk/by-label</c>, as UTF-8 bytes.</summary>
    public static string UnescapeUdev(string value) => DecodeEscapes(value, octal: false);

    private static string DecodeEscapes(string value, bool octal)
    {
        if (value.IndexOf('\\') < 0)
            return value;

        var bytes = new List<byte>(value.Length);
        int i = 0;
        while (i < value.Length)
        {
            if (value[i] == '\\' && TryDecode(value, i, octal, out byte decoded, out int length))
            {
                bytes.Add(decoded);
                i += length;
                continue;
            }
            int next = i;
            do
            {
                next++;
            }
            while (next < value.Length && value[next] != '\\');
            bytes.AddRange(Encoding.UTF8.GetBytes(value.AsSpan(i, next - i).ToString()));
            i = next;
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool TryDecode(string value, int index, bool octal, out byte decoded, out int length)
    {
        decoded = 0;
        if (octal)
        {
            length = 4;
            if (index + 4 > value.Length)
                return false;
            int number = 0;
            for (int k = 1; k <= 3; k++)
            {
                char c = value[index + k];
                if (c is < '0' or > '7')
                    return false;
                number = number * 8 + (c - '0');
            }
            if (number > 255)
                return false;
            decoded = (byte)number;
            return true;
        }

        length = 4;
        return index + 4 <= value.Length
            && value[index + 1] == 'x'
            && byte.TryParse(value.AsSpan(index + 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out decoded);
    }
}
