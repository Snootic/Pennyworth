using System.Text;
using System.Text.RegularExpressions;

namespace Pennyworth.Assistant;

/// <summary>
/// Replaces what identifies the person before a cloud request: the profile path, folder and
/// file names under it that aren't well-known locations, the user and machine names, and
/// e-mail addresses. Placeholders are stable within one anonymiser (the same folder always
/// becomes the same <c>&lt;folder-N&gt;</c>) and <see cref="Restore"/> maps them back for display.
/// When unsure where a path ends, it hides more rather than less.
/// </summary>
public sealed partial class Anonymizer
{
    /// <summary>Folder names under a profile that say nothing about the person.</summary>
    public static readonly IReadOnlySet<string> CommonFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Local", "LocalLow", "Roaming", "Temp", "Documents", "Downloads", "Desktop", "Pictures", "Music",
        "Videos", "OneDrive", "Saved Games", "Favorites", "Links", "Contacts", "Searches", "3D Objects", "Microsoft",
        "Windows", "Packages", "Programs", "Google", "Chrome", "Edge", "Mozilla", "Firefox", "Profiles", "BraveSoftware",
        "Brave-Browser", "User Data", "Default", "Cache", "Code Cache", "GPUCache", "cache2", "Steam", "NVIDIA",
        "NVIDIA Corporation", "AMD", "Intel", "Discord", "Spotify", "Adobe", "D3DSCache", "CrashDumps", "INetCache",
        "Explorer", "Recent", "Start Menu", "Fonts", "Application Data", "Teams", "Slack", "Zoom", "WhatsApp", "Telegram Desktop",
        // Linux: the XDG folders, packaged-app folders and the stock folders of a Linux home.
        ".cache", ".config", ".local", "share", "state", "Public", "Templates", "snap", "flatpak", ".var", "app", "Trash",
        "applications", "icons", "fonts", "mesa_shader_cache", "thumbnails", "mozilla", "chromium", "google-chrome", "microsoft-edge",
        "vivaldi", "zen", "librewolf", "Games", "Applications", "AppImages", ".mozilla", ".steam", ".npm", ".cargo", ".rustup",
        ".gradle", ".nuget", ".dotnet", ".docker", ".vscode", ".wine", ".android", ".java", ".bun",
    };

    private readonly string _profile;
    private readonly Func<string, bool> _isKnownFolder;
    private readonly Regex? _names;
    private readonly Dictionary<string, string> _placeholders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _originals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = [];

    /// <param name="profilePath">"C:\Users\ana".</param>
    /// <param name="isKnownFolder">Whether a folder name under the profile is safe to send; defaults to <see cref="CommonFolders"/>.</param>
    public Anonymizer(string userName, string profilePath, string machineName, Func<string, bool>? isKnownFolder = null)
    {
        _profile = Path.TrimEndingDirectorySeparator(profilePath);
        _isKnownFolder = isKnownFolder ?? CommonFolders.Contains;
        _originals["<profile>"] = _profile;

        var names = new List<(string Value, string Placeholder)>();
        string leaf = Path.GetFileName(_profile);
        foreach (var (value, placeholder) in new[] { (userName, "<user>"), (leaf, "<user>"), (machineName, "<pc>") })
        {
            if (value.Trim().Length >= 2 && !names.Any(n => string.Equals(n.Value, value, StringComparison.OrdinalIgnoreCase)))
            {
                names.Add((value.Trim(), placeholder));
                _placeholders[value.Trim()] = placeholder;
                _originals.TryAdd(placeholder, value.Trim());
            }
        }

        if (names.Count > 0)
        {
            string alternatives = string.Join("|", names.OrderByDescending(n => n.Value.Length).Select(n => Regex.Escape(n.Value)));
            _names = new Regex(@"(?<![\p{L}\p{N}])(" + alternatives + @")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
    }

    /// <summary>For the current Windows user on this PC.</summary>
    public static Anonymizer ForCurrentUser() => new(
        Environment.UserName,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.MachineName);

    public string Anonymize(string text)
    {
        var sb = new StringBuilder();
        int last = 0;
        Regex profilePaths = ProfilePathPattern(_profile);
        foreach (Match path in profilePaths.Matches(text))
        {
            string value = path.Value.TrimEnd(); // "Temp (18 GB)": the space belongs to the sentence
            sb.Append(Outside(text[last..path.Index]));
            sb.Append(AnonymizePath(value));
            last = path.Index + value.Length;
        }

        sb.Append(Outside(text[last..]));
        return sb.ToString();
    }

    /// <summary>Puts the original names back, for showing the answer to the person.</summary>
    public string Restore(string text) =>
        PlaceholderPattern().Replace(text, match => _originals.TryGetValue(match.Value, out string? original) ? original : match.Value);

    private string Outside(string text)
    {
        text = EmailPattern().Replace(text, match => PlaceholderFor(match.Value, "email"));
        return _names is null ? text : _names.Replace(text, match => _placeholders[match.Value]);
    }

    private string AnonymizePath(string path)
    {
        string[] segments = path[_profile.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder("<profile>");
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            bool isFile = i == segments.Length - 1 && Path.HasExtension(segment) && !_isKnownFolder(segment);
            sb.Append('\\').Append(_isKnownFolder(segment) ? segment : PlaceholderFor(segment, isFile ? "file" : "folder"));
        }

        return sb.ToString();
    }

    private string PlaceholderFor(string original, string kind)
    {
        if (!_placeholders.TryGetValue(original, out string? placeholder))
        {
            int n = _counters[kind] = _counters.GetValueOrDefault(kind) + 1;
            placeholder = $"<{kind}-{n}>";
            _placeholders[original] = placeholder;
            _originals[placeholder] = original;
        }

        return placeholder;
    }

    /// <summary>
    /// The profile path and the segments after it. A segment runs until a backslash, a line break,
    /// "," ";" "(" ")" quotes, " · ", or a period followed by a space or the end; spaces are kept
    /// inside segments so a name like "Ana Souza" is hidden whole.
    /// </summary>
    private static Regex ProfilePathPattern(string profile) => new(
        Regex.Escape(profile) + @"(?![^\\\s,;()<>""|?*.])(?:\\(?:[^\\\r\n,;()<>""|?*·.]|\.(?!\s|$))+)*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"<(?:profile|user|pc|folder-\d+|file-\d+|email-\d+)>")]
    private static partial Regex PlaceholderPattern();
}
