using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pennyworth.Platform.Linux;

/// <summary>The mode a monitor is running right now, as its compositor or X server reports it.</summary>
/// <param name="Connector">As the source names it ("DP-1", "HDMI-A-3", "HDMI-1"); compare with <see cref="DisplayModes.Normalize"/>.</param>
internal sealed record CurrentMode(string Connector, int Width, int Height, double RefreshHz);

/// <summary>
/// Parsers for the programs that know which mode each monitor runs at, which sysfs does not: KDE's <c>kscreen-doctor -j</c>,
/// GNOME's Mutter over D-Bus, <c>wlr-randr --json</c> and <c>xrandr --current</c>. Each takes the program's output and
/// returns one <see cref="CurrentMode"/> per enabled monitor; text it cannot read gives an empty list, not an exception.
/// </summary>
internal static partial class DisplayModes
{
    /// <summary>
    /// A key that matches the same monitor under the kernel's and the other sources' names: case-insensitive, with the
    /// connector sub-type dropped (<c>HDMI-A-1</c> and <c>HDMI-1</c>, <c>DVI-D-1</c> and <c>DVI-1</c>).
    /// </summary>
    public static string Normalize(string connector)
    {
        string name = connector.Trim().ToUpperInvariant();
        return SubType().Replace(name, "$1-$3");
    }

    /// <summary>
    /// <c>kscreen-doctor -j</c>: <c>outputs[]</c> with <c>name</c>, <c>enabled</c>, <c>currentModeId</c> and <c>modes[]</c> of
    /// <c>id</c>, <c>size</c> and <c>refreshRate</c>.
    /// </summary>
    public static IReadOnlyList<CurrentMode> ParseKscreenDoctor(string? json)
    {
        var modes = new List<CurrentMode>();
        if (!TryParse(json, out JsonDocument? document))
        {
            return modes;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetPropertyOf("outputs", JsonValueKind.Array, out JsonElement outputs))
            {
                return modes;
            }

            foreach (JsonElement output in outputs.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object))
            {
                if (output.GetString("name") is not { } name
                    || output.TryGetProperty("enabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.False
                    || output.GetString("currentModeId") is not { } current
                    || !output.TryGetPropertyOf("modes", JsonValueKind.Array, out JsonElement available))
                {
                    continue;
                }

                foreach (JsonElement mode in available.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object && m.GetString("id") == current))
                {
                    if (mode.TryGetPropertyOf("size", JsonValueKind.Object, out JsonElement size)
                        && size.GetNumber("width") is { } width && size.GetNumber("height") is { } height && mode.GetNumber("refreshRate") is { } hz)
                    {
                        modes.Add(new CurrentMode(name, (int)width, (int)height, hz));
                        break;
                    }
                }
            }
        }

        return modes;
    }

    /// <summary>
    /// <c>gdbus call --session --dest org.gnome.Mutter.DisplayConfig ... GetCurrentState</c>: the reply is
    /// <c>(serial, monitors, logical monitors, properties)</c>; a monitor is <c>((connector, vendor, product, serial), modes, properties)</c>
    /// and a mode <c>(id, width, height, refresh, scale, supported scales, properties)</c>, the running one with
    /// <c>'is-current': &lt;true&gt;</c> in its properties.
    /// </summary>
    public static IReadOnlyList<CurrentMode> ParseMutter(string? text)
    {
        var modes = new List<CurrentMode>();
        if (GVariantText.Parse(text) is not GVariantList { Items.Count: >= 2 } reply || reply.Items[1] is not GVariantList monitors)
        {
            return modes;
        }

        foreach (GVariantNode monitorNode in monitors.Items)
        {
            if (monitorNode is not GVariantList { Items.Count: >= 2 } monitor
                || monitor.Items[0] is not GVariantList { Items: [GVariantAtom connector, ..] }
                || monitor.Items[1] is not GVariantList available)
            {
                continue;
            }

            foreach (GVariantNode modeNode in available.Items)
            {
                if (modeNode is GVariantList { Items.Count: >= 7 } mode
                    && mode.Items[6] is GVariantDictionary { } properties && properties["is-current"] is GVariantAtom { IsTrue: true }
                    && mode.Items[1] is GVariantAtom { AsInt: { } width } && mode.Items[2] is GVariantAtom { AsInt: { } height }
                    && mode.Items[3] is GVariantAtom { AsDouble: { } hz })
                {
                    modes.Add(new CurrentMode(connector.Text, width, height, hz));
                    break;
                }
            }
        }

        return modes;
    }

    /// <summary><c>wlr-randr --json</c>: an array of outputs with <c>name</c>, <c>enabled</c> and <c>modes[]</c> of <c>width</c>, <c>height</c>, <c>refresh</c> and <c>current</c>.</summary>
    public static IReadOnlyList<CurrentMode> ParseWlrRandr(string? json)
    {
        var modes = new List<CurrentMode>();
        if (!TryParse(json, out JsonDocument? document))
        {
            return modes;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return modes;
            }

            foreach (JsonElement output in document.RootElement.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object))
            {
                if (output.GetString("name") is not { } name
                    || output.TryGetProperty("enabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.False
                    || !output.TryGetPropertyOf("modes", JsonValueKind.Array, out JsonElement available))
                {
                    continue;
                }

                foreach (JsonElement mode in available.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object))
                {
                    if (mode.TryGetProperty("current", out JsonElement current) && current.ValueKind == JsonValueKind.True
                        && mode.GetNumber("width") is { } width && mode.GetNumber("height") is { } height && mode.GetNumber("refresh") is { } hz)
                    {
                        modes.Add(new CurrentMode(name, (int)width, (int)height, hz));
                        break;
                    }
                }
            }
        }

        return modes;
    }

    /// <summary>
    /// <c>xrandr --current</c>: <c>DP-1 connected primary 2560x1440+0+0 ...</c> and under it one line per mode, <c>   2560x1440   74.91*+  59.95</c>,
    /// where <c>*</c> marks the rate in use and <c>+</c> the preferred one. A monitor that is connected but switched off has no <c>*</c>.
    /// </summary>
    public static IReadOnlyList<CurrentMode> ParseXrandr(string? text)
    {
        var modes = new List<CurrentMode>();
        string? output = null;
        foreach (string line in (text ?? "").Split('\n'))
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                if (output is not null && ModeLine().Match(line) is { Success: true } match)
                {
                    foreach (Match rate in Rate().Matches(match.Groups[3].Value))
                    {
                        if (rate.Groups[2].Value.Contains('*'))
                        {
                            modes.Add(new CurrentMode(
                                output,
                                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                                double.Parse(rate.Groups[1].Value, CultureInfo.InvariantCulture)));
                            output = null;
                            break;
                        }
                    }
                }

                continue;
            }

            output = Header().Match(line) is { Success: true } header ? header.Groups[1].Value : null;
        }

        return modes;
    }

    private static bool TryParse(string? json, [NotNullWhen(true)] out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetString(this JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? GetNumber(this JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) ? number : null;

    private static bool TryGetPropertyOf(this JsonElement element, string name, JsonValueKind kind, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind == kind;

    [GeneratedRegex(@"^(HDMI|DVI|DP|MINIDP)-([A-Z])-(\d+)$")]
    private static partial Regex SubType();

    [GeneratedRegex(@"^(\S+) connected\b")]
    private static partial Regex Header();

    [GeneratedRegex(@"^\s+(\d+)x(\d+)i?\s+(.*)$")]
    private static partial Regex ModeLine();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)([*+]*)")]
    private static partial Regex Rate();
}
