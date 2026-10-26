using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pennyworth.KnowledgeBase;

namespace Pennyworth.Assistant;

/// <summary>
/// Holds a streamed answer back one sentence at a time and replaces any sentence that gives
/// advice from the blocked list. Refusals that name a blocked topic ("I wouldn't disable Windows
/// Security") pass: in phase 0 small models often said what they would not do.
/// </summary>
public sealed partial class AdviceFilter
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly List<Regex> _patterns;
    private readonly Regex _negation;
    private readonly Regex? _warning;
    private readonly string _replacement;
    private readonly StringBuilder _buffer = new();

    public AdviceFilter(AdviceCatalog catalog, CultureInfo culture)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        _patterns = [.. catalog.Topics.SelectMany(t => t.Patterns).Select(p => new Regex(p, options, MatchTimeout))];
        _negation = WordsPattern(catalog.Negations, options);
        _warning = catalog.Warnings.Count == 0 ? null : WordsPattern(catalog.Warnings, options);
        _replacement = catalog.Replacement.Get(culture);
    }

    public static AdviceFilter FromKnowledgeBase(CultureInfo culture, string platform = KnowledgeBaseLoader.Windows) =>
        new(KnowledgeBaseLoader.LoadBlockedAdvice(platform), culture);

    /// <summary>Sentences replaced so far.</summary>
    public int BlockedCount { get; private set; }

    /// <summary>Releases every finished sentence in the text so far.</summary>
    public IEnumerable<AssistantUpdate> Push(string chunk)
    {
        _buffer.Append(chunk);
        string text = _buffer.ToString();
        int end = LastBoundary(text);
        if (end <= 0)
        {
            return [];
        }

        _buffer.Remove(0, end);
        return Check(text[..end]);
    }

    /// <summary>Releases what is left at the end of the answer.</summary>
    public IEnumerable<AssistantUpdate> Flush()
    {
        string rest = _buffer.ToString();
        _buffer.Clear();
        return rest.Length == 0 ? [] : Check(rest);
    }

    /// <summary>
    /// True when the sentence gives blocked advice. It is a refusal, and passes, when a negation
    /// comes before the advice ("Please don't turn off…"), not after ("Delete what you don't need"),
    /// or when it warns ("Editing the registry is risky").
    /// </summary>
    public bool IsBlocked(string sentence)
    {
        int first = _patterns.Select(p => p.Match(sentence)).Where(m => m.Success).Select(m => m.Index).DefaultIfEmpty(-1).Min();
        if (first < 0)
        {
            return false;
        }

        Match negation = _negation.Match(sentence);
        bool refusal = (negation.Success && negation.Index < first) || (_warning?.IsMatch(sentence) ?? false);
        return !refusal;
    }

    private static Regex WordsPattern(IEnumerable<string> words, RegexOptions options) =>
        new(@"(?<!\w)(" + string.Join("|", words.Select(Regex.Escape)) + @")(?!\w)", options, MatchTimeout);

    private List<AssistantUpdate> Check(string text)
    {
        var updates = new List<AssistantUpdate>();
        var safe = new StringBuilder();
        foreach (string sentence in SplitKeepingSpace(text))
        {
            if (IsBlocked(sentence))
            {
                if (safe.Length > 0)
                {
                    updates.Add(new TextUpdate(safe.ToString()));
                    safe.Clear();
                }

                BlockedCount++;
                string trailing = sentence[sentence.TrimEnd().Length..];
                updates.Add(new BlockedUpdate(_replacement + trailing, sentence));
            }
            else
            {
                safe.Append(sentence);
            }
        }

        if (safe.Length > 0)
        {
            updates.Add(new TextUpdate(safe.ToString()));
        }

        return updates;
    }

    /// <summary>End of the last complete sentence (including the whitespace after it), or 0.</summary>
    private static int LastBoundary(string text)
    {
        int end = 0;
        foreach (Match match in BoundaryPattern().Matches(text))
        {
            end = match.Index + match.Length;
        }

        return end;
    }

    private static IEnumerable<string> SplitKeepingSpace(string text)
    {
        int start = 0;
        foreach (Match match in BoundaryPattern().Matches(text))
        {
            int end = match.Index + match.Length;
            yield return text[start..end];
            start = end;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    /// <summary>Sentence end: ". ", "! ", "? ", "… " (whitespace required, so "1.5 GB" is safe) or a line break.</summary>
    [GeneratedRegex(@"(?<=[.!?…:])\s+|\n+")]
    private static partial Regex BoundaryPattern();
}
