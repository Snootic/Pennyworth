using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The operating-system section: <c>/etc/os-release</c> for the name, the kernel release for the
/// "build" (Windows' build number has no Linux counterpart; the kernel is what changes under an
/// update), <c>/proc/uptime</c>, and the install date.
/// </summary>
internal static class OsRelease
{
    /// <summary>
    /// Parses os-release(5): <c>KEY=value</c> lines that are shell-quoted. Double quotes unescape
    /// <c>\$ \` \" \\</c>, single quotes are literal, a later assignment wins, comments and lines
    /// without a key are skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int equals = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || equals <= 0)
            {
                continue;
            }

            values[line[..equals].TrimEnd()] = Unquote(line[(equals + 1)..].Trim());
        }

        return values;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return Unescape(value[1..^1], "$`\"\\");
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1];
        }

        return Unescape(value, null);
    }

    /// <summary>A backslash escapes the next character; inside double quotes only the characters in <paramref name="escapable"/>.</summary>
    private static string Unescape(string value, string? escapable)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }

        var text = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && (escapable is null || escapable.Contains(value[i + 1])))
            {
                i++;
            }

            text.Append(value[i]);
        }

        return text.ToString();
    }

    /// <summary>The first field of <c>/proc/uptime</c> ("150572.40 4015209.13"), or null.</summary>
    public static TimeSpan? ParseUptime(string? text)
    {
        string? first = text?.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    /// <summary>
    /// The birth time <c>stat -c %W</c> prints (seconds since the epoch), or null: coreutils prints
    /// 0 when the filesystem does not record one, and that must not read as 1970.
    /// </summary>
    public static DateTimeOffset? ParseBirthTime(string? statOutput) =>
        long.TryParse(statOutput?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()
            : null;

    public static OsSpecs Read(SpecsRoot fs, Func<string, IReadOnlyList<string>, string?> probe)
    {
        IReadOnlyDictionary<string, string> release = Parse(fs.ReadText("/etc/os-release") ?? fs.ReadText("/usr/lib/os-release") ?? "");
        string? Value(string key) => release.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;

        return new OsSpecs(
            Value("PRETTY_NAME") ?? Value("NAME") ?? "Linux",
            Value("VERSION_ID") ?? Value("BUILD_ID"),
            fs.ReadText("/proc/sys/kernel/osrelease") ?? "?",
            RuntimeInformation.OSArchitecture.ToString(),
            InstalledOn(fs, probe),
            ParseUptime(fs.ReadText("/proc/uptime")) ?? TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>
    /// When the system was installed. The birth time of <c>/</c> is the moment its filesystem was made,
    /// which is the install on ext4, xfs and btrfs (a btrfs snapshot keeps the birth time of its
    /// origin, so a snapper rollback does not move it). <c>/etc/machine-id</c> is the fallback: it is
    /// written once, on the first boot. Where the filesystem records no birth time at all, that file's
    /// modification time is the nearest thing left.
    /// </summary>
    private static DateTimeOffset? InstalledOn(SpecsRoot fs, Func<string, IReadOnlyList<string>, string?> probe)
    {
        foreach (string path in new[] { "/", "/etc/machine-id" })
        {
            if (ParseBirthTime(probe("stat", ["-c", "%W", fs.Resolve(path)])) is { } born)
            {
                return born;
            }
        }

        try
        {
            string machineId = fs.Resolve("/etc/machine-id");
            return File.Exists(machineId) ? new DateTimeOffset(File.GetLastWriteTimeUtc(machineId)).ToLocalTime() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
