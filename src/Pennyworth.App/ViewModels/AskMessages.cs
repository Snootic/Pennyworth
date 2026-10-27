using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.Assistant;
using Pennyworth.Assistant.Providers;

namespace Pennyworth.App.ViewModels;

/// <summary>One bubble in the conversation.</summary>
public sealed partial class AskMessageViewModel : ObservableObject
{
    private readonly StringBuilder _text = new();

    public AskMessageViewModel(bool isUser, string text = "")
    {
        IsUser = isUser;
        _text.Append(text);
    }

    public bool IsUser { get; }

    public bool IsAssistant => !IsUser;

    public string Author => IsUser ? Strings.You : "Pennyworth";

    public string Text => _text.ToString();

    public bool HasText => _text.Length > 0;

    /// <summary>Waiting for the first words (and, locally, for the model to load).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThinking))]
    public partial bool IsStreaming { get; set; }

    public bool ShowThinking => IsStreaming && !HasText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAction))]
    public partial AskActionViewModel? Action { get; set; }

    public bool HasAction => Action is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlocked))]
    public partial int Blocked { get; set; }

    public bool HasBlocked => Blocked > 0;

    [ObservableProperty]
    public partial bool StoppedRepeating { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokens), nameof(TokensText))]
    public partial long Tokens { get; set; }

    public bool HasTokens => Tokens > 0;

    public string TokensText => string.Format(CultureInfo.CurrentCulture, Strings.TokensUsed, Tokens);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ErrorText), nameof(ErrorDetail))]
    public partial AssistantError? Error { get; set; }

    public bool HasError => Error is not null;

    public string ErrorText => Error?.Kind switch
    {
        null => "",
        AssistantErrorKind.BadKey => Strings.ErrorBadKey,
        AssistantErrorKind.UnknownModel => Strings.ErrorUnknownModel,
        AssistantErrorKind.Quota => Strings.ErrorQuota,
        AssistantErrorKind.Network => Strings.ErrorNetwork,
        AssistantErrorKind.Timeout => Strings.ErrorTimeout,
        AssistantErrorKind.Server => Strings.ErrorServer,
        _ => Strings.ErrorUnknown,
    };

    public string ErrorDetail => Error?.Detail ?? "";

    /// <summary>Index of this answer in the stored conversation, for feedback.</summary>
    public int? TurnIndex { get; set; }

    /// <summary>True: helpful, false: not helpful, null: not rated yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRate), nameof(IsRated))]
    public partial bool? Helpful { get; set; }

    public bool CanRate => IsAssistant && !IsStreaming && HasText && Helpful is null && TurnIndex is not null;

    public bool IsRated => Helpful is not null;

    public void Append(string text)
    {
        _text.Append(text);
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(HasText));
        OnPropertyChanged(nameof(ShowThinking));
    }

    public void Finish()
    {
        IsStreaming = false;
        OnPropertyChanged(nameof(CanRate));
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A button in an answer that opens an app screen.</summary>
public sealed record AskActionViewModel(AssistantAction Action, string? AppName = null)
{
    public string Label => Action.Kind switch
    {
        ActionKind.Cleanup => Strings.ActionCleanup,
        ActionKind.CleanupItem => Strings.ActionCleanupItem,
        ActionKind.Memory => Strings.ActionMemory,
        ActionKind.CloseApp => string.Format(CultureInfo.CurrentCulture, Strings.ActionCloseApp, AppName ?? Action.Argument),
        ActionKind.Storage => Strings.ActionStorage,
        ActionKind.Folders => Strings.ActionFolders,
        ActionKind.Specs => Strings.ActionSpecs,
        _ => Localizer.Instance.Resolve(Strings.ActionUnlock),
    };
}
