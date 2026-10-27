using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>Which mode each monitor runs at, read from what the session says rather than from the EDID's preferred timing.</summary>
public class DisplayModesTests
{
    /// <summary>
    /// <c>kscreen-doctor -j</c> on this machine (KDE Plasma, Wayland), its list of 36 modes cut to three. The monitor's EDID prefers 60 Hz
    /// and the user runs it at 75: mode 2. A second, switched-off output and a second, 144 Hz one were added by hand.
    /// </summary>
    internal const string Kscreen = """
        {"outputs":[
          {"brightness":0,"clones":[],"connected":true,"currentModeId":"2","enabled":true,"id":1,"name":"HDMI-A-3","pos":{"x":0,"y":0},"priority":1,"rotation":1,"scale":1,
           "size":{"height":1080,"width":1920},"type":6,
           "modes":[{"id":"1","name":"1920x1080@60","refreshRate":60,"size":{"height":1080,"width":1920}},
                    {"id":"2","name":"1920x1080@75","refreshRate":74.9729995727539,"size":{"height":1080,"width":1920}},
                    {"id":"3","name":"1920x1080@60","refreshRate":60,"size":{"height":1080,"width":1920}}]},
          {"connected":true,"currentModeId":"4","enabled":false,"id":2,"name":"eDP-1","type":7,
           "modes":[{"id":"4","name":"1920x1080@60","refreshRate":60.0010,"size":{"height":1080,"width":1920}}]},
          {"connected":true,"currentModeId":"7","enabled":true,"id":3,"name":"DP-1","type":5,
           "modes":[{"id":"6","name":"2560x1440@60","refreshRate":59.95,"size":{"height":1440,"width":2560}},
                    {"id":"7","name":"2560x1440@144","refreshRate":143.912,"size":{"height":1440,"width":2560}}]}
        ],"screen":{"currentSize":{"height":1080,"width":1920},"id":0},"tabletModeAvailable":false,"tabletModeEngaged":false}
        """;

    /// <summary>
    /// <c>gdbus call --session --dest org.gnome.Mutter.DisplayConfig ... GetCurrentState</c> in the format the GVariant text printer uses
    /// (written from the D-Bus interface's documentation: no GNOME session was at hand): a 144 Hz external monitor and the built-in panel.
    /// </summary>
    internal const string Mutter = """
        (uint32 6, [(('DP-1', 'DEL', 'DELL U2720Q', 'ABC123'), [('3840x2160@59.997', 3840, 2160, 59.997, 1.0, [1.0, 2.0], {'is-preferred': <true>}), ('2560x1440@143.912', 2560, 1440, 143.912, 1.0, [1.0], {'is-current': <true>})], {'display-name': <'Dell Inc. 27"'>}), (('eDP-1', 'AUO', '0x1234', '0x00000000'), [('1920x1080@60.002', 1920, 1080, 60.002, 1.0, [1.0, 2.0], {'is-current': <true>, 'is-preferred': <true>}), ('1920x1080@48.001', 1920, 1080, 48.001, 1.0, [1.0, 2.0], @a{sv} {})], {'is-builtin': <true>})], [(0, 0, 1.0, uint32 0, true, [('DP-1', 'DEL', 'DELL U2720Q', 'ABC123')], @a{sv} {})], {'layout-mode': <uint32 1>, 'supports-changing-layout-mode': <false>})

        """;

    /// <summary><c>wlr-randr --json</c> (wlr-randr 0.4): one enabled monitor at 144 Hz, one that is switched off.</summary>
    internal const string Wlr = """
        [
          {"name":"DP-1","description":"Dell Inc. DELL U2720Q ABC123 (DP-1)","make":"Dell Inc.","model":"DELL U2720Q","serial":"ABC123",
           "physical_size":{"width":597,"height":336},"enabled":true,
           "modes":[{"width":3840,"height":2160,"refresh":59.997002,"preferred":true,"current":false},
                    {"width":2560,"height":1440,"refresh":143.912003,"preferred":false,"current":true}],
           "position":{"x":0,"y":0},"transform":"normal","scale":1.000000,"adaptive_sync":false},
          {"name":"HDMI-A-1","enabled":false,"modes":[{"width":1920,"height":1080,"refresh":60.000000,"preferred":true,"current":false}]}
        ]
        """;

    /// <summary><c>xrandr --current</c> on this machine (against Xwayland), shortened, plus a disconnected output and an interlaced one.</summary>
    internal const string Xrandr = """
        Screen 0: minimum 16 x 16, current 1920 x 1080, maximum 32767 x 32767
        HDMI-A-3 connected primary 1920x1080+0+0 (normal left inverted right x axis y axis) 528mm x 297mm
           1920x1080     74.91*+
           1440x1080     74.92  
           1024x768      74.67  
        DP-1 disconnected (normal left inverted right x axis y axis)
        VGA-1 connected 1024x768+1920+0 (normal left inverted right x axis y axis) 340mm x 270mm
           1280x1024     75.02 +  60.02  
           1024x768      75.03*   70.07    60.00  
           1920x1080i    60.00    50.00  
        HDMI-A-2 connected (normal left inverted right x axis y axis)
           1920x1080     60.00 +
        """;

    // ---- kscreen-doctor ----

    [Fact]
    public void Kscreen_gives_the_current_mode_not_the_preferred_one()
    {
        var modes = DisplayModes.ParseKscreenDoctor(Kscreen);

        Assert.Equal(
            [new CurrentMode("HDMI-A-3", 1920, 1080, 74.9729995727539), new CurrentMode("DP-1", 2560, 1440, 143.912)],
            modes); // eDP-1 is switched off.
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"outputs\":{}}")]
    [InlineData("{\"outputs\":[{\"name\":\"DP-1\",\"enabled\":true,\"currentModeId\":\"9\",\"modes\":[{\"id\":\"1\",\"refreshRate\":60,\"size\":{\"width\":1,\"height\":1}}]}]}")]
    public void Kscreen_text_that_is_not_an_answer_gives_nothing(string? text)
    {
        Assert.Empty(DisplayModes.ParseKscreenDoctor(text));
    }

    // ---- Mutter ----

    [Fact]
    public void Mutter_gives_the_mode_flagged_is_current()
    {
        var modes = DisplayModes.ParseMutter(Mutter);

        Assert.Equal([new CurrentMode("DP-1", 2560, 1440, 143.912), new CurrentMode("eDP-1", 1920, 1080, 60.002)], modes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Error: GDBus.Error:org.freedesktop.DBus.Error.ServiceUnknown")]
    [InlineData("(uint32 6, [], [], {})")]
    [InlineData("(uint32 6, [(('DP-1', 'a', 'b', 'c'), [('m', 1, 1, 60.0, 1.0, [1.0], {})], {})], [], {})")] // No current mode.
    [InlineData("(uint32 6, [((")] // Cut off.
    public void Mutter_text_that_is_not_an_answer_gives_nothing(string? text)
    {
        Assert.Empty(DisplayModes.ParseMutter(text));
    }

    [Fact]
    public void The_gvariant_reader_keeps_structure_and_drops_type_words()
    {
        var node = GVariantText.Parse("(uint32 6, ['a\\'b', 2.5, -3], {'k': <true>, 'n': <uint32 7>}, @as [] )");

        var list = Assert.IsType<GVariantList>(node);
        Assert.Equal(4, list.Items.Count);
        Assert.Equal("6", ((GVariantAtom)list.Items[0]).Text);
        Assert.Equal(["a'b", "2.5", "-3"], ((GVariantList)list.Items[1]).Items.Select(i => ((GVariantAtom)i).Text));
        var dictionary = (GVariantDictionary)list.Items[2];
        Assert.True(((GVariantAtom)dictionary["k"]!).IsTrue);
        Assert.Equal(7, ((GVariantAtom)dictionary["n"]!).AsInt);
        Assert.Empty(((GVariantList)list.Items[3]).Items);
    }

    [Theory]
    [InlineData("(1, 2")]
    [InlineData("'unterminated")]
    [InlineData("{1 2}")]
    [InlineData("(1) trailing")]
    [InlineData("<1")]
    public void The_gvariant_reader_gives_null_for_text_that_is_not_gvariant(string text)
    {
        Assert.Null(GVariantText.Parse(text));
    }

    // ---- wlr-randr ----

    [Fact]
    public void Wlr_randr_gives_the_current_mode_of_each_enabled_output()
    {
        Assert.Equal([new CurrentMode("DP-1", 2560, 1440, 143.912003)], DisplayModes.ParseWlrRandr(Wlr));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[{\"name\":\"DP-1\"}]")]
    [InlineData("[{\"name\":\"DP-1\",\"modes\":[{\"width\":1,\"height\":1,\"refresh\":60,\"current\":false}]}]")]
    [InlineData("error: failed to connect to display")]
    public void Wlr_randr_text_that_is_not_an_answer_gives_nothing(string? text)
    {
        Assert.Empty(DisplayModes.ParseWlrRandr(text));
    }

    // ---- xrandr ----

    [Fact]
    public void Xrandr_gives_the_rate_marked_with_a_star()
    {
        var modes = DisplayModes.ParseXrandr(Xrandr);

        Assert.Equal([new CurrentMode("HDMI-A-3", 1920, 1080, 74.91), new CurrentMode("VGA-1", 1024, 768, 75.03)], modes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Can't open display")]
    [InlineData("HDMI-1 connected\n   1920x1080     60.00 +\n")]
    public void Xrandr_text_without_a_running_mode_gives_nothing(string? text)
    {
        Assert.Empty(DisplayModes.ParseXrandr(text));
    }

    // ---- matching to the kernel's connector names ----

    [Theory]
    [InlineData("HDMI-A-3", "HDMI-3")]
    [InlineData("HDMI-3", "HDMI-3")]
    [InlineData("hdmi-a-1", "HDMI-1")]
    [InlineData("DVI-D-1", "DVI-1")]
    [InlineData("DVI-I-2", "DVI-2")]
    [InlineData("DP-1", "DP-1")]
    [InlineData("eDP-1", "EDP-1")]
    [InlineData("VGA-1", "VGA-1")]
    [InlineData(" DP-2 ", "DP-2")]
    public void Connector_names_match_across_the_kernel_gnome_and_xrandr(string name, string key)
    {
        Assert.Equal(key, DisplayModes.Normalize(name));
    }

    // ---- the chain ----

    private static Func<string, IReadOnlyList<string>, string?> Tools(Dictionary<string, string> answers, List<string>? asked = null) => (file, _) =>
    {
        asked?.Add(file);
        return answers.GetValueOrDefault(file);
    };

    [Fact]
    public void The_first_source_that_answers_wins_and_the_rest_are_not_asked()
    {
        var asked = new List<string>();
        var answers = new Dictionary<string, string> { ["kscreen-doctor"] = Kscreen, ["xrandr"] = Xrandr };

        var modes = DisplayProbe.Read(name => name == "XDG_CURRENT_DESKTOP" ? "KDE" : null, Tools(answers, asked));

        Assert.Equal(["kscreen-doctor"], asked);
        Assert.Equal(74.9729995727539, modes["HDMI-3"].RefreshHz);
        Assert.Equal(143.912, modes["DP-1"].RefreshHz);
    }

    [Fact]
    public void A_source_that_fails_hands_over_to_the_next()
    {
        var asked = new List<string>();

        var modes = DisplayProbe.Read(_ => null, Tools(new() { ["xrandr"] = Xrandr, ["wlr-randr"] = "garbage" }, asked));

        Assert.Equal(["wlr-randr", "kscreen-doctor", "gdbus", "xrandr"], asked);
        Assert.Equal(74.91, modes["HDMI-3"].RefreshHz);
        Assert.Equal(75.03, modes["VGA-1"].RefreshHz);
    }

    [Theory]
    [InlineData("GNOME", "gdbus")]
    [InlineData("ubuntu:GNOME", "gdbus")]
    [InlineData("KDE", "kscreen-doctor")]
    [InlineData("sway", "wlr-randr")]
    [InlineData(null, "wlr-randr")]
    public void The_running_desktops_own_source_is_asked_first(string? desktop, string first)
    {
        var asked = new List<string>();

        DisplayProbe.Read(name => name == "XDG_CURRENT_DESKTOP" ? desktop : null, Tools([], asked));

        Assert.Equal(first, asked[0]);
        Assert.Equal(4, asked.Count);
    }

    [Fact]
    public void Gnome_answers_through_gdbus()
    {
        var modes = DisplayProbe.Read(name => name == "XDG_CURRENT_DESKTOP" ? "GNOME" : null, Tools(new() { ["gdbus"] = Mutter }));

        Assert.Equal(143.912, modes["DP-1"].RefreshHz);
        Assert.Equal(60.002, modes["EDP-1"].RefreshHz);
    }

    [Fact]
    public void Nothing_answering_gives_nothing()
    {
        Assert.Empty(DisplayProbe.Read(_ => null, Tools([])));
    }

    // ---- into the display list ----

    private static Edid Samsung => Edid.Parse(Convert.FromHexString(
        "00ffffffffffff004c2d7d70000000001622010380351e782adfd5a35b4da125" +
        "0d5054bfef8081c0810081809500a9c0b300714f0101023a801871382d40582c" +
        "450010292100001e000000fd00304b1e5412000a202020202020000000fc004c" +
        "4632345433350a2020202020000000ff00534e30303030303030300a2020015f"))!;

    [Fact]
    public void A_75_hz_monitor_whose_edid_prefers_60_reads_75()
    {
        var running = new CurrentMode("HDMI-A-3", 1920, 1080, 74.9729995727539);

        DisplaySpecs? display = DrmDisplay.Build("HDMI-A-3", "Intel Arc B580", Samsung, "1920x1080\n1920x1080\n", isPrimary: true, running);

        Assert.Equal(new DisplaySpecs("Samsung LF24T35", "Intel Arc B580", 1920, 1080, 75, true), display);
    }

    [Fact]
    public void The_running_resolution_replaces_the_preferred_one_too()
    {
        var running = new CurrentMode("DP-1", 2560, 1440, 143.912);

        DisplaySpecs? display = DrmDisplay.Build("DP-1", "GPU", Samsung, "3840x2160\n", isPrimary: false, running);

        Assert.Equal((2560, 1440, 144), (display!.Width, display.Height, display.RefreshHz));
    }

    [Fact]
    public void Without_a_running_mode_the_edid_timing_is_still_used()
    {
        DisplaySpecs? display = DrmDisplay.Build("HDMI-A-3", "Intel Arc B580", Samsung, "1920x1080\n", isPrimary: true);

        Assert.Equal(60, display!.RefreshHz);
    }

    [Fact]
    public void The_reader_matches_the_session_s_monitors_to_connectors_by_name()
    {
        using var tree = new SpecsTree()
            .File("/sys/class/drm/card1-HDMI-A-3/status", "connected\n")
            .File("/sys/class/drm/card1-HDMI-A-3/modes", "1920x1080\n1280x720\n")
            .File("/sys/class/drm/card1-DP-1/status", "connected\n")
            .File("/sys/class/drm/card1-DP-1/modes", "2560x1440\n")
            .File("/sys/class/drm/card1-DP-2/status", "disconnected\n");

        // KDE names HDMI-A-3 like the kernel; the DP-1 monitor is left at what sysfs lists because KDE reports no running mode for it.
        PcSpecs specs = new LinuxSpecsReader(
            tree.Root,
            (file, _) => file == "kscreen-doctor" ? Kscreen.Replace("\"name\":\"DP-1\"", "\"name\":\"DP-9\"", StringComparison.Ordinal) : null,
            _ => null,
            name => name == "XDG_CURRENT_DESKTOP" ? "KDE" : null).Read();

        Assert.Equal(
            [("HDMI-A-3", 1920, 1080, 75), ("DP-1", 2560, 1440, 0)],
            specs.Displays.OrderBy(d => d.Name == "HDMI-A-3" ? 0 : 1).Select(d => (d.Name, d.Width, d.Height, d.RefreshHz)));
    }

    [Fact]
    public void The_reader_reads_gnome_s_mutter_names_for_hdmi()
    {
        using var tree = new SpecsTree()
            .File("/sys/class/drm/card0-HDMI-A-1/status", "connected\n")
            .File("/sys/class/drm/card0-HDMI-A-1/modes", "1920x1080\n");
        string mutter = "(uint32 1, [(('HDMI-1', 'ACR', 'KG241', '0'), [('1920x1080@74.973', 1920, 1080, 74.973, 1.0, [1.0], {'is-current': <true>, 'is-preferred': <true>})], {})], [], {})";

        PcSpecs specs = new LinuxSpecsReader(tree.Root, (file, _) => file == "gdbus" ? mutter : null, _ => null, name => name == "XDG_CURRENT_DESKTOP" ? "GNOME" : null).Read();

        Assert.Equal(75, Assert.Single(specs.Displays).RefreshHz);
    }
}
