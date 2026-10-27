using System.Globalization;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.Core.Storage;
using Pennyworth.Insights.Classification;
using Pennyworth.KnowledgeBase;
using Pennyworth.Platform;

namespace Pennyworth.App.Tests;

/// <summary>A folder that holds one place per app becomes one row per app, named after the app.</summary>
public class PlacesTests
{
    private static readonly LocalizedText AppTitle = new("{0} (data)", "{0} (dados)");

    private static readonly KnownLocation[] Locations =
    [
        new("var-app", "/home/ana/.var/app", StorageCategory.Apps, new("Flatpak app data", "Dados dos apps Flatpak"), new("Flatpak.", "Flatpak."),
            new ChildPlaces(AppTitle, new("Data of {0}.", "Dados de {0}."))),
        new("opt", "/opt", StorageCategory.Apps, new("Optional programs", "Programas opcionais"), new("Optional.", "Opcionais."), new ChildPlaces()),
        new("games", "/home/ana/Games", StorageCategory.Games, new("Games folder", "Pasta de jogos"), new("Games.", "Jogos.")),
        new("firefox", "/opt/firefox", StorageCategory.Apps, new("Firefox browser", "Navegador Firefox"), new("Browser.", "Navegador.")),
    ];

    private sealed class Names(Dictionary<string, string> known) : IAppNames
    {
        public string? NameOf(string fullPath, CultureInfo culture) => known.GetValueOrDefault(fullPath);
    }

    private static KnowledgeBaseLocationInfo Info() => new(
        new RuleClassifier(Locations),
        new Names(new() { ["/home/ana/.var/app/org.mozilla.firefox"] = "Firefox" }));

    private static T WithCulture<T>(string culture, Func<T> action)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void A_folder_inside_a_split_location_is_named_after_its_app()
    {
        var info = Info();

        Assert.Equal(("Firefox (data)", "Data of Firefox."), WithCulture("en", () => info.Describe("/home/ana/.var/app/org.mozilla.firefox")));
        Assert.Equal(("Firefox (dados)", "Dados de Firefox."), WithCulture("pt-BR", () => info.Describe("/home/ana/.var/app/org.mozilla.firefox")));
    }

    [Fact]
    public void An_app_with_no_known_name_keeps_its_folder_name()
    {
        Assert.Equal(("com.example.Thing (data)", "Data of com.example.Thing."), WithCulture("en", () => Info().Describe("/home/ana/.var/app/com.example.Thing")));
    }

    [Fact]
    public void Children_with_no_template_are_named_after_themselves_and_have_no_description()
    {
        Assert.Equal(("vendor-tool", string.Empty), WithCulture("en", () => Info().Describe("/opt/vendor-tool")));
    }

    [Fact]
    public void A_folder_with_its_own_rule_keeps_it_and_other_folders_get_nothing()
    {
        var info = Info();

        Assert.Equal(("Firefox browser", "Browser."), WithCulture("en", () => info.Describe("/opt/firefox")));
        Assert.Null(info.Describe("/home/ana/Games/Hades"));
        Assert.Null(info.Describe("/srv/anything"));
    }

    [Fact]
    public void Only_locations_with_children_split()
    {
        var info = Info();

        Assert.True(info.SplitsIntoPlaces("/opt"));
        Assert.True(info.SplitsIntoPlaces("/home/ana/.var/app"));
        Assert.False(info.SplitsIntoPlaces("/opt/firefox"));
        Assert.False(info.SplitsIntoPlaces("/home/ana/Games"));
    }

    private static StorageTree Sample()
    {
        // /opt (Apps): two apps and a loose file; /opt/steam also holds a game (Games) that stays its own area.
        var b = new StorageTreeBuilder("/", StorageCategory.Other);
        int opt = b.AddDirectory(StorageTreeBuilder.RootIndex, "opt", StorageCategory.Apps);
        b.AddFile(opt, "readme", 1, 1, StorageCategory.Apps);
        int firefox = b.AddDirectory(opt, "firefox", StorageCategory.Apps);
        b.AddFile(firefox, "libxul.so", 300, 300, StorageCategory.Apps);
        int tool = b.AddDirectory(opt, "vendor-tool", StorageCategory.Apps);
        b.AddFile(tool, "tool", 200, 200, StorageCategory.Apps);
        int steam = b.AddDirectory(opt, "steam", StorageCategory.Apps);
        b.AddFile(steam, "steam.sh", 50, 50, StorageCategory.Apps);
        int game = b.AddDirectory(steam, "game", StorageCategory.Games);
        b.AddFile(game, "game.pak", 900, 900, StorageCategory.Games);
        b.AddDirectory(opt, "empty", StorageCategory.Apps);
        return b.Build();
    }

    [Fact]
    public void The_storage_page_lists_one_row_per_folder_with_its_own_bytes()
    {
        var summary = new StorageSummaryViewModel(new DriveSummary("/", "", 10_000, 5_000), Info(), _ => { });

        summary.ApplyResult(Sample());

        var apps = summary.Categories.Single(c => c.Category == StorageCategory.Apps).Areas;
        Assert.Equal(["firefox", "vendor-tool", "steam", "opt"], apps.Select(a => Path.GetFileName(a.Path)));
        Assert.Equal([300, 200, 50, 1], apps.Select(a => a.Bytes));
        Assert.Equal("Firefox browser", apps[0].Title);
        Assert.Equal("vendor-tool", apps[1].Title);
        Assert.Null(apps[1].Description);
        Assert.Equal("Optional programs", apps[3].Title); // what lies directly in /opt
        Assert.Equal(551, apps.Sum(a => a.Bytes));
        Assert.Equal("game", Path.GetFileName(summary.Categories.Single(c => c.Category == StorageCategory.Games).Areas.Single().Path));
    }

    [Fact]
    public void Without_a_split_location_the_areas_stay_as_they_were()
    {
        var summary = new StorageSummaryViewModel(new DriveSummary("/", "", 10_000, 5_000), new FakeLocations(), _ => { });

        summary.ApplyResult(Sample());

        Assert.Equal("opt", summary.Categories.Single(c => c.Category == StorageCategory.Apps).Areas.Single().Title);
    }
}
