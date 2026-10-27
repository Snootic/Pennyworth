using Pennyworth.Core.Wording;
using static Pennyworth.Assistant.Tests.Samples;

namespace Pennyworth.Assistant.Tests;

public class PersonaPromptTests
{
    private static ContextBundle Bundle(System.Globalization.CultureInfo culture)
    {
        var snapshot = Snapshot(diskUsedPercent: 92, apps: [App("chrome", 5 * Gb)]);
        var facts = FactCollector.Collect(snapshot, culture);
        return ContextBuilder.Build(new AssistantTopic(TopicKind.Memory), facts, DiagnosisEngine.FromKnowledgeBase().Evaluate(facts, culture), snapshot, culture, WindowsText.Instance);
    }

    [Theory]
    [InlineData("en-US", "emptying the Recycle Bin")]
    [InlineData("pt-BR", "esvaziar a Lixeira")]
    public void Windows_wording_keeps_the_original_advice(string culture, string expected)
    {
        Assert.Contains(expected, PersonaPrompt.Compose(Bundle(System.Globalization.CultureInfo.GetCultureInfo(culture))), StringComparison.Ordinal);
    }

    [Fact]
    public void Ends_with_the_context_exactly_as_built()
    {
        var bundle = Bundle(En);

        Assert.EndsWith(bundle.ToText(), PersonaPrompt.Compose(bundle));
    }

    [Fact]
    public void Lists_only_the_allowed_action_codes()
    {
        var bundle = Bundle(En);
        string prompt = PersonaPrompt.Compose(bundle);

        Assert.All(bundle.AllowedActions, a => Assert.Contains(a.Token, prompt));
        Assert.Equal(bundle.AllowedActions.Count, prompt.Split("[[action:").Length - 1);
        Assert.Contains("[[action:close-app:chrome]] — asks to close chrome", prompt);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void Never_lists_forbidden_topics_or_character_names(string culture)
    {
        string prompt = PersonaPrompt.Compose(Bundle(System.Globalization.CultureInfo.GetCultureInfo(culture))).ToLowerInvariant();

        foreach (string word in new[] { "registry", "registro", "defender", "bios", "overclock", "firewall", "alfred", "batman", "wayne", "gotham" })
        {
            Assert.DoesNotContain(word, prompt);
        }
    }

    [Fact]
    public void Speaks_the_users_language()
    {
        string pt = PersonaPrompt.Compose(Bundle(Pt));

        Assert.StartsWith("Você é o assistente do Pennyworth", pt);
        Assert.Contains("no máximo 120 palavras", pt);
        Assert.Contains("FATOS medidos pelo Pennyworth", pt);
        Assert.Contains("abre a tela de Memória e CPU", pt);
    }
}
