using System.Text;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>
/// A synthetic SMBIOS structure table, built byte by byte from the specification (the real one is root-only
/// and cannot be captured here): the same layout as <c>/sys/firmware/dmi/tables/DMI</c>, with no header.
/// </summary>
internal static class SmbiosTable
{
    /// <summary>
    /// Three slots: 8 GB DDR4 DIMM, an empty one, and a 32 GB DDR5 SO-DIMM whose size and speed sit in the
    /// extended fields.
    /// </summary>
    public static byte[] ThreeSlots() => Table(
        MemoryArray(devices: 3),
        Module(sizeMb: 8192, slot: "DIMM_A1", type: 0x1A, form: 0x09, speed: 3200, configured: 3000, maker: "Kingston", part: "KF3200C16D4/8GX"),
        Module(sizeMb: 0, slot: "DIMM_A2", type: 0x02, form: 0x09, speed: 0, configured: 0, maker: "Unknown", part: "Unknown"),
        Module(sizeMb: 0x7FFF, extendedMb: 32768, slot: "DIMM_B1", type: 0x22, form: 0x0D, speed: 0xFFFF, extendedSpeed: 6000, configured: 4800,
            maker: "To Be Filled By O.E.M.", part: "CMK32GX5M1B6000"));

    /// <summary>What <c>dmidecode -t 17</c> prints for <see cref="ThreeSlots"/>.</summary>
    public const string ThreeSlotsDmidecode = """
        # dmidecode 3.5
        Getting SMBIOS data from sysfs.
        SMBIOS 3.3.0 present.

        Handle 0x0030, DMI type 17, 92 bytes
        Memory Device
        	Array Handle: 0x002D
        	Total Width: 64 bits
        	Size: 8 GB
        	Form Factor: DIMM
        	Locator: DIMM_A1
        	Bank Locator: BANK 0
        	Type: DDR4
        	Type Detail: Synchronous Unbuffered (Unregistered)
        	Speed: 3200 MT/s
        	Manufacturer: Kingston
        	Serial Number: 12345678
        	Part Number: KF3200C16D4/8GX
        	Configured Memory Speed: 3000 MT/s

        Handle 0x0031, DMI type 17, 92 bytes
        Memory Device
        	Size: No Module Installed
        	Form Factor: DIMM
        	Locator: DIMM_A2
        	Type: Unknown
        	Speed: Unknown
        	Manufacturer: Unknown
        	Part Number: Unknown
        	Configured Memory Speed: Unknown

        Handle 0x0032, DMI type 17, 92 bytes
        Memory Device
        	Size: 32 GB
        	Form Factor: SODIMM
        	Locator: DIMM_B1
        	Type: DDR5
        	Speed: 6000 MT/s
        	Manufacturer: To Be Filled By O.E.M.
        	Part Number: CMK32GX5M1B6000
        	Configured Memory Speed: 4800 MT/s

        """;

    /// <summary>Type 16, the physical memory array: "number of memory devices" at 0x0D.</summary>
    private static byte[] MemoryArray(int devices)
    {
        var f = new byte[0x17 - 4];
        f[0x0D - 4] = (byte)devices;
        return Structure(16, f);
    }

    /// <summary>A type 17 memory device, SMBIOS 3.3 layout (0x5C bytes).</summary>
    private static byte[] Module(int sizeMb, string slot, byte type, byte form, int speed, int configured, string maker, string part,
        int extendedMb = 0, int extendedSpeed = 0)
    {
        var f = new byte[0x5C - 4];
        void Put16(int at, int v) { f[at - 4] = (byte)v; f[at - 3] = (byte)(v >> 8); }
        void Put32(int at, int v) => BitConverter.GetBytes(v).CopyTo(f, at - 4);
        Put16(0x0C, sizeMb);
        f[0x0E - 4] = form;
        f[0x10 - 4] = 1; // device locator: string 1
        f[0x12 - 4] = type;
        Put16(0x15, speed);
        f[0x17 - 4] = 2; // manufacturer: string 2
        f[0x1A - 4] = 3; // part number: string 3
        Put32(0x1C, extendedMb);
        Put16(0x20, configured);
        Put32(0x54, extendedSpeed);
        return Structure(17, f, slot, maker, part);
    }

    /// <summary>The formatted area (header included), then the string-set: each string null-terminated, an extra null at the end.</summary>
    private static byte[] Structure(byte type, byte[] formatted, params string[] strings)
    {
        var bytes = new List<byte> { type, (byte)(formatted.Length + 4), 0, 0 };
        bytes.AddRange(formatted);
        foreach (string s in strings)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(s));
            bytes.Add(0);
        }

        bytes.Add(0);
        if (strings.Length == 0)
        {
            bytes.Add(0);
        }

        return [.. bytes];
    }

    private static byte[] Table(params byte[][] structures) =>
        [.. structures.SelectMany(s => s), 127, 4, 0, 0, 0, 0];
}
