using System.Text;

namespace Pennyworth.Monitoring.Hardware;

/// <summary>What the firmware's SMBIOS tables say about the machine.</summary>
public sealed record SmbiosInfo(
    string? SystemManufacturer,
    string? SystemProduct,
    string? BoardManufacturer,
    string? BoardProduct,
    string? BiosVendor,
    string? BiosVersion,
    string? BiosDate,
    string? ProcessorSocket,
    int? MemorySlots,
    IReadOnlyList<MemoryModule> Modules);

/// <summary>
/// Parses the raw SMBIOS table Windows returns for <c>GetSystemFirmwareTable('RSMB')</c>:
/// an 8-byte header, then structures, each a formatted area followed by its strings.
/// </summary>
public static class Smbios
{
    private const int HeaderSize = 8;

    public static SmbiosInfo Parse(ReadOnlySpan<byte> raw) => ParseStructures(raw, HeaderSize);

    /// <summary>
    /// The same, for a table with no header: the structures alone, as Linux exposes them in
    /// <c>/sys/firmware/dmi/tables/DMI</c>.
    /// </summary>
    public static SmbiosInfo ParseStructures(ReadOnlySpan<byte> table) => ParseStructures(table, 0);

    private static SmbiosInfo ParseStructures(ReadOnlySpan<byte> raw, int start)
    {
        string? systemManufacturer = null, systemProduct = null, boardManufacturer = null, boardProduct = null;
        string? biosVendor = null, biosVersion = null, biosDate = null, socket = null;
        int? slots = null;
        var modules = new List<MemoryModule>();

        int offset = start;
        while (offset + 4 <= raw.Length)
        {
            byte type = raw[offset];
            byte length = raw[offset + 1];
            if (type == 127 || length < 4 || offset + length > raw.Length)
            {
                break; // End-of-table marker, or a broken table.
            }

            ReadOnlySpan<byte> formatted = raw.Slice(offset, length);
            int stringsEnd = FindStringsEnd(raw, offset + length);
            string[] strings = ReadStrings(raw[(offset + length)..stringsEnd]);

            switch (type)
            {
                case 0:
                    biosVendor = Text(formatted, strings, 0x04);
                    biosVersion = Text(formatted, strings, 0x05);
                    biosDate = Text(formatted, strings, 0x08);
                    break;
                case 1:
                    systemManufacturer = Text(formatted, strings, 0x04);
                    systemProduct = Text(formatted, strings, 0x05);
                    break;
                case 2:
                    boardManufacturer = Text(formatted, strings, 0x04);
                    boardProduct = Text(formatted, strings, 0x05);
                    break;
                case 4:
                    socket ??= Text(formatted, strings, 0x04);
                    break;
                case 16 when formatted.Length > 0x0E:
                    slots = (slots ?? 0) + Word(formatted, 0x0D);
                    break;
                case 17 when ParseModule(formatted, strings) is { } module:
                    modules.Add(module);
                    break;
            }

            offset = stringsEnd;
        }

        return new SmbiosInfo(systemManufacturer, systemProduct, boardManufacturer, boardProduct,
            biosVendor, biosVersion, biosDate, socket, slots, modules);
    }

    /// <summary>A memory device (type 17); null for empty slots.</summary>
    private static MemoryModule? ParseModule(ReadOnlySpan<byte> s, string[] strings)
    {
        if (s.Length < 0x15)
        {
            return null;
        }

        int size = Word(s, 0x0C);
        long bytes = size switch
        {
            0 or 0xFFFF => 0,
            0x7FFF when s.Length >= 0x20 => (long)DWord(s, 0x1C) * 1024 * 1024, // Extended size, in MB.
            _ when (size & 0x8000) != 0 => (long)(size & 0x7FFF) * 1024,            // In KB.
            _ => (long)size * 1024 * 1024,                                          // In MB.
        };
        if (bytes == 0)
        {
            return null;
        }

        string slot = Text(s, strings, 0x10) ?? Text(s, strings, 0x11) ?? "?";
        int? speed = s.Length > 0x16 ? Speed(Word(s, 0x15), s, 0x54) : null;
        int? configured = s.Length > 0x21 ? Speed(Word(s, 0x20), s, 0x58) : null;
        return new MemoryModule(
            slot,
            bytes,
            MemoryType(s[0x12]),
            speed,
            configured,
            s.Length > 0x17 ? Clean(Text(s, strings, 0x17)) : null,
            s.Length > 0x1A ? Clean(Text(s, strings, 0x1A)) : null,
            FormFactor(s[0x0E]));
    }

    /// <summary>0 means unknown; 0xFFFF means "see the extended field".</summary>
    private static int? Speed(int value, ReadOnlySpan<byte> s, int extendedOffset) => value switch
    {
        0 => null,
        0xFFFF when s.Length >= extendedOffset + 4 => (int)DWord(s, extendedOffset),
        0xFFFF => null,
        _ => value,
    };

    private static string? MemoryType(byte type) => type switch
    {
        0x0F => "SDRAM",
        0x12 => "DDR",
        0x13 => "DDR2",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => null,
    };

    private static string? FormFactor(byte form) => form switch
    {
        0x09 => "DIMM",
        0x0D => "SO-DIMM",
        _ => null,
    };

    /// <summary>Firmware often pads text or uses placeholders like "Unknown" or "To Be Filled By O.E.M.".</summary>
    private static string? Clean(string? text)
    {
        string? trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed)
            || trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("To Be Filled", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            || trimmed.Trim('0').Length == 0
            ? null
            : trimmed;
    }

    /// <summary>The string a byte field refers to (1-based; 0 = none), cleaned of placeholders.</summary>
    private static string? Text(ReadOnlySpan<byte> s, string[] strings, int offset)
    {
        if (offset >= s.Length)
        {
            return null;
        }

        int index = s[offset];
        return index >= 1 && index <= strings.Length ? Clean(strings[index - 1]) : null;
    }

    private static int FindStringsEnd(ReadOnlySpan<byte> raw, int start)
    {
        for (int i = start; i + 1 < raw.Length; i++)
        {
            if (raw[i] == 0 && raw[i + 1] == 0)
            {
                return i + 2;
            }
        }

        return raw.Length;
    }

    private static string[] ReadStrings(ReadOnlySpan<byte> area)
    {
        var strings = new List<string>();
        int start = 0;
        for (int i = 0; i < area.Length; i++)
        {
            if (area[i] == 0)
            {
                if (i > start)
                {
                    strings.Add(Encoding.ASCII.GetString(area[start..i]));
                }

                start = i + 1;
            }
        }

        return [.. strings];
    }

    private static int Word(ReadOnlySpan<byte> s, int offset) => s[offset] | (s[offset + 1] << 8);

    private static uint DWord(ReadOnlySpan<byte> s, int offset) => BitConverter.ToUInt32(s.Slice(offset, 4));
}
