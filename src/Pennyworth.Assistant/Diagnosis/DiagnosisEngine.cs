using System.Globalization;
using System.Text.RegularExpressions;
using Pennyworth.KnowledgeBase;

namespace Pennyworth.Assistant;

/// <summary>A conclusion of a diagnosis rule, in the user's language.</summary>
public sealed record Finding(string RuleId, DiagnosisSeverity Severity, TopicKind Group, string Text, AssistantAction? Action);

/// <summary>
/// Applies the knowledge base's diagnosis rules to facts. Deterministic: the model only
/// explains what these rules conclude.
/// </summary>
public sealed partial class DiagnosisEngine(IReadOnlyList<DiagnosisRule> rules)
{
    public static DiagnosisEngine FromKnowledgeBase(string platform = KnowledgeBaseLoader.Windows) => new(KnowledgeBaseLoader.LoadDiagnosisRules(platform).Rules);

    public IReadOnlyList<DiagnosisRule> Rules { get; } = rules;

    /// <summary>Findings whose conditions all hold, most severe first, then in catalogue order.</summary>
    public IReadOnlyList<Finding> Evaluate(IReadOnlyList<Fact> facts, CultureInfo culture)
    {
        var byId = facts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        return
        [
            .. Rules
                .Select((rule, order) => (rule, order))
                .Where(r => r.rule.When.All(condition => Holds(condition, byId)))
                .OrderByDescending(r => r.rule.Severity)
                .ThenBy(r => r.order)
                .Select(r => new Finding(
                    r.rule.Id,
                    r.rule.Severity,
                    Enum.TryParse(r.rule.Group, out TopicKind group) ? group : TopicKind.Overview,
                    Fill(r.rule.Finding.Get(culture), byId, culture),
                    r.rule.Action is null ? null : AssistantAction.Parse(r.rule.Action))),
        ];
    }

    /// <summary>True when nothing needs attention (only informational findings, or none).</summary>
    public static bool IsAllClear(IReadOnlyList<Finding> findings) => findings.All(f => f.Severity == DiagnosisSeverity.Info);

    /// <summary>Fact ids a finding text names, for catalogue checks.</summary>
    public static IEnumerable<string> Placeholders(string text) => PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value);

    private static bool Holds(FactCondition condition, Dictionary<string, Fact> facts)
    {
        if (!facts.TryGetValue(condition.Fact, out Fact? fact))
        {
            return false; // Not measured: never conclude anything from missing data.
        }

        double n = fact.Number;
        return condition.Op switch
        {
            "gt" => n > condition.Value,
            "gte" => n >= condition.Value,
            "lt" => n < condition.Value,
            "lte" => n <= condition.Value,
            "eq" => n == condition.Value,
            "exists" => true,
            _ => false,
        };
    }

    private static string Fill(string text, Dictionary<string, Fact> facts, CultureInfo culture) =>
        PlaceholderPattern().Replace(text, match => facts.TryGetValue(match.Groups[1].Value, out Fact? fact) ? fact.Format(culture) : "?");

    [GeneratedRegex(@"\{([a-z0-9_.\-]+)\}")]
    private static partial Regex PlaceholderPattern();
}
