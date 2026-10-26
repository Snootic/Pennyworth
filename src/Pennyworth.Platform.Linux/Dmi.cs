using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Manufacturer, model, board and BIOS from <c>/sys/class/dmi/id</c>. Only the attributes the kernel
/// makes world-readable are read: the serial numbers and the product UUID are root-only and are never
/// requested, so nothing identifying the machine leaves it.
/// </summary>
internal static class Dmi
{
    private const string Folder = "/sys/class/dmi/id/";

    private static readonly string[] Placeholders =
    [
        "Unknown", "Default string", "System manufacturer", "System Product Name", "System Version",
        "Not Specified", "Not Applicable", "N/A", "None", "O.E.M.", "OEM",
    ];

    /// <summary>
    /// Firmware fills unset fields with placeholders ("To Be Filled By O.E.M.", "Default string",
    /// all zeros); those are not answers. Matches the cleaning the Windows reader applies to SMBIOS.
    /// </summary>
    public static string? Clean(string? text)
    {
        string? trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed)
            || trimmed.StartsWith("To Be Filled", StringComparison.OrdinalIgnoreCase)
            || Placeholders.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            || trimmed.Trim('0').Length == 0
            ? null
            : trimmed;
    }

    public static SystemSpecs ReadSystem(SpecsRoot fs) => new(
        fs.ReadText("/proc/sys/kernel/hostname") ?? Environment.MachineName,
        Environment.UserName,
        Clean(fs.ReadText(Folder + "sys_vendor")),
        Clean(fs.ReadText(Folder + "product_name")));

    public static BoardSpecs ReadBoard(SpecsRoot fs) => new(
        Clean(fs.ReadText(Folder + "board_vendor")),
        Clean(fs.ReadText(Folder + "board_name")),
        Clean(fs.ReadText(Folder + "bios_vendor")),
        Clean(fs.ReadText(Folder + "bios_version")),
        Clean(fs.ReadText(Folder + "bios_date")));
}
