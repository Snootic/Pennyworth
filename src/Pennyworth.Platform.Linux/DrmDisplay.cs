using System.Globalization;
using System.Text.RegularExpressions;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Connected monitors from the DRM connectors (<c>/sys/class/drm/card0-HDMI-A-3</c>): the name from
/// the EDID, and the mode from <see cref="DisplayProbe"/> (what the compositor or X server has set, matched
/// to the connector by name). Sysfs does not say which mode is set, so when no running session can be asked (a
/// console, no helper program installed) the figures are the monitor's preferred mode: the resolution from the
/// first line the kernel lists and the refresh rate from the EDID timing for it, which is what a display runs
/// at unless the user changed it.
/// </summary>
internal static partial class DrmDisplay
{
    private const string DrmFolder = "/sys/class/drm/";

    /// <param name="current">The running mode of each monitor by normalized connector name; null or empty when none was found.</param>
    public static IReadOnlyList<DisplaySpecs> Read(SpecsRoot fs, IReadOnlyList<DrmCard> cards, IReadOnlyDictionary<string, CurrentMode>? current = null)
    {
        var connectors = new List<(int Card, string Folder, string Connector)>();
        foreach (string entry in fs.Names(DrmFolder))
        {
            if (ConnectorName().Match(entry) is { Success: true } match && fs.ReadText($"{DrmFolder}{entry}/status") == "connected")
            {
                connectors.Add((int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), $"{DrmFolder}{entry}/", match.Groups[2].Value));
            }
        }

        // The boot display's card first: its first monitor is the main one.
        int Rank(int card)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].Index == card)
                {
                    return i;
                }
            }

            return int.MaxValue;
        }

        var displays = new List<DisplaySpecs>();
        foreach (var (card, folder, connector) in connectors.OrderBy(c => Rank(c.Card)))
        {
            byte[]? bytes = fs.ReadBytes(folder + "edid");
            CurrentMode? running = current is not null && current.TryGetValue(DisplayModes.Normalize(connector), out CurrentMode? found) ? found : null;
            if (Build(connector, cards.FirstOrDefault(c => c.Index == card)?.Gpu.Name ?? "?", bytes is null ? null : Edid.Parse(bytes), fs.ReadText(folder + "modes"), displays.Count == 0, running) is { } display)
            {
                displays.Add(display);
            }
        }

        return displays;
    }

    /// <summary>
    /// One monitor. The running mode, when the session reported one, gives the resolution and the refresh rate; otherwise
    /// the connector and EDID give the preferred mode. Null when nothing gives a resolution (a connector that reports
    /// "connected" with no modes has nothing to show).
    /// </summary>
    public static DisplaySpecs? Build(string connector, string adapter, Edid? edid, string? modes, bool isPrimary, CurrentMode? running = null)
    {
        if (running is { Width: > 0, Height: > 0 })
        {
            return new DisplaySpecs(Name(connector, edid), adapter, running.Width, running.Height, (int)Math.Round(running.RefreshHz), isPrimary);
        }

        (int Width, int Height)? size = ParseFirstMode(modes) ?? (edid?.Preferred is { } native ? (native.Width, native.Height) : null);
        if (size is not { } known)
        {
            return null;
        }

        (int width, int height) = known;

        double? refresh = edid?.Preferred is { } preferred && preferred.Width == width && preferred.Height == height
            ? preferred.RefreshHz
            : edid?.RefreshAt(width, height);
        return new DisplaySpecs(Name(connector, edid), adapter, width, height, refresh is { } hz ? (int)Math.Round(hz) : 0, isPrimary);
    }

    /// <summary>The resolution of the first line of a connector's <c>modes</c> file ("1920x1080", "1920x1080i"): the preferred mode.</summary>
    public static (int Width, int Height)? ParseFirstMode(string? modes)
    {
        Match match = Mode().Match(modes ?? "");
        return match.Success ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)) : null;
    }

    /// <summary>"Samsung LF24T35": the maker (unless the name already says it) and the model; the connector when there is no EDID.</summary>
    private static string Name(string connector, Edid? edid)
    {
        string? maker = edid?.Manufacturer is { } id ? Edid.MakerName(id) : null;
        return (edid?.Name, maker) switch
        {
            ({ } model, { } company) => model.StartsWith(company, StringComparison.OrdinalIgnoreCase) ? model : $"{company} {model}",
            ({ } model, null) => model,
            (null, { } company) => $"{company} display",
            _ => connector,
        };
    }

    [GeneratedRegex(@"^card(\d+)-(.+)$")]
    private static partial Regex ConnectorName();

    [GeneratedRegex(@"^\s*(\d+)x(\d+)")]
    private static partial Regex Mode();
}
