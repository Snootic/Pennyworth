namespace Pennyworth.Platform.Linux;

/// <summary>
/// Where the specs readers find <c>/proc</c>, <c>/sys</c> and <c>/etc</c>. The root is injectable so a
/// test can hand a reader a fabricated tree; every read that fails (no such file, permission denied,
/// the <c>EINVAL</c> some sysfs attributes raise) is the same answer, null: a source that is missing
/// degrades one field, it never throws out of a reader.
/// </summary>
internal sealed class SpecsRoot(string? root = null)
{
    private readonly string _root = string.IsNullOrEmpty(root) ? "/" : root;

    /// <summary>The path on disk of an absolute path such as <c>/sys/block</c>.</summary>
    public string Resolve(string path) => Path.Combine(_root, path.TrimStart('/'));

    /// <summary>The trimmed text of a file, or null when it is unreadable or blank.</summary>
    public string? ReadText(string path) => ReadFile(Resolve(path));

    /// <summary>
    /// A file in the folder a symlink points into, read as <c>link/../file</c> would on a real system.
    /// The kernel resolves <c>..</c> after following the link, but .NET collapses it in the path text
    /// first, so the link has to be followed here (a USB interface's <c>device</c> link names the
    /// interface; the USB device that has the <c>product</c> string is its parent).
    /// </summary>
    public string? ReadBesideTarget(string link, string file) =>
        Path.GetDirectoryName(Target(link)?.FullName) is { } folder ? ReadFile(Path.Combine(folder, file)) : null;

    private static string? ReadFile(string fullPath)
    {
        try
        {
            string text = File.ReadAllText(fullPath).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public byte[]? ReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(Resolve(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A whole-number attribute (<c>size</c>, <c>capacity</c>); null when absent or not a number.</summary>
    public long? ReadNumber(string path) =>
        long.TryParse(ReadText(path), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long value) ? value : null;

    /// <summary>The names inside a directory in ordinal order; empty when it cannot be listed.</summary>
    public IReadOnlyList<string> Names(string directory)
    {
        try
        {
            string[] names = Directory.EnumerateFileSystemEntries(Resolve(directory)).Select(Path.GetFileName).OfType<string>().ToArray();
            Array.Sort(names, StringComparer.Ordinal);
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>True when the path exists and, for a symlink, is not dangling.</summary>
    public bool Exists(string path) => Directory.Exists(Resolve(path)) || File.Exists(Resolve(path));

    /// <summary>The final target of a symlink (<c>device/driver</c> points at <c>.../drivers/xe</c>), or null when it is not one.</summary>
    public FileSystemInfo? Target(string path)
    {
        try
        {
            return File.ResolveLinkTarget(Resolve(path), returnFinalTarget: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The last segment of a symlink's final target ("xe", "0000:0a:00.0"), or null.</summary>
    public string? TargetName(string path) => Target(path)?.Name;
}
