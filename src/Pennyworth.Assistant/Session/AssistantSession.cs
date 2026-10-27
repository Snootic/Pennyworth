using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Pennyworth.Core.Wording;
using Pennyworth.KnowledgeBase;

namespace Pennyworth.Assistant;

/// <summary>
/// One question, start to finish: facts → findings → context → butler prompt → model, then the
/// streamed answer is restored (cloud), linked (action codes) and filtered (unsafe advice) before
/// the user sees it. The session never acts on the PC.
/// </summary>
public sealed partial class AssistantSession(
    IChatClient client,
    AssistantProfile profile,
    DiagnosisEngine engine,
    AdviceCatalog advice,
    IPlatformText wording,
    Anonymizer? anonymizer = null)
{
    public const int MaxOutputTokens = 400;

    /// <summary>Hard stop for a runaway answer, in characters.</summary>
    public const int MaxAnswerCharacters = 2_400;

    /// <summary>Whether the last answer was cut because it started repeating itself.</summary>
    public bool StoppedRepeating { get; private set; }

    public async IAsyncEnumerable<AssistantUpdate> AskAsync(
        string question,
        AssistantTopic topic,
        PcSnapshot snapshot,
        CultureInfo culture,
        IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        PreparedQuestion prepared = Prepare(question, topic, snapshot, culture, history);
        yield return new ContextUpdate(prepared.Bundle, prepared.SentPrompt, prepared.SentQuestion);
        await foreach (AssistantUpdate update in StreamAsync(prepared, cancellationToken))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Everything that will be sent, built and (for the cloud) anonymised, without sending it —
    /// so the person can see it first in the send preview.
    /// </summary>
    public PreparedQuestion Prepare(string question, AssistantTopic topic, PcSnapshot snapshot, CultureInfo culture, IReadOnlyList<ChatMessage> history)
    {
        var facts = FactCollector.Collect(snapshot, culture);
        var bundle = ContextBuilder.Build(topic, facts, engine.Evaluate(facts, culture), snapshot, culture, wording);
        bool cloud = !profile.IsLocal && anonymizer is not null;
        string Send(string text) => cloud ? anonymizer!.Anonymize(text) : text;

        string prompt = Send(PersonaPrompt.Compose(bundle));
        string sentQuestion = Send(question);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, prompt),
            .. history.Select(m => new ChatMessage(m.Role, Send(m.Text))),
            new(ChatRole.User, sentQuestion),
        ];
        return new PreparedQuestion(bundle, prompt, sentQuestion, messages, cloud);
    }

    /// <summary>Sends a prepared question and streams the restored, linked and filtered answer.</summary>
    public async IAsyncEnumerable<AssistantUpdate> StreamAsync(PreparedQuestion prepared, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StoppedRepeating = false;
        ContextBundle bundle = prepared.Bundle;
        IReadOnlyList<ChatMessage> messages = prepared.Messages;
        var options = new ChatOptions { MaxOutputTokens = MaxOutputTokens, Temperature = 0.3f };

        var restorer = new PlaceholderRestorer(prepared.IsCloud ? anonymizer : null);
        var linker = new ActionLinker(bundle.AllowedActions);
        var filter = new AdviceFilter(advice, bundle.Culture);
        var answer = new StringBuilder();
        UsageDetails? usage = null;

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options, cancellationToken).WithCancellation(cancellationToken))
        {
            foreach (UsageContent content in update.Contents.OfType<UsageContent>())
            {
                usage = content.Details;
            }

            if (update.Text.Length == 0)
            {
                continue;
            }

            answer.Append(update.Text);
            foreach (AssistantUpdate output in Pipe(restorer.Push(update.Text), linker, filter))
            {
                yield return output;
            }

            if (answer.Length > MaxAnswerCharacters || LoopPattern().IsMatch(Tail(answer)))
            {
                StoppedRepeating = true;
                break;
            }
        }

        foreach (AssistantUpdate output in Pipe(restorer.Flush(), linker, filter))
        {
            yield return output;
        }

        foreach (AssistantUpdate output in Pipe(linker.Flush(), filter))
        {
            yield return output;
        }

        foreach (AssistantUpdate output in filter.Flush())
        {
            yield return output;
        }

        if (usage is not null)
        {
            yield return new UsageUpdate(usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0);
        }
    }

    private static IEnumerable<AssistantUpdate> Pipe(string text, ActionLinker linker, AdviceFilter filter) =>
        text.Length == 0 ? [] : Pipe(linker.Push(text), filter);

    /// <summary>Text goes through the advice filter; an action first releases the sentence before it.</summary>
    private static IEnumerable<AssistantUpdate> Pipe(IEnumerable<AssistantUpdate> linked, AdviceFilter filter)
    {
        foreach (AssistantUpdate update in linked)
        {
            if (update is TextUpdate text)
            {
                foreach (AssistantUpdate filtered in filter.Push(text.Text))
                {
                    yield return filtered;
                }
            }
            else
            {
                foreach (AssistantUpdate filtered in filter.Flush())
                {
                    yield return filtered;
                }

                yield return update;
            }
        }
    }

    private static string Tail(StringBuilder text) => text.ToString(Math.Max(text.Length - 300, 0), Math.Min(text.Length, 300));

    /// <summary>The same word or short phrase eight times in a row ("bem, bem, bem, …").</summary>
    [GeneratedRegex(@"(\b[\p{L}\p{N}'’]+(?:\s+[\p{L}\p{N}'’]+){0,3}[\s,.;!…-]+)\1{7,}", RegexOptions.IgnoreCase)]
    private static partial Regex LoopPattern();

    /// <summary>Restores placeholders that may arrive split across chunks ("&lt;fol", "der-1&gt;").</summary>
    private sealed class PlaceholderRestorer(Anonymizer? anonymizer)
    {
        private const int MaxPlaceholder = 16;
        private string _pending = "";

        public string Push(string chunk)
        {
            if (anonymizer is null)
            {
                return chunk;
            }

            string text = _pending + chunk;
            int open = text.LastIndexOf('<');
            if (open >= 0 && text.IndexOf('>', open) < 0 && text.Length - open <= MaxPlaceholder)
            {
                _pending = text[open..];
                text = text[..open];
            }
            else
            {
                _pending = "";
            }

            return anonymizer.Restore(text);
        }

        public string Flush()
        {
            string rest = _pending;
            _pending = "";
            return anonymizer is null ? rest : anonymizer.Restore(rest);
        }
    }
}
