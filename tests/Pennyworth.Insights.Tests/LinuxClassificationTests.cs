using Pennyworth.Core.Storage;
using Pennyworth.Insights.Classification;
using Pennyworth.KnowledgeBase;

namespace Pennyworth.Insights.Tests;

/// <summary>The real classifier over the Linux <c>locations.json</c>: rooted patterns match only from the root.</summary>
public class LinuxClassificationTests
{
    private static readonly Dictionary<string, string> Variables = new(StringComparer.Ordinal)
    {
        ["HOME"] = "/home/ana",
        ["TMPDIR"] = "/tmp",
        ["XDG_CACHE_HOME"] = "/home/ana/.cache",
        ["XDG_CONFIG_HOME"] = "/home/ana/.config",
        ["XDG_DATA_HOME"] = "/home/ana/.local/share",
        ["XDG_STATE_HOME"] = "/home/ana/.local/state",
    };

    private static RuleClassifier Classifier() =>
        new(KnowledgeBaseLoader.LoadEmbedded(KnowledgeBaseLoader.Linux).Locations, name => Variables.GetValueOrDefault(name));

    [Fact]
    public void Every_location_parses_and_none_is_dropped_for_a_missing_variable()
    {
        var locations = KnowledgeBaseLoader.LoadEmbedded(KnowledgeBaseLoader.Linux).Locations;
        var classifier = Classifier();

        // A pattern that does not parse throws in the constructor; a dropped one cannot be found by its own path.
        Assert.All(locations.Where(l => !l.Path.Contains('*')), l =>
        {
            string expanded = RuleClassifier.ExpandVariables(l.Path, name => Variables.GetValueOrDefault(name))!;
            Assert.NotNull(classifier.FindLocation(expanded));
            Assert.Equal(l.Category, classifier.FindLocation(expanded)!.Category);
        });
    }

    [Theory]
    [InlineData("/etc", StorageCategory.Windows)]
    [InlineData("/boot", StorageCategory.Windows)]
    [InlineData("/var/log", StorageCategory.Windows)]
    [InlineData("/var/lib", StorageCategory.Windows)]
    [InlineData("/usr/lib", StorageCategory.Windows)]
    [InlineData("/usr/lib/modules", StorageCategory.Windows)]
    [InlineData("/usr", StorageCategory.Apps)]
    [InlineData("/opt", StorageCategory.Apps)]
    [InlineData("/usr/local", StorageCategory.Apps)]
    [InlineData("/var/lib/flatpak", StorageCategory.Apps)]
    [InlineData("/var/lib/docker", StorageCategory.Apps)]
    [InlineData("/home/ana/.local/share/flatpak", StorageCategory.Apps)]
    [InlineData("/home/ana/.var", StorageCategory.Apps)]
    [InlineData("/home/ana/.cache", StorageCategory.Apps)]
    [InlineData("/home/ana/.config", StorageCategory.Apps)]
    [InlineData("/home/ana/.local/share", StorageCategory.Apps)]
    [InlineData("/home/ana", StorageCategory.YourFiles)]
    [InlineData("/home/bob", StorageCategory.YourFiles)]
    [InlineData("/home/ana/Documents", StorageCategory.YourFiles)]
    [InlineData("/home/ana/Downloads", StorageCategory.YourFiles)]
    [InlineData("/home/ana/.local/share/Trash", StorageCategory.YourFiles)]
    [InlineData("/home/ana/Games", StorageCategory.Games)]
    [InlineData("/home/ana/.local/share/Steam/steamapps", StorageCategory.Games)]
    [InlineData("/mnt/games/SteamLibrary/steamapps", StorageCategory.Games)]
    [InlineData("/home/ana/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps", StorageCategory.Games)]
    [InlineData("/var/lib/flatpak/runtime", StorageCategory.Apps)]
    [InlineData("/home/ana/.var/app/net.lutris.Lutris", StorageCategory.Games)]
    [InlineData("/home/ana/.local/share/anime-game-launcher", StorageCategory.Games)]
    [InlineData("/home/ana/.local/share/Steam", StorageCategory.Apps)]
    [InlineData("/home/ana/.local/share/Steam/steamapps/common", StorageCategory.Games)]
    [InlineData("/home/linuxbrew", StorageCategory.Apps)]
    [InlineData("/swapfile", StorageCategory.HiddenSystem)]
    [InlineData("/.snapshots", StorageCategory.HiddenSystem)]
    [InlineData("/var/cache", StorageCategory.HiddenSystem)]
    public void Classifies_known_locations(string path, StorageCategory expected)
    {
        Assert.Equal(expected, Classifier().Classify(path, StorageCategory.Other));
    }

    [Theory]
    [InlineData("var/cache")]
    [InlineData("etc")]
    [InlineData("usr")]
    [InlineData("opt")]
    [InlineData("home/ana/Games")]
    [InlineData("home/ana/.cache")]
    [InlineData("swapfile")]
    public void Rooted_patterns_never_match_a_relative_path(string path)
    {
        Assert.Equal(StorageCategory.Other, Classifier().Classify(path, StorageCategory.Other));
    }

    [Theory]
    [InlineData("/home/ana/.cache/mozilla", StorageCategory.Apps)]
    [InlineData("/home/ana/Games/x", StorageCategory.Games)]
    [InlineData("/home/ana/Documents/taxes.pdf", StorageCategory.YourFiles)]
    [InlineData("/usr/share/doc", StorageCategory.Apps)]
    [InlineData("/etc/ssh", StorageCategory.Windows)]
    public void Unknown_paths_keep_the_inherited_category(string path, StorageCategory inherited)
    {
        Assert.Equal(inherited, Classifier().Classify(path, inherited));
    }

    [Theory]
    [InlineData("/tmp")]
    [InlineData("/mnt/data")]
    [InlineData("/srv")]
    [InlineData("/home")]
    [InlineData("/home/ana/Projects")]
    public void Places_without_a_rule_stay_other(string path)
    {
        Assert.Equal(StorageCategory.Other, Classifier().Classify(path, StorageCategory.Other));
    }

    [Fact]
    public void Windows_drive_patterns_never_match_linux_paths()
    {
        var windows = new RuleClassifier(KnowledgeBaseLoader.LoadEmbedded().Locations, _ => null);

        Assert.Equal(StorageCategory.Other, windows.Classify("/usr", StorageCategory.Other));
        Assert.Equal(StorageCategory.Other, windows.Classify("/home/ana", StorageCategory.Other));
        Assert.Equal(StorageCategory.Games, windows.Classify("/home/ana/.local/share/Steam/steamapps", StorageCategory.Other));
    }

    [Fact]
    public void Rules_with_an_unresolved_variable_are_dropped()
    {
        var classifier = new RuleClassifier(KnowledgeBaseLoader.LoadEmbedded(KnowledgeBaseLoader.Linux).Locations, _ => null);

        // Only the absolute rules remain: /etc, /home/* and any steamapps, not the XDG folders.
        Assert.Equal(StorageCategory.Windows, classifier.Classify("/etc", StorageCategory.Other));
        Assert.Equal(StorageCategory.YourFiles, classifier.Classify("/home/ana", StorageCategory.Other));
        Assert.Equal(StorageCategory.Other, classifier.Classify("/home/ana/.cache", StorageCategory.Other));
    }

    [Fact]
    public void Homebrew_is_not_somebodys_user_folder()
    {
        var classifier = Classifier();

        Assert.Equal("homebrew", classifier.FindLocation("/home/linuxbrew")!.Id);
        Assert.Equal("home-folders", classifier.FindLocation("/home/bob")!.Id);
    }

    [Theory]
    [InlineData("/home/ana/.var/app", true)]
    [InlineData("/home/ana/.local/share/flatpak/app", true)]
    [InlineData("/var/lib/flatpak/app", true)]
    [InlineData("/opt", true)]
    [InlineData("/home/ana/.cache", true)]
    [InlineData("/home/ana/.local/share/Steam/steamapps/common", true)]
    [InlineData("/usr", false)]
    [InlineData("/home/ana/.var/app/org.mozilla.firefox", false)]
    public void Only_folders_that_hold_one_place_per_app_are_split(string path, bool split)
    {
        Assert.Equal(split, Classifier().FindLocation(path)?.Children is not null);
    }
}
