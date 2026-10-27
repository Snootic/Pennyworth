using System.Text.RegularExpressions;

namespace Pennyworth.KnowledgeBase.Tests;

/// <summary>Both data sets load through the same checks; the Linux one has extra rules for what its text may say.</summary>
public partial class PlatformDataTests
{
    private static readonly string[] LinuxVariables = ["HOME", "TMPDIR", "XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME"];

    public static TheoryData<string> Platforms => new() { KnowledgeBaseLoader.Windows, KnowledgeBaseLoader.Linux };

    private static void AssertBilingual(LocalizedText text, string what)
    {
        Assert.False(string.IsNullOrWhiteSpace(text.En), $"{what}: English is empty");
        Assert.False(string.IsNullOrWhiteSpace(text.PtBr), $"{what}: Portuguese is empty");
    }

    private static IEnumerable<(string Where, string Text)> AllText(string platform)
    {
        foreach (KnownLocation l in KnowledgeBaseLoader.LoadEmbedded(platform).Locations)
        {
            yield return ($"location {l.Id} title", l.Title.En);
            yield return ($"location {l.Id} title", l.Title.PtBr);
            yield return ($"location {l.Id} description", l.Description.En);
            yield return ($"location {l.Id} description", l.Description.PtBr);
            foreach (LocalizedText t in new[] { l.Children?.Title, l.Children?.Description }.OfType<LocalizedText>())
            {
                yield return ($"location {l.Id} children", t.En);
                yield return ($"location {l.Id} children", t.PtBr);
            }
        }

        foreach (CleanupRule r in KnowledgeBaseLoader.LoadCleanupRules(platform).Rules)
        {
            foreach (LocalizedText t in new[] { r.Title, r.Description, r.Consequence })
            {
                yield return ($"cleanup {r.Id}", t.En);
                yield return ($"cleanup {r.Id}", t.PtBr);
            }
        }

        foreach (ProcessExplanation p in KnowledgeBaseLoader.LoadProcesses(platform).Processes)
        {
            foreach (LocalizedText? t in new[] { p.Title, p.Subtitle, p.Note })
            {
                if (t is not null)
                {
                    yield return ($"process {p.Id}", t.En);
                    yield return ($"process {p.Id}", t.PtBr);
                }
            }
        }

        foreach (DiagnosisRule d in KnowledgeBaseLoader.LoadDiagnosisRules(platform).Rules)
        {
            yield return ($"diagnosis {d.Id}", d.Finding.En);
            yield return ($"diagnosis {d.Id}", d.Finding.PtBr);
        }

        AdviceCatalog advice = KnowledgeBaseLoader.LoadBlockedAdvice(platform);
        yield return ("advice replacement", advice.Replacement.En);
        yield return ("advice replacement", advice.Replacement.PtBr);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Locations_are_complete_and_unique(string platform)
    {
        var locations = KnowledgeBaseLoader.LoadEmbedded(platform).Locations;

        Assert.NotEmpty(locations);
        Assert.Equal(locations.Count, locations.Select(l => l.Id).Distinct().Count());
        Assert.All(locations, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.Path), l.Id);
            AssertBilingual(l.Title, l.Id);
            AssertBilingual(l.Description, l.Id);
        });
    }

    [Fact]
    public void Windows_locations_never_split_into_places()
    {
        Assert.All(KnowledgeBaseLoader.LoadEmbedded(KnowledgeBaseLoader.Windows).Locations, l => Assert.Null(l.Children));
    }

    [Fact]
    public void Linux_child_templates_are_bilingual_and_take_only_the_app_name()
    {
        var withChildren = KnowledgeBaseLoader.LoadEmbedded(KnowledgeBaseLoader.Linux).Locations.Where(l => l.Children is not null).ToList();

        Assert.Contains(withChildren, l => l.Id == "flatpak-app-data-apps");
        Assert.All(withChildren, l =>
        {
            foreach (LocalizedText? t in new[] { l.Children!.Title, l.Children.Description })
            {
                if (t is null)
                {
                    continue;
                }

                AssertBilingual(t, l.Id);
                // string.Format with one argument must not throw: no other placeholder, no stray brace.
                Assert.Equal(t.En.Replace("{0}", "x"), string.Format(t.En, "x"));
                Assert.Equal(t.PtBr.Replace("{0}", "x"), string.Format(t.PtBr, "x"));
            }
        });
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Cleanup_rules_are_complete_and_unique(string platform)
    {
        var rules = KnowledgeBaseLoader.LoadCleanupRules(platform).Rules;

        Assert.NotEmpty(rules);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.Single(rules, r => r.Kind == CleanupKind.RecycleBin);
        Assert.All(rules, r =>
        {
            AssertBilingual(r.Title, r.Id);
            AssertBilingual(r.Description, r.Id);
            AssertBilingual(r.Consequence, r.Id);
            Assert.True(r.Kind == CleanupKind.RecycleBin || r.Paths.Count > 0, $"{r.Id} has no paths");
            Assert.True(r.Kind != CleanupKind.RecycleBin || r.Paths.Count == 0, $"{r.Id} is the trash and names folders");
        });
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Processes_are_complete_and_unique(string platform)
    {
        var processes = KnowledgeBaseLoader.LoadProcesses(platform).Processes;

        Assert.Equal(processes.Count, processes.Select(p => p.Id).Distinct().Count());
        var names = processes.SelectMany(p => p.Names).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(processes, p =>
        {
            Assert.NotEmpty(p.Names);
            AssertBilingual(p.Title, p.Id);
            AssertBilingual(p.Subtitle, p.Id);
            Assert.False(p.IsProtected && p.CanClose, $"{p.Id} is protected but closable");
        });
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Diagnosis_rules_are_complete_and_unique(string platform)
    {
        var rules = KnowledgeBaseLoader.LoadDiagnosisRules(platform).Rules;

        Assert.NotEmpty(rules);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.All(rules, r =>
        {
            Assert.NotEmpty(r.When);
            Assert.All(r.When, c => Assert.Contains(c.Op, new[] { "gt", "gte", "lt", "lte", "eq", "exists" }));
            AssertBilingual(r.Finding, r.Id);
        });
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Blocked_advice_patterns_compile_and_are_unique(string platform)
    {
        AdviceCatalog advice = KnowledgeBaseLoader.LoadBlockedAdvice(platform);

        Assert.NotEmpty(advice.Topics);
        Assert.NotEmpty(advice.Negations);
        AssertBilingual(advice.Replacement, "replacement");
        Assert.Equal(advice.Topics.Count, advice.Topics.Select(t => t.Id).Distinct().Count());
        Assert.All(advice.Topics, t =>
        {
            Assert.NotEmpty(t.Patterns);
            Assert.All(t.Patterns, p => _ = new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        });
    }

    [Fact]
    public void The_data_sets_are_different_files()
    {
        Assert.NotEqual(
            KnowledgeBaseLoader.LoadEmbedded(KnowledgeBaseLoader.Windows).Locations.Select(l => l.Id),
            KnowledgeBaseLoader.LoadEmbedded(KnowledgeBaseLoader.Linux).Locations.Select(l => l.Id));
        Assert.Equal(KnowledgeBaseLoader.LoadEmbedded().Locations.Count, KnowledgeBaseLoader.LoadEmbedded(KnowledgeBaseLoader.Windows).Locations.Count);
        Assert.Equal(KnowledgeBaseLoader.LoadCleanupRules().Rules.Count, KnowledgeBaseLoader.LoadCleanupRules(KnowledgeBaseLoader.Windows).Rules.Count);
    }

    [Theory]
    [InlineData("macos")]
    [InlineData("Linux")]
    [InlineData("")]
    public void An_unknown_platform_is_an_error(string platform)
    {
        Assert.Throws<ArgumentException>(() => KnowledgeBaseLoader.LoadEmbedded(platform));
        Assert.Throws<ArgumentException>(() => KnowledgeBaseLoader.LoadProcesses(platform));
        Assert.Throws<ArgumentException>(() => KnowledgeBaseLoader.LoadCleanupRules(platform));
        Assert.Throws<ArgumentException>(() => KnowledgeBaseLoader.LoadDiagnosisRules(platform));
        Assert.Throws<ArgumentException>(() => KnowledgeBaseLoader.LoadBlockedAdvice(platform));
    }

    [Fact]
    public void Linux_text_never_mentions_windows_things()
    {
        var banned = new Regex(@"Windows|Explorer|Recycle Bin|Defender|(?i:\.exe\b|registry)", RegexOptions.CultureInvariant);

        Assert.All(AllText(KnowledgeBaseLoader.Linux), t => Assert.False(banned.IsMatch(t.Text), $"{t.Where}: {t.Text}"));
        Assert.All(KnowledgeBaseLoader.LoadProcesses(KnowledgeBaseLoader.Linux).Processes.SelectMany(p => p.Names), n => Assert.False(n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), n));
    }

    [Fact]
    public void Linux_paths_use_only_variables_linux_resolves()
    {
        var paths = KnowledgeBaseLoader.LoadEmbedded(KnowledgeBaseLoader.Linux).Locations.Select(l => (Where: l.Id, l.Path))
            .Concat(KnowledgeBaseLoader.LoadCleanupRules(KnowledgeBaseLoader.Linux).Rules.SelectMany(r => r.Paths.Select(p => (Where: r.Id, Path: p))))
            .ToList();

        Assert.All(paths, p =>
        {
            Assert.DoesNotContain('\\', p.Path);
            Assert.DoesNotContain("?:", p.Path);
            Assert.True(p.Path.StartsWith('/') || p.Path.StartsWith('%') || p.Path.StartsWith("**/"), $"{p.Where}: {p.Path} is neither absolute nor starts with a variable");
            Assert.All(VariablePattern().Matches(p.Path).Select(m => m.Groups[1].Value), name => Assert.Contains(name, LinuxVariables));
            Assert.Equal(0, p.Path.Count(c => c == '%') % 2);
        });
    }

    [Fact]
    public void Linux_cleanup_rules_follow_the_safety_promises()
    {
        var rules = KnowledgeBaseLoader.LoadCleanupRules(KnowledgeBaseLoader.Linux).Rules;

        Assert.All(rules, r =>
        {
            // Whatever lives outside the home folder needs root and must say so: the app only asks for the administrator
            // password for a rule that declares it, and never lets an unprivileged rule reach system folders.
            bool outsideHome = r.Paths.Any(p => p.StartsWith('/'));
            Assert.True(!outsideHome || r.RequiresAdmin, $"{r.Id} cleans system folders without requiring admin");
            Assert.True(r.Safety != SafetyLevel.Safe || !r.RequiresAdmin, $"{r.Id} is marked Safe but needs admin");

            // Never a whole folder that can hold other programs' live files or the user's data.
            Assert.All(r.Paths, p =>
            {
                Assert.DoesNotContain(p.TrimEnd('/'), new[] { "%HOME%", "%TMPDIR%", "%XDG_CACHE_HOME%", "%XDG_CONFIG_HOME%", "%XDG_DATA_HOME%", "%XDG_STATE_HOME%", "/tmp", "/var/tmp", "/var", "/var/log", "/var/cache", "/home", "/" });
                Assert.False(p.Contains("Documents") || p.Contains("Downloads") || p.Contains("Pictures") || p.Contains("Trash"), $"{r.Id}: {p}");
                Assert.False(p.Contains("compatdata") || p.Contains("steamapps/common"), $"{r.Id}: {p}");
            });
        });
    }

    [GeneratedRegex("%([^%]+)%")]
    private static partial Regex VariablePattern();
}
