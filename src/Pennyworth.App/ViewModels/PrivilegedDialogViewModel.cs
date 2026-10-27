using Pennyworth.App.Resources;
using Pennyworth.Core.Privilege;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

public enum PrivilegedDialogState
{
    /// <summary>Says what will be read and asks the person to go ahead.</summary>
    Asking,

    /// <summary>The system's password prompt is open.</summary>
    Waiting,

    /// <summary>The prompt was dismissed: nothing was done, and the person may ask again.</summary>
    Cancelled,

    /// <summary>There is no way to ask on this machine; the sentence says why.</summary>
    Unavailable,

    /// <summary>It was asked for and did not work.</summary>
    Failed,
}

/// <summary>
/// A read-only request that needs an administrator's password: first what will be read and what will not be touched,
/// then the wait for the system's own prompt, then a calm message when it did not happen. One shape for every such
/// request, so the window has a single template; each subclass says its own words and what pressing the button runs.
/// </summary>
public abstract partial class PrivilegedDialogViewModel(MainWindowViewModel shell) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking), nameof(IsIdle), nameof(IsWaiting), nameof(IsMessage), nameof(Message))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial PrivilegedDialogState State { get; private set; }

    /// <summary>Why the request cannot be made, with <see cref="PrivilegedDialogState.Unavailable"/>.</summary>
    public PrivilegedUnavailableReason? Reason { get; private set; }

    /// <summary>The request started by <see cref="StartCommand"/>; lets tests await it.</summary>
    public Task Run { get; private set; } = Task.CompletedTask;

    public abstract string Title { get; }

    /// <summary>Why this is being asked, and that the system (not the app) takes the password.</summary>
    public abstract string Why { get; }

    /// <summary>What will be read.</summary>
    public abstract string Reads { get; }

    /// <summary>What will not be touched.</summary>
    public abstract string Wont { get; }

    /// <summary>Asking or waiting: the explanation is on screen.</summary>
    public bool IsAsking => State is PrivilegedDialogState.Asking or PrivilegedDialogState.Waiting;

    /// <summary>Asking and not yet pressed: the buttons that begin or decline are shown.</summary>
    public bool IsIdle => State == PrivilegedDialogState.Asking;

    public bool IsWaiting => State == PrivilegedDialogState.Waiting;

    /// <summary>Cancelled, unavailable or failed: the one message is on screen.</summary>
    public bool IsMessage => State is PrivilegedDialogState.Cancelled or PrivilegedDialogState.Unavailable or PrivilegedDialogState.Failed;

    /// <summary>The calm sentence for the non-asking states; null while asking.</summary>
    public string? Message => State switch
    {
        PrivilegedDialogState.Cancelled => Strings.PrivCancelled,
        PrivilegedDialogState.Unavailable => PrivilegedText.Reason(Reason),
        PrivilegedDialogState.Failed => Strings.PrivFailed,
        _ => null,
    };

    /// <summary>The shell that does the work and closes the dialog.</summary>
    protected MainWindowViewModel Shell => shell;

    /// <summary>Starts the request and returns the work to await; called with the state already <see cref="PrivilegedDialogState.Waiting"/>.</summary>
    protected abstract Task StartAsync();

    /// <summary>Re-reads the texts after a language switch.</summary>
    internal void RefreshTexts() => OnPropertyChanged(string.Empty);

    internal void Cancelled() => State = PrivilegedDialogState.Cancelled;

    internal void Failed() => State = PrivilegedDialogState.Failed;

    internal void Unavailable(PrivilegedUnavailableReason? reason)
    {
        Reason = reason;
        State = PrivilegedDialogState.Unavailable;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Start()
    {
        State = PrivilegedDialogState.Waiting;
        Run = StartAsync();
    }

    /// <summary>Back to the explanation after a cancelled or failed attempt.</summary>
    [RelayCommand]
    private void TryAgain() => State = PrivilegedDialogState.Asking;

    [RelayCommand]
    private void NotNow() => shell.CloseDialog();

    [RelayCommand]
    private void Close() => shell.CloseDialog();
}

/// <summary>"Include what needs administrator access?": snapshots and file system bookkeeping of the system drive.</summary>
public sealed class AdminSpaceDialogViewModel(MainWindowViewModel shell) : PrivilegedDialogViewModel(shell)
{
    public override string Title => Strings.AdminSpaceTitle;

    public override string Why => Strings.AdminSpaceWhy;

    public override string Reads => Strings.AdminSpaceReads;

    public override string Wont => Strings.AdminSpaceWont;

    protected override Task StartAsync() => Shell.IncludeAdminSpaceAsync(this);
}

/// <summary>"Measure the folders I couldn't read?": the size, and only the size, of what a person cannot list.</summary>
/// <param name="summary">The drive whose unreadable folders are measured.</param>
public sealed class LockedMeasureDialogViewModel(MainWindowViewModel shell, StorageSummaryViewModel summary) : PrivilegedDialogViewModel(shell)
{
    public StorageSummaryViewModel Summary { get; } = summary;

    public override string Title => Strings.LockedMeasureTitle;

    public override string Why => Strings.LockedMeasureWhy;

    public override string Reads => Strings.LockedMeasureReads;

    public override string Wont => Strings.LockedMeasureWont;

    protected override Task StartAsync() => Shell.MeasureLockedAsync(this);
}
