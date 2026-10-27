namespace Pennyworth.Platform.Linux;

/// <summary>
/// Asks the running session which mode each monitor is in. Sysfs only lists what a monitor can do, never what the compositor
/// chose, so a 75 Hz monitor whose EDID prefers 60 Hz would read as 60. The sources are tried one after another and the first
/// that names at least one monitor wins: KDE (<c>kscreen-doctor</c>), GNOME (Mutter over D-Bus), wlroots compositors
/// (<c>wlr-randr</c>) and X11 (<c>xrandr</c>). A program that is missing, fails or hangs is simply skipped, so on any one
/// desktop most of them cost nothing; the desktop named by <c>XDG_CURRENT_DESKTOP</c> goes first.
/// </summary>
internal static class DisplayProbe
{
    /// <summary>The longest any one of these programs may take; they answer in milliseconds when they answer at all.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private sealed record Source(string Name, string Program, string[] Arguments, Func<string?, IReadOnlyList<CurrentMode>> Parse);

    private static readonly Source Kscreen = new("kde", "kscreen-doctor", ["-j"], DisplayModes.ParseKscreenDoctor);

    private static readonly Source Mutter = new("gnome", "gdbus",
        ["call", "--session", "--dest", "org.gnome.Mutter.DisplayConfig", "--object-path", "/org/gnome/Mutter/DisplayConfig", "--method", "org.gnome.Mutter.DisplayConfig.GetCurrentState"],
        DisplayModes.ParseMutter);

    private static readonly Source Wlr = new("wlroots", "wlr-randr", ["--json"], DisplayModes.ParseWlrRandr);

    private static readonly Source Xrandr = new("x11", "xrandr", ["--current"], DisplayModes.ParseXrandr);

    /// <summary>The running modes keyed by <see cref="DisplayModes.Normalize"/>d connector name; empty when no source answered.</summary>
    /// <param name="environment">Environment variable lookup.</param>
    /// <param name="probe">Runs a program and returns its output, or null when it is missing, fails or outlives <see cref="Timeout"/>.</param>
    public static IReadOnlyDictionary<string, CurrentMode> Read(Func<string, string?> environment, Func<string, IReadOnlyList<string>, string?> probe)
    {
        foreach (Source source in Order(environment("XDG_CURRENT_DESKTOP")))
        {
            IReadOnlyList<CurrentMode> modes = source.Parse(probe(source.Program, source.Arguments));
            if (modes.Count > 0)
            {
                var byConnector = new Dictionary<string, CurrentMode>(StringComparer.Ordinal);
                foreach (CurrentMode mode in modes)
                {
                    byConnector.TryAdd(DisplayModes.Normalize(mode.Connector), mode);
                }

                return byConnector;
            }
        }

        return new Dictionary<string, CurrentMode>();
    }

    /// <summary>The sources in the order to ask: the running desktop's own first, every other after it.</summary>
    private static IEnumerable<Source> Order(string? desktop)
    {
        string name = desktop ?? "";
        bool gnome = name.Contains("GNOME", StringComparison.OrdinalIgnoreCase) || name.Contains("Unity", StringComparison.OrdinalIgnoreCase);
        bool kde = name.Contains("KDE", StringComparison.OrdinalIgnoreCase);
        return gnome ? [Mutter, Kscreen, Wlr, Xrandr]
            : kde ? [Kscreen, Mutter, Wlr, Xrandr]
            : [Wlr, Kscreen, Mutter, Xrandr];
    }
}
