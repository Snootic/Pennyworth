using Microsoft.Extensions.AI;
using Pennyworth.Core.Wording;
using Pennyworth.KnowledgeBase;
using static Pennyworth.Assistant.Tests.Samples;

namespace Pennyworth.Assistant.Tests;

public class AssistantSessionTests
{
    private static readonly AssistantProfile Local = new("local", AssistantBackend.Local, "On this PC", "phi-3.5-mini");
    private static readonly AssistantProfile Cloud = new("cloud", AssistantBackend.OpenAiCompatible, "My account", "gpt");
    private static readonly Anonymizer Ana = new("ana", @"C:\Users\ana", "DESKTOP-ANA");

    private static AssistantSession Session(FakeChatClient client, AssistantProfile profile) =>
        new(client, profile, DiagnosisEngine.FromKnowledgeBase(), KnowledgeBaseLoader.LoadBlockedAdvice(), WindowsText.Instance, Ana);

    private static async Task<List<AssistantUpdate>> Ask(AssistantSession session, string question, PcSnapshot? snapshot = null, IReadOnlyList<ChatMessage>? history = null)
    {
        var updates = new List<AssistantUpdate>();
        await foreach (var update in session.AskAsync(question, new AssistantTopic(TopicKind.Storage), snapshot ?? Snapshot(diskUsedPercent: 92), En, history ?? [], TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static string Shown(IEnumerable<AssistantUpdate> updates) => string.Concat(updates.Select(u => u switch
    {
        TextUpdate t => t.Text,
        BlockedUpdate b => "[blocked]",
        ActionUpdate a => $"[{a.Action.Code}]",
        _ => "",
    }));

    [Fact]
    public async Task Streams_a_grounded_answer_with_its_action_and_usage()
    {
        var client = new FakeChatClient(["Your drive is 92% ", "full. Shall we tidy up? ", "[[action:", "cleanup]]"]);

        var updates = await Ask(Session(client, Local), "Why is C: full?");

        var context = Assert.IsType<ContextUpdate>(updates[0]);
        Assert.Contains("Drive C: full: 92%", context.SentPrompt);
        Assert.Equal("Your drive is 92% full. Shall we tidy up? [cleanup]", Shown(updates));
        Assert.Equal(new UsageUpdate(120, 4), updates[^1]);

        var sent = Assert.Single(client.Calls);
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Equal("Why is C: full?", sent[^1].Text);
        Assert.Equal(AssistantSession.MaxOutputTokens, client.Options[0]!.MaxOutputTokens);
    }

    [Fact]
    public async Task Cloud_requests_hide_personal_names_and_answers_show_them_again()
    {
        var client = new FakeChatClient(["The folder <profile>\\Vid", "eos\\<fold", "er-1> is big, <user>."]);
        string question = @"What is in C:\Users\ana\Videos\Ana Trip, on my PC DESKTOP-ANA?";

        var updates = await Ask(Session(client, Cloud), question);

        var sent = Assert.Single(client.Calls);
        Assert.Equal(@"What is in <profile>\Videos\<folder-1>, on my PC <pc>?", sent[^1].Text);
        Assert.DoesNotContain("ana", sent[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"The folder C:\Users\ana\Videos\Ana Trip is big, ana.", Shown(updates));
        Assert.Equal(sent[^1].Text, Assert.IsType<ContextUpdate>(updates[0]).SentQuestion);
    }

    [Fact]
    public async Task The_preview_shows_exactly_what_is_sent()
    {
        var client = new FakeChatClient(["Fine."]);
        var session = Session(client, Cloud);

        var prepared = session.Prepare(@"What is C:\Users\ana\Music?", AssistantTopic.Overview, Snapshot(), En, []);
        Assert.True(prepared.IsCloud);
        Assert.Equal(@"What is <profile>\Music?", prepared.SentQuestion);
        Assert.Empty(client.Calls); // nothing sent while previewing

        await foreach (var _ in session.StreamAsync(prepared, TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal(prepared.Messages.Select(m => m.Text), client.Calls.Single().Select(m => m.Text));
    }

    [Fact]
    public async Task Local_requests_send_the_real_text()
    {
        var client = new FakeChatClient(["Fine."]);

        await Ask(Session(client, Local), @"What is C:\Users\ana\Videos?");

        Assert.Equal(@"What is C:\Users\ana\Videos?", client.Calls[0][^1].Text);
    }

    [Fact]
    public async Task Unsafe_sentences_are_replaced_and_disallowed_actions_dropped()
    {
        var client = new FakeChatClient(["Free space first. Then disable Windows Defender. ", "[[action:close-app:explorer]]"]);

        var updates = await Ask(Session(client, Local), "Speed it up?");

        Assert.Equal("Free space first. [blocked]", Shown(updates));
        Assert.Single(updates.OfType<BlockedUpdate>());
        Assert.Empty(updates.OfType<ActionUpdate>());
    }

    [Fact]
    public async Task Stops_an_answer_that_repeats_itself()
    {
        var client = new FakeChatClient([.. Enumerable.Repeat("bem, ", 40), "end"]);
        var session = Session(client, Local);

        var updates = await Ask(session, "Por quê?");

        Assert.True(session.StoppedRepeating);
        Assert.DoesNotContain("end", Shown(updates));
    }

    [Fact]
    public async Task Earlier_turns_are_sent_as_history()
    {
        var client = new FakeChatClient(["Yes."]);
        ChatMessage[] history = [new(ChatRole.User, "Is C: full?"), new(ChatRole.Assistant, "It is 92% full.")];

        await Ask(Session(client, Local), "Should I clean?", history: history);

        Assert.Equal(["Is C: full?", "It is 92% full.", "Should I clean?"], client.Calls[0].Skip(1).Select(m => m.Text));
    }

    [Fact]
    public async Task Cancellation_stops_the_stream()
    {
        var client = new FakeChatClient(["One. ", "Two. ", "Three."]) { Gate = new TaskCompletionSource() };
        using var cts = new CancellationTokenSource();
        var session = Session(client, Local);
        var seen = new List<AssistantUpdate>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in session.AskAsync("Hi", AssistantTopic.Overview, Snapshot(), En, [], cts.Token))
            {
                seen.Add(update);
                if (update is ContextUpdate)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.DoesNotContain(seen, u => u is TextUpdate { Text: "Three." });
    }
}
