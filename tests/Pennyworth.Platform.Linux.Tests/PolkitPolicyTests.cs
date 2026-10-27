using System.Text.Json;
using System.Xml.Linq;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>
/// Keeps the polkit policy shipped in the tarball, the helper protocol compiled into both programs and the knowledge
/// base's admin-only cleanup rules in step: a drift between them would either make a button fail at the prompt or
/// let the helper delete somewhere the rule never named.
/// </summary>
public class PolkitPolicyTests
{
    private const string ExecPath = "org.freedesktop.policykit.exec.path";
    private const string ExecArgv1 = "org.freedesktop.policykit.exec.argv1";
    private static readonly XNamespace XmlNs = XNamespace.Xml;

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pennyworth.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Pennyworth.slnx was not found above " + AppContext.BaseDirectory);
    }

    private static List<XElement> Actions()
    {
        XDocument document = XDocument.Load(Path.Combine(RepoRoot, "tools", "package", "linux", "app.pennyworth.policy"));
        Assert.Equal("policyconfig", document.Root!.Name.LocalName);
        return [.. document.Root.Elements("action")];
    }

    private static string Annotation(XElement action, string key) =>
        action.Elements("annotate").Single(a => (string?)a.Attribute("key") == key).Value;

    [Fact]
    public void Has_exactly_one_action_per_helper_subcommand_with_the_protocols_ids()
    {
        string[] ids = [.. Actions().Select(a => (string)a.Attribute("id")!)];

        Assert.Equal(HelperProtocol.Subcommands.Select(HelperProtocol.ActionId).OrderBy(x => x, StringComparer.Ordinal), ids.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_action_matches_the_installed_helper_and_exactly_its_own_subcommand()
    {
        foreach (string subcommand in HelperProtocol.Subcommands)
        {
            XElement action = Actions().Single(a => (string?)a.Attribute("id") == HelperProtocol.ActionId(subcommand));

            Assert.Equal(HelperProtocol.InstalledPath, Annotation(action, ExecPath));
            Assert.Equal(subcommand, Annotation(action, ExecArgv1));
            Assert.DoesNotContain(action.Elements("annotate"), a => ((string?)a.Attribute("key"))?.EndsWith("allow_gui", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public void Only_an_active_session_may_authenticate_and_cleanup_asks_every_time()
    {
        foreach (string subcommand in HelperProtocol.Subcommands)
        {
            XElement defaults = Actions().Single(a => (string?)a.Attribute("id") == HelperProtocol.ActionId(subcommand)).Element("defaults")!;
            string expected = subcommand == HelperProtocol.CleanSystem ? "auth_admin" : "auth_admin_keep";

            Assert.Equal("no", defaults.Element("allow_any")!.Value);
            Assert.Equal("no", defaults.Element("allow_inactive")!.Value);
            Assert.Equal(expected, defaults.Element("allow_active")!.Value);
        }
    }

    [Fact]
    public void Every_action_is_described_in_english_and_brazilian_portuguese()
    {
        foreach (XElement action in Actions())
        {
            foreach (string element in new[] { "description", "message" })
            {
                XElement[] texts = [.. action.Elements(element)];

                Assert.Contains(texts, t => t.Attribute(XmlNs + "lang") is null && t.Value.Trim().Length > 0);
                Assert.Contains(texts, t => (string?)t.Attribute(XmlNs + "lang") == "pt_BR" && t.Value.Trim().Length > 0);
                Assert.Equal(2, texts.Length);
            }
        }
    }

    [Fact]
    public void The_installed_path_is_named_once_per_action_and_nowhere_else()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot, "tools", "package", "linux", "app.pennyworth.policy"));
        string annotated = $">{HelperProtocol.InstalledPath}<";

        Assert.Equal(HelperProtocol.Subcommands.Count, text.Split(annotated).Length - 1);
        Assert.Equal(HelperProtocol.Subcommands.Count, text.Split(HelperProtocol.InstalledPath).Length - 1);
    }

    private static Dictionary<string, string[]> AdminRules()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "src", "Pennyworth.KnowledgeBase", "Data", "linux", "cleanup.json")));
        var rules = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (JsonElement rule in json.RootElement.GetProperty("rules").EnumerateArray())
        {
            if (rule.TryGetProperty("requiresAdmin", out JsonElement admin) && admin.ValueKind == JsonValueKind.True)
            {
                rules.Add(rule.GetProperty("id").GetString()!, [.. rule.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!)]);
            }
        }

        return rules;
    }

    [Fact]
    public void The_helpers_cleanup_allowlist_is_exactly_the_admin_rules_of_the_knowledge_base()
    {
        Dictionary<string, string[]> admin = AdminRules();

        Assert.NotEmpty(admin);
        Assert.Equal(admin.Keys.OrderBy(x => x, StringComparer.Ordinal), SystemCleanupRules.Ids.OrderBy(x => x, StringComparer.Ordinal));
        foreach ((string id, string[] paths) in admin)
        {
            IReadOnlyList<string>? folders = SystemCleanupRules.FoldersOf(id);

            Assert.NotNull(folders);
            Assert.Equal(paths.OrderBy(x => x, StringComparer.Ordinal), folders.OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void No_admin_rule_is_missing_from_the_allowlist_and_the_allowlist_folders_are_absolute()
    {
        foreach (string id in AdminRules().Keys)
        {
            Assert.NotNull(SystemCleanupRules.FoldersOf(id));
        }

        foreach (string id in SystemCleanupRules.Ids)
        {
            Assert.All(SystemCleanupRules.FoldersOf(id)!, folder => Assert.StartsWith("/", folder, StringComparison.Ordinal));
        }
    }
}
