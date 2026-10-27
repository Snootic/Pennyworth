using System.Text.RegularExpressions;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Names the compositor that is running: the session type from <c>XDG_SESSION_TYPE</c> (or <c>WAYLAND_DISPLAY</c> /
/// <c>DISPLAY</c> when that is unset), then the compositor or window manager by scanning <c>/proc/&lt;pid&gt;/comm</c> for the
/// programs below, then, when no process is visible (a sandbox shows only its own), by asking the session bus who owns
/// <c>org.kde.KWin</c> or <c>org.gnome.Mutter.DisplayConfig</c>. The version comes from the program's own <c>--version</c>.
/// Nothing needs root; a source that is missing leaves its part out.
/// </summary>
internal static partial class CompositorDetector
{
    /// <summary>A compositor or window manager: the process name (<c>comm</c>, 15 characters at most) and how it is shown.</summary>
    /// <param name="Wayland">Null when it runs under both session types (gnome-shell).</param>
    internal sealed record Known(string Comm, string Name, bool? Wayland, string? Shell = null, string[]? VersionArguments = null);

    /// <summary>The longest a <c>--version</c> run of one of these programs may take.</summary>
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(2);

    private static readonly string[] Long = ["--version"];

    /// <summary>In the order they win: a full desktop's compositor before a window manager that may also be running.</summary>
    internal static readonly IReadOnlyList<Known> Programs =
    [
        new("kwin_wayland", "KWin", true, null, Long),
        new("gnome-shell", "Mutter", null, "GNOME Shell", Long),
        new("sway", "sway", true, null, Long),
        new("Hyprland", "Hyprland", true, null, Long),
        new("labwc", "labwc", true, null, Long),
        new("weston", "Weston", true, null, Long),
        new("river", "river", true, null, ["-version"]),
        new("wayfire", "Wayfire", true, null, Long),
        new("niri", "niri", true, null, Long),
        new("cosmic-comp", "COSMIC", true),
        new("kwin_x11", "KWin", false, null, Long),
        new("openbox", "Openbox", false, null, Long),
        new("xfwm4", "Xfwm", false, null, Long),
        new("marco", "Marco", false, null, Long),
        new("muffin", "Muffin", false, null, Long),
        new("metacity", "Metacity", false, null, Long),
        new("i3", "i3", false, null, Long),
        new("bspwm", "bspwm", false, null, Long),
        new("icewm", "IceWM", false, null, Long),
        new("fluxbox", "Fluxbox", false, null, Long),
        new("awesome", "awesome", false, null, Long),
        new("qtile", "Qtile", false, null, Long),
        new("dwm", "dwm", false, null, null),
        new("herbstluftwm", "herbstluftwm", false, null, Long),
        new("jwm", "JWM", false, null, Long),
        new("fvwm", "FVWM", false, null, Long),
    ];

    public static CompositorSpecs? Read(SpecsRoot fs, Func<string, string?> environment, Func<string, IReadOnlyList<string>, string?> probe)
    {
        if (SessionOf(environment) is not { } session)
        {
            return null; // A text console or an SSH session: no compositor to name.
        }

        IReadOnlySet<string> running = ProcessNames(fs);
        Known? known = Choose(session == "Wayland", running) ?? FromSessionBus(session == "Wayland", probe);
        if (known is null)
        {
            return new CompositorSpecs(session);
        }

        string? version = known.VersionArguments is { } arguments ? Version(known.Comm, arguments, probe) : null;
        return new CompositorSpecs(session, known.Name, version, known.Shell);
    }

    /// <summary>"Wayland" or "X11"; null with no graphical session at all.</summary>
    internal static string? SessionOf(Func<string, string?> environment) =>
        environment("XDG_SESSION_TYPE")?.ToLowerInvariant() switch
        {
            "wayland" => "Wayland",
            "x11" => "X11",
            _ when !string.IsNullOrEmpty(environment("WAYLAND_DISPLAY")) => "Wayland",
            _ when !string.IsNullOrEmpty(environment("DISPLAY")) => "X11",
            _ => null,
        };

    /// <summary>The first program of <see cref="Programs"/> that is running and fits the session type.</summary>
    internal static Known? Choose(bool wayland, IReadOnlySet<string> running) =>
        Programs.FirstOrDefault(program => (program.Wayland ?? wayland) == wayland && running.Contains(program.Comm));

    private static IReadOnlySet<string> ProcessNames(SpecsRoot fs)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in fs.Names("/proc"))
        {
            if (entry.All(char.IsAsciiDigit) && fs.ReadText($"/proc/{entry}/comm") is { } comm)
            {
                names.Add(comm);
            }
        }

        return names;
    }

    /// <summary>Who owns a well-known session bus name tells the compositor when the process list cannot.</summary>
    private static Known? FromSessionBus(bool wayland, Func<string, IReadOnlyList<string>, string?> probe)
    {
        if (OwnsName(probe, "org.kde.KWin"))
        {
            return Programs.First(p => p.Comm == (wayland ? "kwin_wayland" : "kwin_x11"));
        }

        return OwnsName(probe, "org.gnome.Mutter.DisplayConfig") ? Programs.First(p => p.Comm == "gnome-shell") : null;
    }

    private static bool OwnsName(Func<string, IReadOnlyList<string>, string?> probe, string name) =>
        ParseNameHasOwner(probe("gdbus", ["call", "--session", "--dest", "org.freedesktop.DBus", "--object-path", "/org/freedesktop/DBus", "--method", "org.freedesktop.DBus.NameHasOwner", name]));

    /// <summary><c>gdbus call ... NameHasOwner</c> prints <c>(true,)</c> or <c>(false,)</c>.</summary>
    internal static bool ParseNameHasOwner(string? output) => output?.Trim() is "(true,)";

    private static string? Version(string program, string[] arguments, Func<string, IReadOnlyList<string>, string?> probe) =>
        ParseVersion(probe(program, arguments));

    /// <summary>
    /// The first dotted number of a <c>--version</c> text: <c>kwin 6.8.1</c>, <c>GNOME Shell 47.1</c>, <c>sway version 1.10.1</c>,
    /// <c>This is xfwm4 version 4.18.0 (revision 6dcf4a1) for Xfce 4.18</c>. A single number (<c>47</c>) counts when nothing dotted is there.
    /// </summary>
    internal static string? ParseVersion(string? output)
    {
        string text = (output ?? "").Split('\n').FirstOrDefault(line => line.Trim().Length > 0) ?? "";
        Match dotted = Dotted().Match(text);
        return dotted.Success ? dotted.Value : Whole().Match(text) is { Success: true } whole ? whole.Value : null;
    }

    [GeneratedRegex(@"\d+(?:\.\d+)+")]
    private static partial Regex Dotted();

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex Whole();
}
