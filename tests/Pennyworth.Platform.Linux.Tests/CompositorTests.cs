using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>Naming the compositor of the running session for Specs > Graphics.</summary>
public class CompositorTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;

    private static SpecsTree Processes(params string[] comms)
    {
        var tree = new SpecsTree().Dir("/proc");
        for (int i = 0; i < comms.Length; i++)
        {
            tree.File($"/proc/{1000 + i}/comm", comms[i] + "\n");
        }

        return tree.File("/proc/self/comm", "ignored\n").File("/proc/sys/kernel/hostname", "x");
    }

    private static Func<string, IReadOnlyList<string>, string?> Tools(Dictionary<string, string> answers, List<string>? asked = null) => (file, arguments) =>
    {
        asked?.Add(file + " " + string.Join(' ', arguments));
        return answers.GetValueOrDefault(file);
    };

    // ---- the session ----

    [Theory]
    [InlineData("wayland", null, null, "Wayland")]
    [InlineData("Wayland", null, null, "Wayland")]
    [InlineData("x11", "wayland-0", ":0", "X11")]
    [InlineData(null, "wayland-0", ":0", "Wayland")]
    [InlineData("tty", "wayland-1", null, "Wayland")]
    [InlineData(null, null, ":0", "X11")]
    [InlineData("tty", null, null, null)]
    [InlineData(null, null, null, null)]
    [InlineData(null, "", "", null)]
    public void The_session_type_comes_from_the_session_variable_then_from_the_display_variables(string? type, string? wayland, string? display, string? expected)
    {
        var environment = Env(("XDG_SESSION_TYPE", type!), ("WAYLAND_DISPLAY", wayland!), ("DISPLAY", display!));

        Assert.Equal(expected, CompositorDetector.SessionOf(environment));
    }

    // ---- the compositor ----

    [Fact]
    public void Kde_on_wayland_is_kwin_with_the_version_from_its_own_binary()
    {
        // This machine: kwin_wayland, its wrapper (a different comm), plasmashell and Xwayland.
        using var tree = Processes("systemd", "kwin_wayland", "kwin_wayland_wr", "plasmashell", "Xwayland");
        var asked = new List<string>();

        CompositorSpecs? compositor = CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "wayland")), Tools(new() { ["kwin_wayland"] = "kwin 6.8.1\n" }, asked));

        Assert.Equal(new CompositorSpecs("Wayland", "KWin", "6.8.1"), compositor);
        Assert.Equal("KWin 6.8.1 (Wayland)", compositor!.Text);
        Assert.Equal(["kwin_wayland --version"], asked);
    }

    [Fact]
    public void Gnome_shell_is_mutter_inside_the_gnome_shell_on_either_session_type()
    {
        using var tree = Processes("gnome-shell", "gnome-session-b");

        CompositorSpecs? wayland = CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "wayland")), Tools(new() { ["gnome-shell"] = "GNOME Shell 47.1\n" }));
        CompositorSpecs? x11 = CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "x11")), Tools([]));

        Assert.Equal("Mutter 47.1 (GNOME Shell, Wayland)", wayland!.Text);
        Assert.Equal("Mutter (GNOME Shell, X11)", x11!.Text);
    }

    [Theory]
    [InlineData("sway", "sway version 1.10.1", "sway 1.10.1 (Wayland)")]
    [InlineData("Hyprland", "Hyprland 0.44.1 built from branch main", "Hyprland 0.44.1 (Wayland)")]
    [InlineData("labwc", "labwc 0.7.1 (+xwayland +nls)", "labwc 0.7.1 (Wayland)")]
    [InlineData("weston", "weston 13.0.0", "Weston 13.0.0 (Wayland)")]
    [InlineData("river", "river 0.3.4", "river 0.3.4 (Wayland)")]
    [InlineData("wayfire", "0.9.0", "Wayfire 0.9.0 (Wayland)")]
    [InlineData("cosmic-comp", null, "COSMIC (Wayland)")]
    public void Other_wayland_compositors_are_named_after_their_process(string comm, string? versionText, string expected)
    {
        using var tree = Processes("systemd", comm);

        CompositorSpecs? compositor = CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "wayland")), Tools(versionText is null ? [] : new() { [comm] = versionText + "\n" }));

        Assert.Equal(expected, compositor!.Text);
    }

    [Fact]
    public void River_is_asked_with_its_single_dash_flag()
    {
        using var tree = Processes("river");
        var asked = new List<string>();

        CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "wayland")), Tools([], asked));

        Assert.Equal(["river -version"], asked);
    }

    [Theory]
    [InlineData("kwin_x11", "kwin 5.27.11", "KWin 5.27.11 (X11)")]
    [InlineData("openbox", "Openbox 3.6.1", "Openbox 3.6.1 (X11)")]
    [InlineData("xfwm4", "\tThis is xfwm4 version 4.18.0 (revision 6dcf4a1) for Xfce 4.18", "Xfwm 4.18.0 (X11)")]
    [InlineData("i3", "i3 version 4.23 (2023-10-29) © 2009 Michael Stapelberg and contributors", "i3 4.23 (X11)")]
    [InlineData("dwm", null, "dwm (X11)")]
    public void On_x11_the_window_manager_is_named(string comm, string? versionText, string expected)
    {
        using var tree = Processes("Xorg", comm);

        CompositorSpecs? compositor = CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "x11")), Tools(versionText is null ? [] : new() { [comm] = versionText + "\n" }));

        Assert.Equal(expected, compositor!.Text);
    }

    [Fact]
    public void A_program_of_the_other_session_type_is_not_the_compositor()
    {
        // A stray kwin_x11 on a Wayland session (or a Wayland one on X11) is not what draws the windows.
        using var tree = Processes("kwin_x11", "sway");

        Assert.Equal("sway (Wayland)", CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "wayland")), Tools([]))!.Text);
        Assert.Equal("KWin (X11)", CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "x11")), Tools([]))!.Text);
    }

    [Fact]
    public void A_full_desktop_s_compositor_wins_over_a_window_manager_that_also_runs()
    {
        using var tree = Processes("openbox", "gnome-shell");

        Assert.Equal("Mutter (GNOME Shell, X11)", CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "x11")), Tools([]))!.Text);
    }

    [Fact]
    public void With_no_process_visible_the_session_bus_names_the_compositor()
    {
        using var tree = Processes("bwrap");
        var asked = new List<string>();
        var tools = Tools(new() { ["gdbus"] = "(true,)\n" }, asked);

        CompositorSpecs? compositor = CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "wayland")), tools);

        Assert.Equal("KWin (Wayland)", compositor!.Text);
        Assert.Contains(asked, call => call.StartsWith("gdbus call --session --dest org.freedesktop.DBus", StringComparison.Ordinal) && call.EndsWith("org.kde.KWin", StringComparison.Ordinal));
    }

    [Fact]
    public void The_bus_falls_back_to_mutter_when_kwin_has_no_owner()
    {
        using var tree = Processes();
        Func<string, IReadOnlyList<string>, string?> tools = (file, arguments) =>
            file == "gdbus" && arguments[^1] == "org.gnome.Mutter.DisplayConfig" ? "(true,)\n" : file == "gdbus" ? "(false,)\n" : null;

        CompositorSpecs? compositor = CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "x11")), tools);

        Assert.Equal("Mutter (GNOME Shell, X11)", compositor!.Text);
    }

    [Fact]
    public void A_session_with_nothing_that_names_it_is_only_the_session_type()
    {
        using var tree = Processes("bash");

        CompositorSpecs? compositor = CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "x11")), Tools([]));

        Assert.Equal(new CompositorSpecs("X11"), compositor);
        Assert.Equal("X11", compositor!.Text);
    }

    [Fact]
    public void Without_a_graphical_session_there_is_no_compositor()
    {
        using var tree = Processes("kwin_wayland");

        Assert.Null(CompositorDetector.Read(new SpecsRoot(tree.Root), Env(("XDG_SESSION_TYPE", "tty")), Tools([])));
    }

    // ---- parsers ----

    [Theory]
    [InlineData("kwin 6.8.1", "6.8.1")]
    [InlineData("kwin_wayland 6.8.1\n", "6.8.1")]
    [InlineData("GNOME Shell 47.1", "47.1")]
    [InlineData("GNOME Shell 47", "47")]
    [InlineData("\n\nsway version 1.10.1\nsecond line 9.9", "1.10.1")]
    [InlineData("labwc 0.7.1 (+xwayland +nls)", "0.7.1")]
    [InlineData("no digits here", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_version_is_the_first_number_of_the_first_line(string? output, string? version)
    {
        Assert.Equal(version, CompositorDetector.ParseVersion(output));
    }

    [Theory]
    [InlineData("(true,)", true)]
    [InlineData("(true,)\n", true)]
    [InlineData("(false,)", false)]
    [InlineData("Error: GDBus.Error:org.freedesktop.DBus.Error.ServiceUnknown", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_bus_reply_says_whether_a_name_has_an_owner(string? output, bool owned)
    {
        Assert.Equal(owned, CompositorDetector.ParseNameHasOwner(output));
    }

    [Theory]
    [InlineData("Wayland", "KWin", "6.8.1", null, "KWin 6.8.1 (Wayland)")]
    [InlineData("Wayland", "Mutter", "47.1", "GNOME Shell", "Mutter 47.1 (GNOME Shell, Wayland)")]
    [InlineData("X11", "Openbox", null, null, "Openbox (X11)")]
    [InlineData("X11", null, null, null, "X11")]
    public void The_text_reads_name_version_shell_and_session(string session, string? name, string? version, string? shell, string text)
    {
        Assert.Equal(text, new CompositorSpecs(session, name, version, shell).Text);
    }

    // ---- the reader ----

    [Fact]
    public void The_specs_carry_the_compositor_and_leave_it_null_without_a_session()
    {
        using var tree = Processes("kwin_wayland");

        PcSpecs withSession = new LinuxSpecsReader(tree.Root, (file, _) => file == "kwin_wayland" ? "kwin 6.8.1" : null, _ => null, Env(("XDG_SESSION_TYPE", "wayland"))).Read();
        PcSpecs without = new LinuxSpecsReader(tree.Root, (_, _) => null, _ => null, Env()).Read();

        Assert.Equal("KWin 6.8.1 (Wayland)", withSession.Compositor!.Text);
        Assert.Null(without.Compositor);
    }
}
