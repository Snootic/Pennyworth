using System.Globalization;
using System.Text.RegularExpressions;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Connected monitors from the DRM connectors (<c>/sys/class/drm/card0-HDMI-A-3</c>): the name from
/// the EDID, the resolution from the connector's mode list and the refresh rate from the EDID timing
/// for that resolution. Sysfs does not say which mode the compositor has set, so the figures are the
/// monitor's preferred mode (the first one the kernel lists), which is what a display runs at unless
/// the user changed it. Without a compositor session there is nothing more to ask without root.
/// </summary>
internal static partial class DrmDisplay
{
    private const string DrmFolder = "/sys/class/drm/";

    public static IReadOnlyList<DisplaySpecs> Read(SpecsRoot fs, IReadOnlyList<DrmCard> cards)
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
            if (Build(connector, cards.FirstOrDefault(c => c.Index == card)?.Gpu.Name ?? "?", bytes is null ? null : Edid.Parse(bytes), fs.ReadText(folder + "modes"), displays.Count == 0) is { } display)
            {
                displays.Add(display);
            }
        }

        return displays;
    }

    /// <summary>
    /// One monitor, from what its connector and EDID say. Null when neither gives a resolution (a
    /// connector that reports "connected" with no modes has nothing to show).
    /// </summary>
    public static DisplaySpecs? Build(string connector, string adapter, Edid? edid, string? modes, bool isPrimary)
    {
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
