using System.Globalization;
using Pennyworth.Platform.Linux;

namespace Pennyworth.Platform.Linux.Tests;

public class FlatpakAppNamesTests
{
    // Captured from ~/.local/share/flatpak/exports/share/applications/com.usebottles.bottles.desktop (shortened).
    private const string Bottles = """
        [Desktop Entry]
        Name[it]=Bottles
        Name[es]=Botellas
        Name[pt_BR]=Garrafas
        Name[pt]=Bottles
        Name=Bottles
        Comment=Run Windows Software
        Exec=bottles %u
        Type=Application

        [Desktop Action new-bottle]
        Name=New Bottle
        Name[pt_BR]=Nova garrafa
        """;

    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    [Fact]
    public void The_name_follows_the_language_then_falls_back_to_the_plain_name()
    {
        Assert.Equal("Garrafas", DesktopEntry.Name(Bottles, PtBr));
        Assert.Equal("Bottles", DesktopEntry.Name(Bottles, En));
        Assert.Equal("Botellas", DesktopEntry.Name(Bottles, CultureInfo.GetCultureInfo("es-MX")));
        Assert.Equal("Bottles", DesktopEntry.Name(Bottles, CultureInfo.GetCultureInfo("ja")));
    }

    [Fact]
    public void A_regional_name_beats_the_language_name_and_the_language_name_beats_nothing()
    {
        const string entry = "[Desktop Entry]\nName=App\nName[pt]=Aplicativo\nName[pt_BR]=Aplicação\n";

        Assert.Equal("Aplicação", DesktopEntry.Name(entry, PtBr));
        Assert.Equal("Aplicativo", DesktopEntry.Name(entry, CultureInfo.GetCultureInfo("pt-PT")));
    }

    [Fact]
    public void Names_of_actions_comments_and_blank_values_are_ignored()
    {
        Assert.Null(DesktopEntry.Name("[Desktop Action x]\nName=Only an action\n", En));
        Assert.Null(DesktopEntry.Name("# Name=comment\n[Desktop Entry]\nName=\n", En));
        Assert.Null(DesktopEntry.Name("", En));
        Assert.Equal("Spaced", DesktopEntry.Name("[Desktop Entry]\r\nName = Spaced\r\n", En));
    }

    [Theory]
    [InlineData("/home/ana/.var/app/com.google.Chrome", "com.google.Chrome")]
    [InlineData("/home/ana/.local/share/flatpak/app/org.mozilla.firefox", "org.mozilla.firefox")]
    [InlineData("/var/lib/flatpak/app/net.davidotek.pupgui2", "net.davidotek.pupgui2")]
    [InlineData("/home/ana/.var/app/com.google.Chrome/config", null)]
    [InlineData("/home/ana/.var/app", null)]
    [InlineData("/var/lib/flatpak/runtime/org.kde.Platform", null)]
    [InlineData("/home/ana/.var/app/.hidden", null)]
    [InlineData("/home/ana/other/app/com.example.Thing", null)]
    [InlineData("/opt/app", null)]
    public void Only_the_folder_that_carries_the_id_is_an_app(string path, string? id)
    {
        Assert.Equal(id, FlatpakAppNames.AppId(path));
    }

    private static FlatpakAppNames Names(Dictionary<string, string> files, List<string>? reads = null) => new(
        "/home/ana/.local/share",
        readText: path =>
        {
            reads?.Add(path);
            return files.GetValueOrDefault(path);
        });

    [Fact]
    public void Reads_the_entry_of_the_user_installation_then_the_system_one()
    {
        var names = Names(new()
        {
            ["/home/ana/.local/share/flatpak/exports/share/applications/com.usebottles.bottles.desktop"] = Bottles,
            ["/var/lib/flatpak/exports/share/applications/org.kde.krita.desktop"] = "[Desktop Entry]\nName=Krita\n",
        });

        Assert.Equal("Garrafas", names.NameOf("/home/ana/.var/app/com.usebottles.bottles", PtBr));
        Assert.Equal("Bottles", names.NameOf("/home/ana/.local/share/flatpak/app/com.usebottles.bottles", En));
        Assert.Equal("Krita", names.NameOf("/var/lib/flatpak/app/org.kde.krita", En));
        Assert.Equal("Krita", names.NameOf("/home/ana/.var/app/org.kde.krita", En));
    }

    [Fact]
    public void An_app_without_an_entry_and_a_folder_that_is_no_app_have_no_name_and_the_lookup_is_cached()
    {
        var reads = new List<string>();
        var names = Names(new() { ["/var/lib/flatpak/exports/share/applications/org.kde.krita.desktop"] = "[Desktop Entry]\nName=Krita\n" }, reads);

        Assert.Null(names.NameOf("/home/ana/.var/app/com.gone.App", En));
        Assert.Null(names.NameOf("/home/ana/.cache/thing", En));
        Assert.Equal(2, reads.Count); // Both installations were asked once, for the one app.

        Assert.Equal("Krita", names.NameOf("/var/lib/flatpak/app/org.kde.krita", En));
        Assert.Equal("Krita", names.NameOf("/var/lib/flatpak/app/org.kde.krita", En));
        Assert.Equal(4, reads.Count);
    }
}
