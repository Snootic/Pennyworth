namespace Pennyworth.Platform.Linux.Tests;

public class EdidTests
{
    /// <summary>
    /// The EDID of the Samsung LF24T35 on this machine's HDMI-A-3, as the kernel exposes it
    /// (<c>/sys/class/drm/card0-HDMI-A-3/edid</c>): a base block and one CTA-861 extension. The unit's
    /// serial number is blanked and the checksums recomputed; everything else is as captured.
    /// </summary>
    private const string SamsungLF24T35 =
        "00ffffffffffff004c2d7d70000000001622010380351e782adfd5a35b4da125" +
        "0d5054bfef8081c0810081809500a9c0b300714f0101023a801871382d40582c" +
        "450010292100001e000000fd00304b1e5412000a202020202020000000fc004c" +
        "4632345433350a2020202020000000ff00534e30303030303030300a2020015f" +
        "02031cb146901f0413031267030c0010000024681a00000101304b00011d00bc" +
        "52d01e20b828554010292100001e8c0ad090204031200c405500102921000018" +
        "8c0ad08a20e02d10103e9600102921000018011d007251d01e206e2855001029" +
        "2100001e2a4480a0703827403020350010292100001a00000000000000000018";

    private static byte[] Sample() => Convert.FromHexString(SamsungLF24T35);

    [Fact]
    public void Reads_the_identity_the_monitor_reports()
    {
        Edid edid = Edid.Parse(Sample())!;

        Assert.Equal("SAM", edid.Manufacturer);
        Assert.Equal(0x707D, edid.ProductCode);
        Assert.Equal("LF24T35", edid.Name);
        Assert.Equal(53, edid.WidthCm);
        Assert.Equal(30, edid.HeightCm);
    }

    [Fact]
    public void The_first_detailed_timing_is_the_preferred_mode_and_its_refresh_is_clock_over_total_pixels()
    {
        EdidTiming preferred = Edid.Parse(Sample())!.Preferred!;

        // 148.5 MHz / (2200 x 1125 total pixels).
        Assert.Equal(1920, preferred.Width);
        Assert.Equal(1080, preferred.Height);
        Assert.Equal(60.0, preferred.RefreshHz, precision: 2);
    }

    [Fact]
    public void Collects_timings_from_the_extension_block_and_the_standard_timings_too()
    {
        Edid edid = Edid.Parse(Sample())!;

        // 1280x720 only appears as a standard timing (0x81c0 -> (0x81 + 31) * 8 wide, 16:9, 60 Hz).
        Assert.Contains(edid.Timings, t => t is { Width: 1280, Height: 720 } && Math.Abs(t.RefreshHz - 60) < 0.01);
        // The extension block holds more 1920x1080 modes than the base block's one.
        Assert.True(edid.Timings.Count(t => t is { Width: 1920, Height: 1080 }) > 1);
        Assert.True(edid.RefreshAt(1920, 1080) >= 60);
        Assert.Null(edid.RefreshAt(3840, 2160));
    }

    [Fact]
    public void Rejects_data_that_is_not_an_edid()
    {
        byte[] wrongHeader = Sample();
        wrongHeader[1] = 0x00;

        Assert.Null(Edid.Parse(wrongHeader));
        Assert.Null(Edid.Parse(Sample().AsSpan(0, 127)));
        Assert.Null(Edid.Parse([]));
    }

    [Fact]
    public void A_base_block_alone_still_parses_even_when_the_extension_count_lies()
    {
        byte[] baseBlock = Sample()[..128];

        Edid? edid = Edid.Parse(baseBlock); // byte 126 says one extension follows, but none does.

        Assert.NotNull(edid);
        Assert.Equal("LF24T35", edid.Name);
    }

    [Fact]
    public void A_manufacturer_id_outside_a_to_z_is_unknown_rather_than_garbage()
    {
        byte[] data = Sample();
        data[8] = 0;
        data[9] = 0;

        Assert.Null(Edid.Parse(data)!.Manufacturer);
    }

    [Fact]
    public void A_monitor_without_a_name_descriptor_has_no_name()
    {
        byte[] data = Sample();
        data[0x5D] = 0xFE; // The 0xFC "monitor name" descriptor becomes "unspecified text".

        Assert.Null(Edid.Parse(data)!.Name);
    }

    [Theory]
    [InlineData("SAM", "Samsung")]
    [InlineData("GSM", "LG")]
    [InlineData("XYZ", "XYZ")]
    public void Names_the_common_makers_and_keeps_the_id_of_the_rest(string id, string expected) =>
        Assert.Equal(expected, Edid.MakerName(id));
}
