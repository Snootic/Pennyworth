using Pennyworth.Cleanup;
using Pennyworth.Core.Wording;
using Pennyworth.KnowledgeBase;
using static Pennyworth.Assistant.Tests.Samples;

namespace Pennyworth.Assistant.Tests;

public class ContextBuilderTests
{
    private static readonly DiagnosisEngine Engine = DiagnosisEngine.FromKnowledgeBase();

    private static ContextBundle Build(AssistantTopic topic, PcSnapshot snapshot, System.Globalization.CultureInfo? culture = null)
    {
        culture ??= En;
        var facts = FactCollector.Collect(snapshot, culture);
        return ContextBuilder.Build(topic, facts, Engine.Evaluate(facts, culture), snapshot, culture, WindowsText.Instance);
    }

    [Fact]
    public void Memory_questions_lead_with_memory_facts_and_findings()
    {
        var bundle = Build(new AssistantTopic(TopicKind.Memory), Snapshot(ramPercent: 90, diskUsedPercent: 95, apps: [App("chrome", 5 * Gb)]));

        Assert.Equal("Memory (RAM) installed", bundle.Facts[0].Label);
        Assert.StartsWith("Memory has been 90% used", bundle.Findings[0]);
        Assert.Contains(bundle.Findings, f => f.StartsWith("The Windows drive is 95% full", StringComparison.Ordinal)); // other topics still visible
        Assert.Equal(
            [new AssistantAction(ActionKind.Memory), new AssistantAction(ActionKind.Specs), new AssistantAction(ActionKind.CloseApp, "chrome")],
            bundle.AllowedActions);
    }

    [Fact]
    public void Protected_or_absent_apps_are_never_offered_for_closing()
    {
        var bundle = Build(new AssistantTopic(TopicKind.Memory), Snapshot(apps: [App("svchost", 9 * Gb, canClose: false, title: "Windows services"), App("chrome", Gb)]));

        Assert.DoesNotContain(bundle.AllowedActions, a => a.Kind == ActionKind.CloseApp);
        Assert.DoesNotContain(Build(AssistantTopic.Overview, Snapshot()).AllowedActions, a => a.Kind == ActionKind.CloseApp);
    }

    [Fact]
    public void Cleanup_items_bring_their_description_and_recycle_bin_contents()
    {
        var bin = new CleanupEstimate(Rule("recycle-bin", SafetyLevel.UsuallySafe, CleanupKind.RecycleBin), 2 * Gb, 2, true, [],
        [
            new CleanupFile(@"C:\$Recycle.Bin\S-1\$Rabc.exe", (long)(1.5 * Gb), DateTime.UtcNow, @"C:\Users\ana\Documents\setup.exe"),
            new CleanupFile(@"C:\$Recycle.Bin\S-1\$Rdef", Gb / 2, DateTime.UtcNow, @"C:\Users\ana\Music\old"),
        ]);

        var bundle = Build(new AssistantTopic(TopicKind.CleanupItem, "recycle-bin"), Snapshot(cleanup: [bin]));

        Assert.Equal(new AssistantAction(ActionKind.CleanupItem, "recycle-bin"), bundle.AllowedActions[0]);
        Assert.Contains(new ContextLine("If removed", "conseq"), bundle.Facts);
        Assert.Contains(new ContextLine("Item in the Recycle Bin", @"C:\Users\ana\Documents\setup.exe (1.50 GB)"), bundle.Facts);
        Assert.Contains(bundle.AllowedActions, a => a.Kind == ActionKind.Unlock);
    }

    [Fact]
    public void Spec_sections_map_to_their_topic()
    {
        Assert.Equal(TopicKind.Graphics, ContextBuilder.GroupOf(new AssistantTopic(TopicKind.SpecSection, "04")));
        Assert.Equal(TopicKind.Overview, ContextBuilder.GroupOf(new AssistantTopic(TopicKind.SpecSection, "07")));
        Assert.Equal("Graphics card", Build(new AssistantTopic(TopicKind.SpecSection, "04"), Snapshot()).Facts[0].Label);
    }

    [Fact]
    public void Stays_within_the_budget()
    {
        var huge = Snapshot(cleanup: [.. Enumerable.Range(0, 400).Select(i => new CleanupEstimate(Rule($"rule-{i}", SafetyLevel.Safe), Gb + i, 10, true))]);

        var bundle = Build(new AssistantTopic(TopicKind.CleanupItem, "rule-1"), huge);

        Assert.True(bundle.ToText().Length <= ContextBuilder.MaxCharacters);
        Assert.StartsWith("Cleanup: rule-1 files", bundle.Facts[0].Label);
    }

    [Fact]
    public void Writes_the_context_in_the_users_language()
    {
        string text = Build(AssistantTopic.Overview, Snapshot(diskUsedPercent: 92), Pt).ToText();

        Assert.StartsWith("FATOS medidos pelo Pennyworth neste PC:\n- Tipo de PC: Desktop\n".ReplaceLineEndings(), text);
        Assert.Contains("- Disco C: ocupado: 92%", text);
        Assert.Contains("CONCLUSÕES das regras do Pennyworth:", text);
        Assert.Contains("NÃO MEDIDO pelo Pennyworth (diga que não consegue ver): temperaturas", text);
    }

    [Fact]
    public void Windows_wording_keeps_the_original_not_measured_line()
    {
        string english = ContextBuilder.Build(AssistantTopic.Overview, [], [], PcSnapshot.Empty, En, WindowsText.Instance).ToText();
        string portuguese = ContextBuilder.Build(AssistantTopic.Overview, [], [], PcSnapshot.Empty, System.Globalization.CultureInfo.GetCultureInfo("pt-BR"), WindowsText.Instance).ToText();

        Assert.Contains("NOT MEASURED by Pennyworth (say you can't see these): temperatures, fans, internet speed, game or Windows requirements, prices.", english, StringComparison.Ordinal);
        Assert.Contains("NÃO MEDIDO pelo Pennyworth (diga que não consegue ver): temperaturas, ventoinhas, velocidade da internet, requisitos de jogos ou do Windows, preços.", portuguese, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_when_nothing_needs_attention()
    {
        var bundle = ContextBuilder.Build(AssistantTopic.Overview, [], [], PcSnapshot.Empty, En, WindowsText.Instance);

        Assert.EndsWith("FINDINGS: nothing needs attention right now." + Environment.NewLine, bundle.ToText());
        Assert.Equal([new AssistantAction(ActionKind.Cleanup), new AssistantAction(ActionKind.Memory), new AssistantAction(ActionKind.Specs)], bundle.AllowedActions);
    }
}
