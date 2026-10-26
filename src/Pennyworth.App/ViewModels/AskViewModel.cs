using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Assistant;
using Pennyworth.Assistant.Local;
using Pennyworth.Assistant.Providers;
using Pennyworth.Core.Formatting;
using Pennyworth.KnowledgeBase;
using Pennyworth.Platform;

namespace Pennyworth.App.ViewModels;

/// <summary>What the Ask screen needs from the shell.</summary>
public interface IAskHost : IDialogHost
{
    AssistantSettings AssistantSettings { get; }

    void SaveAssistantSettings(AssistantSettings settings);

    /// <summary>What the app has measured right now (loads the specs first if needed).</summary>
    Task<PcSnapshot> SnapshotAsync();

    /// <summary>Opens the screen or dialog an answer points to.</summary>
    void Open(AssistantAction action);

    /// <summary>The display name of a running app, for "Close Google Chrome…".</summary>
    string? AppName(string key);
}

public enum AskMode
{
    Setup,
    Chat,
    History,
    Settings,
}

/// <summary>
/// Screen 07: the butler assistant. Setup (download a local model or add an AI account), the
/// conversation with streaming, Stop and action buttons, the send preview for cloud requests,
/// history and settings. Every answer goes through <see cref="AssistantSession"/>.
/// </summary>
public sealed partial class AskViewModel : ObservableObject
{
    private const int HistoryMessages = 6;
    private const int TitleLength = 60;

    private readonly Lazy<DiagnosisEngine> _engine;
    private readonly Lazy<AdviceCatalog> _advice;
    private readonly AssistantServices _services;
    private readonly IAskHost _host;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HashSet<string> _previewed = [];
    private CancellationTokenSource? _answer;
    private CancellationTokenSource? _download;
    private string? _conversationId;
    private Guid _previewScope = Guid.NewGuid();
    private Pennyworth.Monitoring.Hardware.PcSpecs? _specs;

    public AskViewModel(AssistantServices services, IAskHost host, Func<DateTimeOffset> clock)
    {
        _services = services;
        _engine = new(() => DiagnosisEngine.FromKnowledgeBase(services.PlatformName));
        _advice = new(() => KnowledgeBaseLoader.LoadBlockedAdvice(services.PlatformName));
        _host = host;
        _clock = clock;
        RecommendedModel = services.Catalog.Models.First(m => m.Device == ModelDevice.Cpu);
        Mode = host.AssistantSettings.Default is null ? AskMode.Setup : AskMode.Chat;
        SelectBackend(AssistantBackend.OpenAiCompatible);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetup), nameof(IsChat), nameof(IsHistory), nameof(IsSettings), nameof(ShowStarters), nameof(CanGoBack))]
    public partial AskMode Mode { get; private set; }

    public bool IsSetup => Mode == AskMode.Setup;

    public bool IsChat => Mode == AskMode.Chat;

    public bool IsHistory => Mode == AskMode.History;

    public bool IsSettings => Mode == AskMode.Settings;

    /// <summary>Setup reached from Settings ("Add another") can go back.</summary>
    public bool CanGoBack => Mode != AskMode.Chat && Profile is not null;

    public AssistantProfile? Profile => _host.AssistantSettings.Default;

    public string BadgeText => Profile switch
    {
        null => "",
        { IsLocal: true } p => string.Format(CultureInfo.CurrentCulture, Strings.BadgeLocal, p.DisplayName),
        { } p => string.Format(CultureInfo.CurrentCulture, Strings.BadgeCloud, p.DisplayName),
    };

    public ObservableCollection<AskMessageViewModel> Messages { get; } = [];

    public bool ShowStarters => IsChat && Messages.Count == 0;

    public IReadOnlyList<string> Starters => [Strings.StarterOverview, Strings.StarterSlow, Strings.StarterStorage, Strings.StarterExplain];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = "";

    /// <summary>What the next question is about; set by the "Ask about this" buttons.</summary>
    [ObservableProperty]
    public partial AssistantTopic Topic { get; set; } = AssistantTopic.Overview;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsageNote))]
    public partial string? UsageNote { get; private set; }

    public bool HasUsageNote => UsageNote is not null;

    /// <summary>The answer started by <see cref="SendCommand"/>; lets tests await it.</summary>
    public Task CurrentRun { get; private set; } = Task.CompletedTask;

    // ── Setup: local model ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalModelName), nameof(LocalSubtitle), nameof(DownloadLabel), nameof(IsInstalled))]
    public partial LocalModel RecommendedModel { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VerdictTitle), nameof(VerdictReasons), nameof(IsVerdictWarning))]
    public partial HardwareVerdict? Verdict { get; private set; }

    /// <summary>"Phi-4-mini on the graphics card".</summary>
    public string LocalModelName => string.Format(CultureInfo.CurrentCulture, RecommendedModel.Device == ModelDevice.Gpu ? Strings.DeviceGpu : Strings.DeviceCpu, RecommendedModel.DisplayName);

    public string LocalSubtitle => string.Format(CultureInfo.CurrentCulture, Strings.SetupLocalSub, ByteSize.Format(RecommendedModel.Size));

    public string DownloadLabel => string.Format(CultureInfo.CurrentCulture, Strings.DownloadModel, ByteSize.Format(RecommendedModel.Size));

    public bool IsInstalled => _services.Models.IsInstalled(RecommendedModel);

    /// <summary>False when this platform cannot run the on-PC model: the setup then offers only an AI account.</summary>
    public bool CanRunLocally => _services.CanRunLocally;

    public int SetupColumns => CanRunLocally ? 2 : 1;

    public string VerdictTitle => Verdict?.Fit switch
    {
        null => "",
        HardwareFit.Good => Strings.FitGood,
        HardwareFit.Slow => Strings.FitSlow,
        HardwareFit.NotEnoughMemory => Strings.FitMemory,
        _ => Strings.FitSpace,
    };

    public string VerdictReasons => Verdict is null ? "" : string.Join(" ", Verdict.Reasons);

    public bool IsVerdictWarning => Verdict?.Fit is HardwareFit.NotEnoughMemory or HardwareFit.NotEnoughSpace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand), nameof(CancelDownloadCommand))]
    public partial bool IsDownloading { get; private set; }

    [ObservableProperty]
    public partial double DownloadFraction { get; private set; }

    [ObservableProperty]
    public partial string DownloadText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownloadError))]
    public partial string? DownloadError { get; private set; }

    public bool HasDownloadError => DownloadError is not null;

    /// <summary>The download started by <see cref="DownloadModelCommand"/>; lets tests await it.</summary>
    public Task CurrentDownload { get; private set; } = Task.CompletedTask;

    // ── Setup: AI account ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenAi), nameof(IsAnthropic), nameof(IsGemini), nameof(IsAzure), nameof(ShowEndpoint), nameof(EndpointLabel), nameof(ModelLabel))]
    public partial AssistantBackend CloudBackend { get; private set; }

    public bool IsOpenAi => CloudBackend == AssistantBackend.OpenAiCompatible;

    public bool IsAnthropic => CloudBackend == AssistantBackend.Anthropic;

    public bool IsGemini => CloudBackend == AssistantBackend.Gemini;

    public bool IsAzure => CloudBackend == AssistantBackend.AzureOpenAi;

    public bool ShowEndpoint => CloudBackend is AssistantBackend.OpenAiCompatible or AssistantBackend.AzureOpenAi;

    public string EndpointLabel => IsAzure ? Strings.FieldEndpointAzure : Strings.FieldEndpoint;

    public string ModelLabel => IsAzure ? Strings.FieldModelAzure : Strings.FieldModel;

    [ObservableProperty]
    public partial string ProfileName { get; set; } = "";

    [ObservableProperty]
    public partial string Endpoint { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCloudProfileCommand), nameof(TestConnectionCommand))]
    public partial string ModelId { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCloudProfileCommand), nameof(TestConnectionCommand))]
    public partial string ApiKey { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    public partial bool IsTesting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial string? TestResult { get; private set; }

    public bool HasTestResult => TestResult is not null;

    [ObservableProperty]
    public partial bool TestOk { get; private set; }

    // ── History and settings ──────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConversations))]
    public partial IReadOnlyList<ConversationRowViewModel> Conversations { get; private set; } = [];

    public bool HasConversations => Conversations.Count > 0;

    [ObservableProperty]
    public partial bool ConfirmingDelete { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ProfileRowViewModel> ProfileRows { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ModelRowViewModel> ModelRows { get; private set; } = [];

    [ObservableProperty]
    public partial string CapText { get; set; } = "";

    public bool IsCloudProfile => Profile is { IsLocal: false };

    public bool ShowPreview
    {
        get => Profile is { } p && !_host.AssistantSettings.HidePreviewFor.Contains(p.Id);
        set
        {
            if (Profile is { } p)
            {
                var hidden = _host.AssistantSettings.HidePreviewFor.Where(id => id != p.Id).ToList();
                if (!value)
                {
                    hidden.Add(p.Id);
                }

                _host.SaveAssistantSettings(_host.AssistantSettings with { HidePreviewFor = hidden });
                OnPropertyChanged();
            }
        }
    }

    public string UsageText => Profile is { IsLocal: false } p
        ? string.Format(CultureInfo.CurrentCulture, Strings.UsageThisMonth, _services.Usage.Month(p.Id, _clock()).Total)
        : "";

    /// <summary>Called when the page opens: checks the PC for the local model and refreshes the lists.</summary>
    public async Task EnterAsync()
    {
        if (Profile is null)
        {
            Mode = AskMode.Setup;
        }

        PcSnapshot snapshot = await _host.SnapshotAsync();
        _specs = snapshot.Specs;
        RecommendedModel = HardwareCheck.Recommend(_services.Catalog, _specs);
        Verdict = HardwareCheck.Evaluate(
            RecommendedModel,
            _specs,
            _services.AvailableMemory(),
            _services.FreeSpace(_services.Models.FolderOf(RecommendedModel)),
            Localizer.Instance.Culture);
        UpdateUsageNote();
        OnPropertyChanged(nameof(IsInstalled));
    }

    /// <summary>"Ask about this" from another screen: the question is ready to send, about that topic.</summary>
    public void AskAbout(AssistantTopic topic, string question)
    {
        Topic = topic;
        Draft = question;
        if (Profile is not null)
        {
            Mode = AskMode.Chat;
        }
    }

    public void RefreshTexts()
    {
        foreach (AskMessageViewModel message in Messages)
        {
            message.Refresh();
        }

        if (Verdict is not null)
        {
            Verdict = HardwareCheck.Evaluate(Verdict.Model, _specs, _services.AvailableMemory(), _services.FreeSpace(_services.Models.FolderOf(Verdict.Model)), Localizer.Instance.Culture);
        }

        RefreshLists();
        OnPropertyChanged(string.Empty);
    }

    // ── Chat ──────────────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        string question = Draft.Trim();
        if (Profile is not { } profile)
        {
            Mode = AskMode.Setup;
            return;
        }

        string? key = null;
        if (profile.IsLocal)
        {
            if (!_services.Models.IsInstalled(_services.Catalog.Get(profile.ModelId)))
            {
                Fail(question, Strings.ErrorNoModel);
                return;
            }
        }
        else
        {
            key = _services.Keys.Read(profile.Id);
            if (string.IsNullOrEmpty(key))
            {
                Fail(question, Strings.ErrorNoKey);
                return;
            }

            if (_services.Usage.State(profile, _clock()) == UsageState.Reached)
            {
                Fail(question, Strings.UsageReached);
                return;
            }
        }

        IChatClient client = _services.CreateClient(profile, key);
        var session = new AssistantSession(client, profile, _engine.Value, _advice.Value, profile.IsLocal ? null : _services.Anonymizer);
        PcSnapshot snapshot = await _host.SnapshotAsync();
        PreparedQuestion prepared = session.Prepare(question, Topic, snapshot, Localizer.Instance.Culture, History());

        string scope = $"{_previewScope}|{Topic}";
        if (!profile.IsLocal && ShowPreview && !_previewed.Contains(scope))
        {
            _host.ShowDialog(new SendPreviewDialogViewModel(profile, prepared, dontShowAgain =>
            {
                _host.CloseDialog();
                _previewed.Add(scope);
                if (dontShowAgain)
                {
                    ShowPreview = false;
                }

                CurrentRun = RunAsync(session, client, profile, prepared, question);
            }, () =>
            {
                _host.CloseDialog();
                client.Dispose();
            }));
            return;
        }

        CurrentRun = RunAsync(session, client, profile, prepared, question);
        await CurrentRun;
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop() => _answer?.Cancel();

    [RelayCommand]
    private async Task AskStarter(string question)
    {
        Draft = question;
        Topic = AssistantTopic.Overview;
        if (SendCommand.CanExecute(null))
        {
            await SendCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private void OpenAction(AskActionViewModel action) => _host.Open(action.Action);

    [RelayCommand]
    private void RateHelpful(AskMessageViewModel message) => Rate(message, true);

    [RelayCommand]
    private void RateNotHelpful(AskMessageViewModel message) => Rate(message, false);

    [RelayCommand]
    private void NewConversation()
    {
        _answer?.Cancel();
        Messages.Clear();
        _conversationId = null;
        _previewScope = Guid.NewGuid();
        Topic = AssistantTopic.Overview;
        Mode = Profile is null ? AskMode.Setup : AskMode.Chat;
        OnPropertyChanged(nameof(ShowStarters));
    }

    [RelayCommand]
    private void ShowHistory()
    {
        RefreshLists();
        ConfirmingDelete = false;
        Mode = AskMode.History;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        RefreshLists();
        Mode = AskMode.Settings;
    }

    [RelayCommand]
    private void BackToChat() => Mode = Profile is null ? AskMode.Setup : AskMode.Chat;

    private async Task RunAsync(AssistantSession session, IChatClient client, AssistantProfile profile, PreparedQuestion prepared, string question)
    {
        Draft = "";
        var user = new AskMessageViewModel(isUser: true, question);
        var answer = new AskMessageViewModel(isUser: false) { IsStreaming = true };
        Messages.Add(user);
        Messages.Add(answer);
        OnPropertyChanged(nameof(ShowStarters));
        IsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _answer = cancellation;
        try
        {
            await foreach (AssistantUpdate update in session.StreamAsync(prepared, cancellation.Token))
            {
                switch (update)
                {
                    case TextUpdate text:
                        answer.Append(text.Text);
                        break;
                    case ActionUpdate action:
                        answer.Action = new AskActionViewModel(action.Action, action.Action.Argument is { } k ? _host.AppName(k) : null);
                        break;
                    case BlockedUpdate blocked:
                        answer.Append(blocked.Replacement);
                        answer.Blocked++;
                        break;
                    case UsageUpdate usage when !profile.IsLocal:
                        answer.Tokens = usage.InputTokens + usage.OutputTokens;
                        _services.Usage.Add(profile.Id, usage.InputTokens, usage.OutputTokens, _clock());
                        break;
                }
            }

            answer.StoppedRepeating = session.StoppedRepeating;
        }
        catch (Exception ex)
        {
            AssistantError error = AssistantError.From(ex, cancellation.Token);
            if (error.Kind != AssistantErrorKind.Cancelled)
            {
                answer.Error = error;
            }
        }
        finally
        {
            _answer = null;
            client.Dispose();
            Save(profile, user, answer, prepared);
            answer.Finish();
            IsBusy = false;
            UpdateUsageNote();
        }
    }

    private void Fail(string question, string message)
    {
        Messages.Add(new AskMessageViewModel(isUser: true, question));
        Messages.Add(new AskMessageViewModel(isUser: false) { Error = new AssistantError(AssistantErrorKind.Unknown, message) });
        Messages[^1].Append(message);
        Draft = "";
        OnPropertyChanged(nameof(ShowStarters));
    }

    /// <summary>The last finished turns, sent so follow-up questions make sense.</summary>
    private List<ChatMessage> History() =>
    [
        .. Messages
            .Where(m => m.HasText && !m.HasError && !m.IsStreaming)
            .TakeLast(HistoryMessages)
            .Select(m => new ChatMessage(m.IsUser ? ChatRole.User : ChatRole.Assistant, m.Text)),
    ];

    private void Save(AssistantProfile profile, AskMessageViewModel user, AskMessageViewModel answer, PreparedQuestion prepared)
    {
        try
        {
            if (_conversationId is null)
            {
                string title = user.Text.Length > TitleLength ? user.Text[..TitleLength] + "…" : user.Text;
                _conversationId = _services.Conversations.Start(title, profile.Id, _clock()).Id;
            }

            _services.Conversations.AppendTurn(_conversationId, new ConversationTurn("user", user.Text, _clock(), prepared.SentPrompt));
            if (answer.HasText)
            {
                answer.TurnIndex = _services.Conversations.AppendTurn(_conversationId, new ConversationTurn("assistant", answer.Text, _clock(), Blocked: answer.Blocked));
            }
        }
        catch (IOException)
        {
            // History is a convenience; an answer must not fail because it couldn't be saved.
        }
    }

    private void Rate(AskMessageViewModel message, bool helpful)
    {
        if (_conversationId is { } id && message.TurnIndex is int turn)
        {
            _services.Conversations.SetFeedback(id, turn, helpful, _clock());
            message.Helpful = helpful;
            _host.ShowToast(Strings.FeedbackThanks);
        }
    }

    private void UpdateUsageNote()
    {
        UsageNote = Profile is { IsLocal: false, MonthlyTokenCap: long cap } p && cap > 0
            ? _services.Usage.State(p, _clock()) switch
            {
                UsageState.Reached => Strings.UsageReached,
                UsageState.Near => string.Format(CultureInfo.CurrentCulture, Strings.UsageNear, ((double)_services.Usage.Month(p.Id, _clock()).Total / cap).ToString("P0", CultureInfo.CurrentCulture)),
                _ => null,
            }
            : null;
    }

    // ── Setup ─────────────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadModel()
    {
        CurrentDownload = DownloadCoreAsync(RecommendedModel);
        return CurrentDownload;
    }

    private bool CanDownload() => !IsDownloading;

    [RelayCommand(CanExecute = nameof(IsDownloading))]
    private void CancelDownload() => _download?.Cancel();

    [RelayCommand]
    private void UseLocalModel() => UseLocal(RecommendedModel);

    private async Task DownloadCoreAsync(LocalModel model)
    {
        IsDownloading = true;
        DownloadError = null;
        DownloadFraction = 0;
        DownloadText = string.Format(CultureInfo.CurrentCulture, Strings.DownloadingModel, model.DisplayName);
        using var cancellation = new CancellationTokenSource();
        _download = cancellation;
        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadFraction = p.Fraction;
            string amount = $"{ByteSize.Format(p.Done)} / {ByteSize.Format(p.Total)}";
            DownloadText = p.Fraction >= 1 ? Strings.VerifyingModel
                : p.Remaining is { } left ? string.Format(CultureInfo.CurrentCulture, Strings.DownloadLeft, amount, (long)left.TotalSeconds)
                : amount;
        });
        try
        {
            await _services.Download(model, _services.Models.FolderOf(model), progress, cancellation.Token);
            UseLocal(model);
        }
        catch (OperationCanceledException)
        {
            DownloadText = "";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or UnauthorizedAccessException)
        {
            DownloadError = string.Format(CultureInfo.CurrentCulture, Strings.DownloadFailed, ex.Message);
        }
        finally
        {
            _download = null;
            IsDownloading = false;
            OnPropertyChanged(nameof(IsInstalled));
        }
    }

    private void UseLocal(LocalModel model)
    {
        var profile = new AssistantProfile("local-" + model.Id, AssistantBackend.Local, model.DisplayName, model.Id);
        AddProfile(profile);
    }

    [RelayCommand]
    private void SelectBackend(AssistantBackend backend)
    {
        CloudBackend = backend;
        ProfileName = BackendLabel(backend);
        ModelId = backend switch
        {
            AssistantBackend.OpenAiCompatible => "gpt-4.1-mini",
            AssistantBackend.Anthropic => "claude-haiku-4-5",
            AssistantBackend.Gemini => "gemini-flash-latest",
            _ => "",
        };
        Endpoint = "";
        TestResult = null;
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnection()
    {
        IsTesting = true;
        TestResult = Strings.Testing;
        try
        {
            using IChatClient client = _services.CreateClient(DraftProfile("test"), ApiKey.Trim());
            ConnectionResult result = await ConnectionTester.TestAsync(client, CancellationToken.None);
            TestOk = result.Ok;
            TestResult = result.Ok
                ? string.Format(CultureInfo.CurrentCulture, Strings.TestOk, result.Reply, (long)result.Latency.TotalMilliseconds)
                : $"{new AskMessageViewModel(false) { Error = result.Error }.ErrorText} {result.Error?.Detail}".Trim();
        }
        catch (ArgumentException ex)
        {
            TestOk = false;
            TestResult = ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool CanTest() => !IsTesting && CanSaveCloudProfile();

    [RelayCommand(CanExecute = nameof(CanSaveCloudProfile))]
    private void SaveCloudProfile()
    {
        string id = "cloud-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            _services.Keys.Save(id, ApiKey.Trim());
        }
        catch (InvalidOperationException ex)
        {
            // No keyring to keep the key in: say so, store nothing, and keep what was typed.
            TestResult = ex.Message;
            return;
        }

        ApiKey = "";
        AddProfile(DraftProfile(id));
        TestResult = null;
    }

    private bool CanSaveCloudProfile() => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ModelId);

    private AssistantProfile DraftProfile(string id) => new(
        id,
        CloudBackend,
        string.IsNullOrWhiteSpace(ProfileName) ? BackendLabel(CloudBackend) : ProfileName.Trim(),
        ModelId.Trim(),
        string.IsNullOrWhiteSpace(Endpoint) ? null : Endpoint.Trim());

    private void AddProfile(AssistantProfile profile)
    {
        AssistantSettings settings = _host.AssistantSettings;
        _host.SaveAssistantSettings(settings with
        {
            Profiles = [.. settings.Profiles.Where(p => p.Id != profile.Id), profile],
            DefaultProfileId = profile.Id,
        });
        NewConversation();
        RefreshLists();
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(IsCloudProfile));
        OnPropertyChanged(nameof(ShowPreview));
    }

    public static string BackendLabel(AssistantBackend backend) => backend switch
    {
        AssistantBackend.Anthropic => Strings.BackendAnthropic,
        AssistantBackend.Gemini => Strings.BackendGemini,
        AssistantBackend.AzureOpenAi => Strings.BackendAzure,
        AssistantBackend.Local => Strings.SetupLocalTitle,
        _ => Strings.BackendOpenAi,
    };

    // ── History and settings ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenConversation(ConversationRowViewModel row)
    {
        if (_services.Conversations.Load(row.Id) is not { } conversation)
        {
            return;
        }

        Messages.Clear();
        for (int i = 0; i < conversation.Turns.Count; i++)
        {
            ConversationTurn turn = conversation.Turns[i];
            bool isUser = turn.Role == "user";
            var message = new AskMessageViewModel(isUser, turn.Text) { Blocked = turn.Blocked, TurnIndex = isUser ? null : i, Helpful = turn.Helpful };
            message.Finish();
            Messages.Add(message);
        }

        _conversationId = row.Id;
        _previewScope = Guid.NewGuid();
        Mode = AskMode.Chat;
        OnPropertyChanged(nameof(ShowStarters));
    }

    [RelayCommand]
    private void DeleteAllConversations()
    {
        if (!ConfirmingDelete)
        {
            ConfirmingDelete = true;
            return;
        }

        _services.Conversations.DeleteAll();
        ConfirmingDelete = false;
        _conversationId = null;
        Messages.Clear();
        RefreshLists();
        _host.ShowToast(Strings.ConversationsDeleted);
    }

    [RelayCommand]
    private void MakeDefault(ProfileRowViewModel row)
    {
        _host.SaveAssistantSettings(_host.AssistantSettings with { DefaultProfileId = row.Profile.Id });
        NewConversation();
        RefreshLists();
        Mode = AskMode.Settings;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void RemoveProfile(ProfileRowViewModel row)
    {
        if (!row.Profile.IsLocal)
        {
            _services.Keys.Delete(row.Profile.Id);
        }

        AssistantSettings settings = _host.AssistantSettings;
        var remaining = settings.Profiles.Where(p => p.Id != row.Profile.Id).ToList();
        _host.SaveAssistantSettings(settings with
        {
            Profiles = remaining,
            DefaultProfileId = settings.DefaultProfileId == row.Profile.Id ? remaining.FirstOrDefault()?.Id : settings.DefaultProfileId,
            HidePreviewFor = [.. settings.HidePreviewFor.Where(id => id != row.Profile.Id)],
        });
        RefreshLists();
        Mode = remaining.Count == 0 ? AskMode.Setup : AskMode.Settings;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void AddAnother() => Mode = AskMode.Setup;

    [RelayCommand]
    private void SaveCap()
    {
        if (Profile is not { IsLocal: false } profile)
        {
            return;
        }

        long? cap = long.TryParse(CapText.Replace(".", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Trim(), out long value) && value > 0 ? value : null;
        AssistantSettings settings = _host.AssistantSettings;
        _host.SaveAssistantSettings(settings with
        {
            Profiles = [.. settings.Profiles.Select(p => p.Id == profile.Id ? p with { MonthlyTokenCap = cap } : p)],
        });
        CapText = cap?.ToString("N0", CultureInfo.CurrentCulture) ?? "";
        UpdateUsageNote();
        RefreshLists();
    }

    [RelayCommand]
    private void RemoveModel(ModelRowViewModel row)
    {
        long size = _services.Models.SizeOnDisk(row.Model);
        foreach (var local in _host.AssistantSettings.Profiles.Where(p => p.IsLocal && p.ModelId == row.Model.Id).ToList())
        {
            RemoveProfile(new ProfileRowViewModel(local, false, ""));
        }

        _services.Models.Remove(row.Model);
        _host.ShowToast(string.Format(CultureInfo.CurrentCulture, Strings.ModelRemoved, ByteSize.Format(size)));
        RefreshLists();
        OnPropertyChanged(nameof(IsInstalled));
    }

    private void RefreshLists()
    {
        Conversations = [.. _services.Conversations.List().Select(c => new ConversationRowViewModel(c))];
        AssistantSettings settings = _host.AssistantSettings;
        ProfileRows = [.. settings.Profiles.Select(p => new ProfileRowViewModel(p, p.Id == settings.Default?.Id, BackendLabel(p.Backend)))];
        ModelRows = [.. _services.Catalog.Models.Where(m => _services.Models.SizeOnDisk(m) > 0).Select(m => new ModelRowViewModel(m, _services.Models.SizeOnDisk(m)))];
        CapText = Profile?.MonthlyTokenCap?.ToString("N0", CultureInfo.CurrentCulture) ?? "";
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(IsCloudProfile));
    }
}

public sealed record ConversationRowViewModel(ConversationInfo Info)
{
    public string Id => Info.Id;

    public string Title => Info.Title;

    public string Detail => $"{Info.LastActivity.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)} · {string.Format(CultureInfo.CurrentCulture, Strings.HistoryTurns, Info.TurnCount)}";
}

public sealed record ProfileRowViewModel(AssistantProfile Profile, bool IsDefault, string BackendLabel)
{
    public string Name => Profile.DisplayName;

    public string Detail => Profile.IsLocal ? BackendLabel : $"{BackendLabel} · {Profile.ModelId}";

    public bool CanMakeDefault => !IsDefault;
}

public sealed record ModelRowViewModel(LocalModel Model, long SizeOnDisk)
{
    public string Text => string.Format(CultureInfo.CurrentCulture, Strings.LocalModelOnDisk, $"{Model.DisplayName}, {ByteSize.Format(SizeOnDisk)}");
}

/// <summary>"What I'm about to send": the exact instructions, facts and question, before a cloud request.</summary>
public sealed partial class SendPreviewDialogViewModel(AssistantProfile profile, PreparedQuestion prepared, Action<bool> send, Action cancel) : ObservableObject
{
    public string Subtitle => string.Format(CultureInfo.CurrentCulture, Strings.SendPreviewSub, profile.DisplayName);

    public string Instructions => prepared.SentPrompt;

    public string Question => prepared.SentQuestion;

    [ObservableProperty]
    public partial bool DontShowAgain { get; set; }

    [RelayCommand]
    private void Send() => send(DontShowAgain);

    [RelayCommand]
    private void Cancel() => cancel();
}
