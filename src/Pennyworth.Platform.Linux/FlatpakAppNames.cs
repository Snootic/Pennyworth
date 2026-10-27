using System.Collections.Concurrent;
using System.Globalization;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The readable name of a Flatpak app, for the two folders that carry its id: the app itself
/// (<c>.../flatpak/app/com.google.Chrome</c>, in the user's or the system's installation) and its data
/// (<c>~/.var/app/com.google.Chrome</c>). The name comes from the app's exported <c>.desktop</c> entry, which every
/// installation keeps under <c>exports/share/applications</c>; an app with no entry (or one that was uninstalled and left its
/// data behind) has no name here and keeps its id.
/// </summary>
internal sealed class FlatpakAppNames : IAppNames
{
    private static readonly string[] SystemInstallations = ["/var/lib/flatpak"];

    private readonly string[] _installations;
    private readonly Func<string, string?> _readText;
    private readonly ConcurrentDictionary<(string Id, string Culture), string?> _names = new();

    /// <param name="dataHome">The user's data home; their Flatpak installation is <c>flatpak</c> inside it.</param>
    /// <param name="installations">Replaces the user's and the system's installation folders, for tests.</param>
    /// <param name="readText">Reads a file's text or returns null; defaults to the file system.</param>
    public FlatpakAppNames(string dataHome, IEnumerable<string>? installations = null, Func<string, string?>? readText = null)
    {
        _installations = [.. installations ?? [Path.Combine(dataHome, "flatpak"), .. SystemInstallations]];
        _readText = readText ?? ReadFile;
    }

    public string? NameOf(string fullPath, CultureInfo culture)
    {
        if (AppId(fullPath) is not { } id)
        {
            return null;
        }

        return _names.GetOrAdd((id, culture.Name), key => Lookup(key.Id, culture));
    }

    /// <summary>The id when the path is <c>.../flatpak/app/&lt;id&gt;</c> or <c>.../.var/app/&lt;id&gt;</c>, else null.</summary>
    internal static string? AppId(string fullPath)
    {
        string[] names = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length < 3 || names[^2] != "app" || names[^3] is not ("flatpak" or ".var"))
        {
            return null;
        }

        string id = names[^1];
        return id.Contains('.') && !id.StartsWith('.') ? id : null;
    }

    private string? Lookup(string id, CultureInfo culture)
    {
        foreach (string installation in _installations)
        {
            string entry = Path.Combine(installation, "exports", "share", "applications", id + ".desktop");
            if (_readText(entry) is { } text && DesktopEntry.Name(text, culture) is { } name)
            {
                return name;
            }
        }

        return null;
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
