using System.Globalization;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Battery and mains state from <c>/sys/class/power_supply</c>. A machine with no system battery
/// (a desktop) has nothing to report, which is null, the same as on Windows.
/// </summary>
internal static class PowerSupply
{
    private const string Folder = "/sys/class/power_supply/";

    /// <param name="Type">"Battery", "Mains", "USB"...</param>
    /// <param name="Scope">"Device" marks a peripheral's own battery (a wireless mouse, a game controller), "System" or absent the machine's.</param>
    /// <param name="Online">The <c>online</c> flag of a mains or USB supply; null when the attribute is absent.</param>
    internal sealed record Supply(string Type, string? Scope, string? Status, int? Capacity, bool? Online, bool? Present);

    /// <summary>
    /// Combines the supplies into the one battery the Specs screen shows. Peripherals' batteries and
    /// batteries marked absent are not the machine's. With several batteries the percentage is their
    /// mean. When the firmware exposes no mains supply at all, a battery that is charging or full
    /// still proves the charger is plugged in.
    /// </summary>
    public static BatterySpecs? Combine(IEnumerable<Supply> supplies)
    {
        var all = supplies.ToList();
        var batteries = all.Where(s => s.Type.Equals("Battery", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(s.Scope, "Device", StringComparison.OrdinalIgnoreCase)
            && s.Present != false).ToList();
        if (batteries.Count == 0)
        {
            return null;
        }

        var lines = all.Where(s => s.Type.Equals("Mains", StringComparison.OrdinalIgnoreCase) || s.Type.StartsWith("USB", StringComparison.OrdinalIgnoreCase)).ToList();
        bool charging = batteries.Any(b => Is(b.Status, "Charging"));
        bool onAc = lines.Count > 0
            ? lines.Any(l => l.Online == true)
            : batteries.Any(b => Is(b.Status, "Charging") || Is(b.Status, "Full") || Is(b.Status, "Not charging"));
        int[] levels = batteries.Where(b => b.Capacity is >= 0 and <= 100).Select(b => b.Capacity!.Value).ToArray();
        return new BatterySpecs(levels.Length > 0 ? (int)Math.Round(levels.Average(), MidpointRounding.AwayFromZero) : null, charging, onAc || charging);
    }

    public static BatterySpecs? Read(SpecsRoot fs) => Combine(fs.Names(Folder).Select(name =>
    {
        string folder = $"{Folder}{name}/";
        return new Supply(
            fs.ReadText(folder + "type") ?? "",
            fs.ReadText(folder + "scope"),
            fs.ReadText(folder + "status"),
            fs.ReadNumber(folder + "capacity") is { } capacity ? (int)capacity : null,
            fs.ReadNumber(folder + "online") is { } online ? online != 0 : null,
            fs.ReadNumber(folder + "present") is { } present ? present != 0 : null);
    }));

    private static bool Is(string? status, string expected) => string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);
}
