namespace Pennyworth.Platform.Linux;

/// <summary>
/// The <c>KEY=value</c> lines of a sysfs <c>uevent</c> file (<c>DRIVER=xe</c>, <c>PCI_SLOT_NAME=0000:05:00.0</c>).
/// Unlike <c>os-release</c> there is no quoting.
/// </summary>
internal static class Uevent
{
    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim();
            int equals = line.IndexOf('=');
            if (equals > 0)
            {
                values[line[..equals]] = line[(equals + 1)..];
            }
        }

        return values;
    }
}
