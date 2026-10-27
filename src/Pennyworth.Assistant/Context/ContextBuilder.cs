using System.Globalization;
using System.Text;
using Pennyworth.Cleanup;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Wording;
using Pennyworth.Monitoring;

namespace Pennyworth.Assistant;

public sealed record ContextLine(string Label, string Value);

/// <summary>
/// What the model is told about this PC for one question. <see cref="ToText"/> is exactly what
/// goes into the prompt, and what the send preview shows before a cloud request.
/// </summary>
public sealed record ContextBundle(
    AssistantTopic Topic,
    CultureInfo Culture,
    IReadOnlyList<ContextLine> Facts,
    IReadOnlyList<string> Findings,
    IReadOnlyList<AssistantAction> AllowedActions,
    IPlatformText Wording)
{
    public bool IsPortuguese => Culture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(IsPortuguese ? "FATOS medidos pelo Pennyworth neste PC:" : "FACTS measured by Pennyworth on this PC:");
        foreach (ContextLine line in Facts)
        {
            sb.Append("- ").Append(line.Label).Append(": ").AppendLine(line.Value);
        }

        // Phase 1 evaluation: without this line small models "answered" these from general knowledge.
        sb.AppendLine();
        sb.AppendLine(IsPortuguese
            ? $"NÃO MEDIDO pelo Pennyworth (diga que não consegue ver): temperaturas, ventoinhas, velocidade da internet, {Wording.Text(PlatformText.RequirementsNotMeasured, true)}, preços."
            : $"NOT MEASURED by Pennyworth (say you can't see these): temperatures, fans, internet speed, {Wording.Text(PlatformText.RequirementsNotMeasured, false)}, prices.");

        if (Findings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(IsPortuguese ? "CONCLUSÕES das regras do Pennyworth:" : "FINDINGS from Pennyworth's rules:");
            foreach (string finding in Findings)
            {
                sb.Append("- ").AppendLine(finding);
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(IsPortuguese ? "CONCLUSÕES: nada precisa de atenção agora." : "FINDINGS: nothing needs attention right now.");
        }

        return sb.ToString();
    }
}

/// <summary>Picks, orders and trims the facts and findings for one topic.</summary>
public static class ContextBuilder
{
    /// <summary>About 1,500 tokens; small local models must keep all of it in view.</summary>
    public const int MaxCharacters = 6_000;

    private const int MaxActions = 5;
    private const int MaxTrashItems = 5;

    /// <summary>The facts every answer may need, in this order, after the topic's own facts.</summary>
    private static readonly string[] KeyFacts =
    [
        "pc.kind", "os.name", "disk.system.used_percent", "disk.system.free", "disk.system.kind",
        "ram.total", "ram.used_percent", "ram.sustained_percent", "cpu.name", "cpu.percent",
        "gpu.name", "gpu.memory", "battery.percent", "cleanup.safe_bytes",
    ];

    public static ContextBundle Build(
        AssistantTopic topic,
        IReadOnlyList<Fact> facts,
        IReadOnlyList<Finding> findings,
        PcSnapshot snapshot,
        CultureInfo culture,
        IPlatformText wording)
    {
        TopicKind group = GroupOf(topic);
        var lines = new List<ContextLine>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        void AddFact(Fact fact)
        {
            if (used.Add(fact.Id))
            {
                lines.Add(new ContextLine(fact.Label, fact.Format(culture)));
            }
        }

        if (topic.Kind == TopicKind.CleanupItem && topic.Key is { } ruleId)
        {
            foreach (Fact fact in facts.Where(f => f.Id.StartsWith($"cleanup.item.{ruleId}.", StringComparison.Ordinal)))
            {
                AddFact(fact);
            }

            lines.AddRange(CleanupItemLines(snapshot, ruleId, culture, wording));
        }
        else if (group != TopicKind.Overview)
        {
            foreach (Fact fact in facts.Where(f => f.Group == group))
            {
                AddFact(fact);
            }
        }

        var byId = facts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        foreach (string id in KeyFacts)
        {
            if (byId.TryGetValue(id, out Fact? fact))
            {
                AddFact(fact);
            }
        }

        var ordered = findings
            .OrderByDescending(f => group != TopicKind.Overview && f.Group == group)
            .ThenByDescending(f => f.Severity)
            .ToList();

        var bundle = new ContextBundle(topic, culture, lines, [.. ordered.Select(f => f.Text)], AllowedActions(topic, group, ordered, snapshot), wording);
        return Trim(bundle);
    }

    /// <summary>The topic a question is about, as one of the fact groups.</summary>
    public static TopicKind GroupOf(AssistantTopic topic) => topic.Kind switch
    {
        TopicKind.CleanupItem => TopicKind.Storage,
        TopicKind.SpecSection => topic.Key switch
        {
            "02" => TopicKind.Processor,
            "03" => TopicKind.Memory,
            "04" => TopicKind.Graphics,
            "05" => TopicKind.Storage,
            "08" => TopicKind.Power,
            _ => TopicKind.Overview,
        },
        _ => topic.Kind,
    };

    private static List<ContextLine> CleanupItemLines(PcSnapshot snapshot, string ruleId, CultureInfo culture, IPlatformText wording)
    {
        var lines = new List<ContextLine>();
        bool pt = culture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        if (snapshot.Cleanup.FirstOrDefault(e => e.Rule.Id == ruleId) is not { } estimate)
        {
            return lines;
        }

        lines.Add(new ContextLine(pt ? "O que é" : "What it is", estimate.Rule.Description.Get(culture)));
        lines.Add(new ContextLine(pt ? "Se for removido" : "If removed", estimate.Rule.Consequence.Get(culture)));
        foreach (CleanupFile item in (estimate.Files ?? []).Where(f => f.OriginalPath is not null).Take(MaxTrashItems))
        {
            // The full path in one piece, so the anonymiser hides personal file names too (phase 6 privacy review).
            lines.Add(new ContextLine(pt ? $"Item na {wording.Text(PlatformText.Trash, true)}" : $"Item in the {wording.Text(PlatformText.Trash, false)}", $"{item.OriginalPath} ({ByteSize.Format(item.Size, culture)})"));
        }

        return lines;
    }

    private static List<AssistantAction> AllowedActions(AssistantTopic topic, TopicKind group, List<Finding> findings, PcSnapshot snapshot)
    {
        var actions = new List<AssistantAction>();
        void Add(AssistantAction action)
        {
            if (!actions.Contains(action))
            {
                actions.Add(action);
            }
        }

        if (topic.Kind == TopicKind.CleanupItem && topic.Key is { } ruleId && snapshot.Cleanup.Any(e => e.Rule.Id == ruleId))
        {
            Add(new AssistantAction(ActionKind.CleanupItem, ruleId));
        }

        foreach (Finding finding in findings.Where(f => f.Action is not null && (group == TopicKind.Overview || f.Group == group)))
        {
            Add(finding.Action!);
        }

        if (group is TopicKind.Memory or TopicKind.Processor or TopicKind.Overview && ClosableTopApp(snapshot.Latest) is { } app)
        {
            Add(new AssistantAction(ActionKind.CloseApp, app.Key));
        }

        AssistantAction[] defaults = group switch
        {
            TopicKind.Storage => [new(ActionKind.Cleanup), new(ActionKind.Storage)],
            TopicKind.Memory or TopicKind.Processor => [new(ActionKind.Memory)],
            TopicKind.Graphics or TopicKind.Power => [new(ActionKind.Specs)],
            _ => [new(ActionKind.Cleanup), new(ActionKind.Memory), new(ActionKind.Specs)],
        };
        foreach (AssistantAction action in defaults)
        {
            Add(action);
        }

        if (group == TopicKind.Storage && !snapshot.IsUnlocked)
        {
            Add(new AssistantAction(ActionKind.Unlock));
        }

        return [.. actions.Take(MaxActions)];
    }

    /// <summary>The app using the most memory, when the user may close it.</summary>
    private static AppGroup? ClosableTopApp(MonitorSample? sample) =>
        sample?.Apps.OrderByDescending(a => a.Memory).FirstOrDefault() is { CanClose: true, IsProtected: false } app ? app : null;

    /// <summary>Drops the least important facts, then findings, until the text fits the budget.</summary>
    private static ContextBundle Trim(ContextBundle bundle)
    {
        while (bundle.ToText().Length > MaxCharacters && bundle.Facts.Count > 1)
        {
            bundle = bundle with { Facts = [.. bundle.Facts.Take(bundle.Facts.Count - 1)] };
        }

        while (bundle.ToText().Length > MaxCharacters && bundle.Findings.Count > 1)
        {
            bundle = bundle with { Findings = [.. bundle.Findings.Take(bundle.Findings.Count - 1)] };
        }

        return bundle;
    }
}
