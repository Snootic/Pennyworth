using System.Text;

namespace Pennyworth.Platform.Linux;

/// <summary>One video timing a monitor advertises.</summary>
internal sealed record EdidTiming(int Width, int Height, double RefreshHz);

/// <summary>
/// What a monitor says about itself in its EDID block (VESA E-EDID 1.4). The serial-number descriptor
/// is deliberately not read: it identifies the unit, and nothing here needs it.
/// </summary>
/// <param name="Manufacturer">The three-letter PNP id ("SAM"), null when the field is not valid.</param>
/// <param name="Name">The monitor name descriptor ("LF24T35"), null when there is none.</param>
/// <param name="Preferred">The native timing: the first detailed timing of the base block.</param>
/// <param name="Timings">Every detailed timing (base block and CTA-861 extensions) and standard timing.</param>
internal sealed record Edid(
    string? Manufacturer, int ProductCode, string? Name, int? WidthCm, int? HeightCm, EdidTiming? Preferred, IReadOnlyList<EdidTiming> Timings)
{
    private const int BlockSize = 128;
    private static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    private static readonly Dictionary<string, string> Makers = new(StringComparer.Ordinal)
    {
        ["ACR"] = "Acer", ["AOC"] = "AOC", ["APP"] = "Apple", ["AUO"] = "AU Optronics", ["AUS"] = "ASUS",
        ["BNQ"] = "BenQ", ["BOE"] = "BOE", ["CMN"] = "Innolux", ["CMO"] = "Chi Mei", ["DEL"] = "Dell",
        ["ENC"] = "Eizo", ["GBT"] = "Gigabyte", ["GSM"] = "LG", ["HIT"] = "Hitachi", ["HPN"] = "HP",
        ["HWP"] = "HP", ["IVM"] = "Iiyama", ["LEN"] = "Lenovo", ["LGD"] = "LG Display", ["MEI"] = "Panasonic",
        ["MSI"] = "MSI", ["NEC"] = "NEC", ["PHL"] = "Philips", ["SAM"] = "Samsung", ["SDC"] = "Samsung Display",
        ["SHP"] = "Sharp", ["SNY"] = "Sony", ["TSB"] = "Toshiba", ["VSC"] = "ViewSonic",
    };

    /// <summary>The company behind a PNP id ("SAM" is Samsung), or the id itself when it is not a common one.</summary>
    public static string MakerName(string pnpId) => Makers.TryGetValue(pnpId, out string? name) ? name : pnpId;

    /// <summary>Parses a 128-byte base block plus any extension blocks; null when it is not an EDID.</summary>
    public static Edid? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < BlockSize || !data[..Header.Length].SequenceEqual(Header))
        {
            return null;
        }

        var timings = new List<EdidTiming>();
        EdidTiming? preferred = null;
        string? name = null;
        for (int offset = 54; offset < 54 + 4 * 18; offset += 18)
        {
            ReadOnlySpan<byte> descriptor = data.Slice(offset, 18);
            if (descriptor[0] == 0 && descriptor[1] == 0)
            {
                if (descriptor[3] == 0xFC)
                {
                    name ??= Text(descriptor[5..]);
                }
            }
            else if (DetailedTiming(descriptor) is { } timing)
            {
                preferred ??= timing;
                timings.Add(timing);
            }
        }

        bool sixteenTenOrBetter = data[19] >= 3 || data[18] > 1; // Before EDID 1.3 aspect code 00 meant 1:1.
        for (int offset = 38; offset < 54; offset += 2)
        {
            if (StandardTiming(data[offset], data[offset + 1], sixteenTenOrBetter) is { } standard)
            {
                timings.Add(standard);
            }
        }

        int extensions = Math.Min((int)data[126], data.Length / BlockSize - 1);
        for (int block = 1; block <= extensions; block++)
        {
            ReadOnlySpan<byte> extension = data.Slice(block * BlockSize, BlockSize);
            if (extension[0] != 0x02) // CTA-861: the only extension that carries more detailed timings.
            {
                continue;
            }

            for (int offset = extension[2]; offset >= 4 && offset + 18 <= BlockSize - 1; offset += 18)
            {
                ReadOnlySpan<byte> descriptor = extension.Slice(offset, 18);
                if (descriptor[0] == 0 && descriptor[1] == 0)
                {
                    break; // Padding: the detailed timings end here.
                }

                if (DetailedTiming(descriptor) is { } timing)
                {
                    timings.Add(timing);
                }
            }
        }

        return new Edid(
            DecodeManufacturer(data[8], data[9]),
            data[10] | data[11] << 8,
            name,
            data[21] > 0 ? data[21] : null,
            data[22] > 0 ? data[22] : null,
            preferred,
            timings);
    }

    /// <summary>The refresh rate the monitor advertises at this resolution (the highest, null when none), in Hz.</summary>
    public double? RefreshAt(int width, int height) =>
        Timings.Where(t => t.Width == width && t.Height == height).Select(t => (double?)t.RefreshHz).Max();

    /// <summary>Three 5-bit letters ("A" = 1) packed big-endian into two bytes; null when any is outside A-Z.</summary>
    private static string? DecodeManufacturer(byte high, byte low)
    {
        int packed = high << 8 | low;
        Span<char> letters = stackalloc char[3];
        for (int i = 0; i < 3; i++)
        {
            int letter = packed >> (10 - 5 * i) & 0x1F;
            if (letter is < 1 or > 26)
            {
                return null;
            }

            letters[i] = (char)('A' + letter - 1);
        }

        return new string(letters);
    }

    /// <summary>A text descriptor: ASCII, ended by a line feed and padded with spaces.</summary>
    private static string? Text(ReadOnlySpan<byte> bytes)
    {
        string text = Encoding.ASCII.GetString(bytes[..13]);
        int end = text.IndexOf('\n');
        string trimmed = (end >= 0 ? text[..end] : text).Trim();
        return trimmed.Length > 0 && trimmed.All(c => c is >= ' ' and <= '~') ? trimmed : null;
    }

    /// <summary>
    /// An 18-byte detailed timing: pixel clock in 10 kHz units, then the 12-bit active and blanking
    /// sizes split across bytes, and the refresh rate as clock / (htotal * vtotal). Interlaced timings
    /// are skipped: their rate is per field and no modern panel uses them as its native mode.
    /// </summary>
    private static EdidTiming? DetailedTiming(ReadOnlySpan<byte> d)
    {
        int clock = d[0] | d[1] << 8;
        int horizontalActive = d[2] | (d[4] & 0xF0) << 4;
        int horizontalBlank = d[3] | (d[4] & 0x0F) << 8;
        int verticalActive = d[5] | (d[7] & 0xF0) << 4;
        int verticalBlank = d[6] | (d[7] & 0x0F) << 8;
        long total = (long)(horizontalActive + horizontalBlank) * (verticalActive + verticalBlank);
        return clock == 0 || horizontalActive == 0 || verticalActive == 0 || total == 0 || (d[17] & 0x80) != 0
            ? null
            : new EdidTiming(horizontalActive, verticalActive, clock * 10_000.0 / total);
    }

    /// <summary>A two-byte standard timing: width = (first + 31) * 8, aspect in the top bits of the second, refresh = low 6 bits + 60.</summary>
    private static EdidTiming? StandardTiming(byte first, byte second, bool sixteenTen)
    {
        if ((first is 0x00 or 0x01) && (second is 0x00 or 0x01))
        {
            return null; // Unused slot.
        }

        int width = (first + 31) * 8;
        int height = (second >> 6) switch
        {
            0 => sixteenTen ? width * 10 / 16 : width,
            1 => width * 3 / 4,
            2 => width * 4 / 5,
            _ => width * 9 / 16,
        };
        return new EdidTiming(width, height, (second & 0x3F) + 60);
    }
}
