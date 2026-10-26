using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.Assistant;
using Pennyworth.Assistant.Local;
using Pennyworth.Assistant.Providers;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring.Hardware;
using Pennyworth.Platform;

namespace Pennyworth.App.Tests;

// The shell sets the process-wide language from settings, so these run apart from tests that switch it.
[Collection(nameof(LanguageSwitching))]
public sealed class AskViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pw-ask-").FullName;
    private readonly FakeKeys _keys = new();
    private readonly List<(AssistantProfile Profile, string? Key)> _clients = [];
    private Func<IChatClient> _nextClient = () => new ScriptedClient("Your drive is 50% full. ", "[[action:cleanup]]");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AssistantServices Services(ModelCatalog? catalog = null) => new(
        _keys,
        new ConversationStore(Path.Combine(_dir, "conversations")),
        new UsageMeter(Path.Combine(_dir, "usage.json")),
        catalog ?? TinyCatalog(),
        new LocalModelStore(Path.Combine(_dir, "models")),
        (profile, key) =>
        {
            _clients.Add((profile, key));
            return _nextClient();
        },
        async (model, folder, progress, ct) =>
        {
            Directory.CreateDirectory(folder);
            foreach (ModelFile file in model.Files)
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, file.Path), new byte[file.Size], ct);
            }

            await File.WriteAllTextAsync(Path.Combine(folder, ".verified"), model.Revision, ct);
            progress.Report(new DownloadProgress(model.Size, model.Size, 0, TimeSpan.Zero, ""));
        },
        new Anonymizer("ana", @"C:\Users\ana", "DESKTOP-ANA"),
        () => 8L << 30,
        _ => 100L << 30);

    private (MainWindowViewModel Shell, AskViewModel Ask) Create(ISettingsStore? settings = null)
    {
        var shell = new MainWindowViewModel(TestApp.Services(settings: settings) with { Specs = new FakeSpecs(), Assistant = Services() });
        shell.Initialize();
        return (shell, shell.Ask!);
    }

    [Fact]
    public void Offers_the_on_pc_model_only_where_the_platform_can_run_it()
    {
        var capable = new MainWindowViewModel(TestApp.Services() with { Assistant = Services() }).Ask!;
        var incapable = new MainWindowViewModel(TestApp.Services() with { Assistant = Services() with { CanRunLocally = false } }).Ask!;

        Assert.True(capable.CanRunLocally);
        Assert.Equal(2, capable.SetupColumns);
        Assert.False(incapable.CanRunLocally);
        Assert.Equal(1, incapable.SetupColumns);
    }

    [Fact]
    public async Task Starts_with_setup_and_recommends_a_model_for_this_PC()
    {
        var (shell, ask) = Create();

        shell.GoAskCommand.Execute(null);
        await ask.EnterAsync();

        Assert.True(shell.IsAskPage);
        Assert.True(ask.IsSetup);
        Assert.Equal("tiny-gpu", ask.RecommendedModel.Id); // the sample PC has an 8 GB graphics card
        Assert.Equal(Resources.Strings.FitGood, ask.VerdictTitle);
        Assert.False(ask.IsInstalled);
    }

    [Fact]
    public async Task Downloading_the_local_model_makes_it_the_way_to_answer()
    {
        var (_, ask) = Create();
        await ask.EnterAsync();

        await ask.DownloadModelCommand.ExecuteAsync(null);

        Assert.True(ask.IsChat);
        Assert.Equal(AssistantBackend.Local, ask.Profile!.Backend);
        Assert.Equal("tiny-gpu", ask.Profile.ModelId);
        Assert.StartsWith("On this PC", ask.BadgeText);
    }

    [Fact]
    public async Task Local_answers_stream_with_their_action_and_are_kept_in_history()
    {
        var (shell, ask) = Create();
        await ask.EnterAsync();
        await ask.DownloadModelCommand.ExecuteAsync(null);

        ask.Draft = "How is my PC?";
        await ask.SendCommand.ExecuteAsync(null);
        await ask.CurrentRun;

        Assert.Equal(2, ask.Messages.Count);
        var answer = ask.Messages[1];
        Assert.Equal("Your drive is 50% full. ", answer.Text);
        Assert.Equal(ActionKind.Cleanup, answer.Action!.Action.Kind);
        Assert.Null(_clients.Single().Key); // local: no key involved
        Assert.Equal("", ask.Draft);

        ask.RateHelpfulCommand.Execute(answer);
        ask.ShowHistoryCommand.Execute(null);
        var row = Assert.Single(ask.Conversations);
        Assert.Equal("How is my PC?", row.Title);
        ask.NewConversationCommand.Execute(null);
        ask.OpenConversationCommand.Execute(row);
        Assert.True(ask.Messages[1].Helpful);

        ask.OpenActionCommand.Execute(answer.Action);
        Assert.True(shell.IsCleanupPage);
    }

    [Fact]
    public async Task Cloud_accounts_keep_the_key_out_of_the_settings_file()
    {
        string path = Path.Combine(_dir, "settings.json");
        var (_, ask) = Create(new FileSettingsStore(path));

        ask.SelectBackendCommand.Execute(AssistantBackend.Anthropic);
        ask.ApiKey = "sk-ant-secret";
        ask.SaveCloudProfileCommand.Execute(null);

        Assert.True(ask.IsChat);
        Assert.Equal(AssistantBackend.Anthropic, ask.Profile!.Backend);
        Assert.Equal("claude-haiku-4-5", ask.Profile.ModelId);
        Assert.Equal("sk-ant-secret", _keys.Read(ask.Profile.Id));
        Assert.DoesNotContain("sk-ant-secret", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("\"backend\": \"Anthropic\"", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cloud_questions_show_what_is_sent_once_per_conversation_and_count_tokens()
    {
        var (shell, ask) = Create();
        ask.ApiKey = "sk-1";
        ask.SaveCloudProfileCommand.Execute(null);

        ask.Draft = @"What is in C:\Users\ana\Videos?";
        await ask.SendCommand.ExecuteAsync(null);

        var preview = Assert.IsType<SendPreviewDialogViewModel>(shell.Dialog);
        Assert.Equal(@"What is in <profile>\Videos?", preview.Question);
        Assert.DoesNotContain(@"C:\Users\ana", preview.Instructions);
        Assert.Empty(ask.Messages);

        preview.SendCommand.Execute(null);
        await ask.CurrentRun;
        Assert.Null(shell.Dialog);
        Assert.Equal("sk-1", _clients[^1].Key);
        Assert.True(ask.Messages[1].HasTokens);
        Assert.Contains("tokens used this month", ask.UsageText);

        ask.Draft = "And now?";
        await ask.SendCommand.ExecuteAsync(null);
        await ask.CurrentRun;
        Assert.Null(shell.Dialog); // same conversation and topic: no second preview
        Assert.Equal(4, ask.Messages.Count);
    }

    [Fact]
    public async Task Refused_keys_and_missing_keys_explain_themselves()
    {
        var (shell, ask) = Create();
        ask.ApiKey = "sk-bad";
        ask.SaveCloudProfileCommand.Execute(null);
        ask.ShowPreview = false;
        _nextClient = () => new ScriptedClient(new ProviderException(401, "invalid x-api-key"));

        ask.Draft = "Hi";
        await ask.SendCommand.ExecuteAsync(null);
        await ask.CurrentRun;

        Assert.Equal(Resources.Strings.ErrorBadKey, ask.Messages[1].ErrorText);
        Assert.Contains("401", ask.Messages[1].ErrorDetail);

        _keys.Delete(ask.Profile!.Id);
        ask.Draft = "Again";
        await ask.SendCommand.ExecuteAsync(null);
        Assert.Equal(Resources.Strings.ErrorNoKey, ask.Messages[^1].Text);
    }

    [Fact]
    public async Task Stops_asking_the_cloud_when_the_monthly_cap_is_used()
    {
        var (_, ask) = Create();
        ask.ApiKey = "sk-1";
        ask.SaveCloudProfileCommand.Execute(null);
        ask.ShowPreview = false;
        ask.ShowSettingsCommand.Execute(null);
        ask.CapText = "100";
        ask.SaveCapCommand.Execute(null);
        _nextClient = () => new ScriptedClient(inputTokens: 200, "Fine.");

        ask.Draft = "First";
        await ask.SendCommand.ExecuteAsync(null);
        await ask.CurrentRun;
        Assert.Equal(Resources.Strings.UsageReached, ask.UsageNote);

        ask.Draft = "Second";
        await ask.SendCommand.ExecuteAsync(null);
        Assert.Equal(Resources.Strings.UsageReached, ask.Messages[^1].Text);
        Assert.Single(_clients); // the second question never reached the provider
    }

    [Fact]
    public async Task Ask_about_this_opens_the_screen_with_the_question_ready()
    {
        var (shell, ask) = Create();
        await ask.EnterAsync();
        await ask.DownloadModelCommand.ExecuteAsync(null);
        shell.GoHomeCommand.Execute(null);

        shell.AskAbout(new AssistantTopic(TopicKind.Memory), "Why is Chrome so big?");

        Assert.True(shell.IsAskPage);
        Assert.Equal("Why is Chrome so big?", ask.Draft);
        Assert.Equal(TopicKind.Memory, ask.Topic.Kind);
    }

    [Fact]
    public async Task Every_screen_can_ask_about_what_it_shows()
    {
        var (shell, ask) = Create();
        await shell.Specs.EnsureLoaded();

        shell.Home.AskAboutPcCommand.Execute(null);
        Assert.Equal((TopicKind.Overview, Resources.Strings.AskAboutHome), (ask.Topic.Kind, ask.Draft));

        shell.Specs.AskAboutSectionCommand.Execute(shell.Specs.Sections[1]);
        Assert.Equal(new AssistantTopic(TopicKind.SpecSection, "02"), ask.Topic);
        Assert.Contains(shell.Specs.Sections[1].Title, ask.Draft);

        var row = new AppRowViewModel("chrome", null);
        shell.Memory.AskAboutAppCommand.Execute(row);
        Assert.Equal(TopicKind.Memory, ask.Topic.Kind);

        shell.Pc.SelectPartCommand.Execute(PcPart.Graphics);
        shell.Pc.AskAboutPartCommand.Execute(null);
        Assert.Equal(TopicKind.Graphics, ask.Topic.Kind);
        Assert.True(shell.IsAskPage);
        Assert.True(shell.Home.CanAsk && shell.Memory.CanAsk && shell.Cleanup.CanAsk && shell.Specs.CanAsk);
    }

    [Fact]
    public void Without_assistant_services_no_screen_offers_to_ask()
    {
        var shell = new MainWindowViewModel(TestApp.Services());

        Assert.Null(shell.Ask);
        Assert.False(shell.Home.CanAsk || shell.Memory.CanAsk || shell.Cleanup.CanAsk || shell.Specs.CanAsk || shell.Pc.CanAsk);
    }

    [Fact]
    public async Task Removing_the_last_way_to_answer_goes_back_to_setup()
    {
        var (_, ask) = Create();
        ask.ApiKey = "sk-1";
        ask.SaveCloudProfileCommand.Execute(null);
        string id = ask.Profile!.Id;
        ask.ShowSettingsCommand.Execute(null);

        ask.RemoveProfileCommand.Execute(Assert.Single(ask.ProfileRows));

        Assert.True(ask.IsSetup);
        Assert.Null(_keys.Read(id));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Stop_cancels_a_streaming_answer()
    {
        var (_, ask) = Create();
        await ask.EnterAsync();
        await ask.DownloadModelCommand.ExecuteAsync(null);
        var gate = new TaskCompletionSource();
        _nextClient = () => new ScriptedClient("Start. ", "never shown") { Gate = gate };

        ask.Draft = "Long answer please";
        var sending = ask.SendCommand.ExecuteAsync(null);
        while (!ask.IsBusy || !ask.Messages[^1].HasText)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        ask.StopCommand.Execute(null);
        await sending;

        Assert.False(ask.IsBusy);
        Assert.Equal("Start. ", ask.Messages[^1].Text);
        Assert.False(ask.Messages[^1].HasError);
    }

    private static ModelCatalog TinyCatalog()
    {
        byte[] data = RandomNumberGenerator.GetBytes(16);
        string hash = Convert.ToHexStringLower(SHA256.HashData(data));
        LocalModel Model(string id, ModelDevice device, int video) => new(id, id, "org/x", new string('b', 40), "f", "MIT", device, 8, video, [],
            new LocalizedText("note", "nota"), [new ModelFile("genai_config.json", 16, hash)]);
        return new ModelCatalog(1, [Model("tiny-gpu", ModelDevice.Gpu, 6), Model("tiny-cpu", ModelDevice.Cpu, 0)]);
    }

    private sealed class FakeKeys : ICredentialStore
    {
        private readonly Dictionary<string, string> _keys = [];

        public void Save(string profileId, string secret) => _keys[profileId] = secret;

        public string? Read(string profileId) => _keys.GetValueOrDefault(profileId);

        public void Delete(string profileId) => _keys.Remove(profileId);
    }

    private sealed class FakeSpecs : ISpecsReader
    {
        public PcSpecs Read() => SpecsViewModelTests.SamplePc();
    }

    private sealed class ScriptedClient : IChatClient
    {
        private readonly string[] _chunks;
        private readonly Exception? _error;
        private readonly long _input;

        public ScriptedClient(params string[] chunks) : this(10, chunks)
        {
        }

        public ScriptedClient(long inputTokens, params string[] chunks)
        {
            _chunks = chunks;
            _input = inputTokens;
        }

        public ScriptedClient(Exception error)
        {
            _chunks = [];
            _error = error;
        }

        public TaskCompletionSource? Gate { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_error is not null)
            {
                throw _error;
            }

            for (int i = 0; i < _chunks.Length; i++)
            {
                if (i > 0 && Gate is { } gate)
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, _chunks[i]);
            }

            yield return new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = _input, OutputTokenCount = _chunks.Length })] };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
