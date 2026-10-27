using System.Globalization;
using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Assistant;
using Pennyworth.Cleanup;
using Pennyworth.Core.Formatting;
using Pennyworth.KnowledgeBase;
using Pennyworth.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

public enum CleanupPhase
{
    Measuring,
    Select,
    Cleaning,
    Done,
    Empty,
}

/// <summary>What the cleanup screen needs from the shell.</summary>
public interface IDialogHost
{
    void ShowDialog(object dialog);

    void CloseDialog();

    void ShowToast(string message);
}

/// <summary>
/// Guided cleanup: everything is measured first, only Safe items are pre-ticked, nothing is
/// deleted before the user confirms, and every run is logged.
/// </summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly ICleanupService _service;
    private readonly IDialogHost _host;
    private readonly CleanupHistory? _history;
    private readonly IExplorerLauncher? _explorer;
    private readonly Action _goHome;

    public CleanupViewModel(ICleanupService service, IDialogHost host, Action goHome, CleanupHistory? history = null, IExplorerLauncher? explorer = null)
    {
        _service = service;
        _host = host;
        _goHome = goHome;
        _history = history;
        _explorer = explorer;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(IsMeasuring), nameof(IsSelecting), nameof(IsCleaning), nameof(IsDone), nameof(IsEmpty))]
    public partial CleanupPhase Phase { get; private set; } = CleanupPhase.Measuring;

    public bool IsMeasuring => Phase == CleanupPhase.Measuring;

    public bool IsSelecting => Phase == CleanupPhase.Select;

    public bool IsCleaning => Phase == CleanupPhase.Cleaning;

    public bool IsDone => Phase == CleanupPhase.Done;

    public bool IsEmpty => Phase == CleanupPhase.Empty;

    /// <summary>The list of everything one item would delete, while the user is looking at it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewing))]
    public partial CleanupPreviewViewModel? Preview { get; private set; }

    public bool IsPreviewing => Preview is not null;

    /// <summary>Every rule that has something to clean, in knowledge-base order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Groups), nameof(TotalBytes), nameof(SafeBytes), nameof(Title))]
    public partial IReadOnlyList<CleanupItemViewModel> Items { get; private set; } = [];

    public IReadOnlyList<CleanupGroupViewModel> Groups => Enum.GetValues<SafetyLevel>()
        .Select(level => new CleanupGroupViewModel(level, Items.Where(item => item.Rule.Safety == level).ToArray()))
        .Where(group => group.Items.Count > 0)
        .ToArray();

    /// <summary>The items being cleaned, or that were cleaned in the last run.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CleanupItemViewModel> RunItems { get; private set; } = [];

    public long TotalBytes => Items.Sum(item => item.Bytes);

    /// <summary>What can be freed with only Safe items: the home screen's "I can safely free".</summary>
    public long SafeBytes => Items.Where(item => item.Rule.Safety == SafetyLevel.Safe && item.IsAvailable).Sum(item => item.Bytes);

    public long SelectedBytes => Items.Where(item => item.IsSelected).Sum(item => item.Bytes);

    public long ShaderBytes => Items.FirstOrDefault(item => item.Rule.Id == "shader")?.Bytes ?? 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial long FreedBytes { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkippedText))]
    public partial long SkippedFiles { get; private set; }

    public string Title => Phase switch
    {
        CleanupPhase.Measuring => Strings.Measuring,
        CleanupPhase.Cleaning => Strings.Cleaning,
        CleanupPhase.Done => string.Format(Strings.Freed, ByteSize.Format(FreedBytes)),
        CleanupPhase.Empty => Strings.NothingTitle,
        _ => string.Format(Strings.CanFreeTitle, ByteSize.Format(TotalBytes)),
    };

    public string Subtitle => Phase switch
    {
        CleanupPhase.Cleaning => Strings.CleaningSub,
        CleanupPhase.Done => Strings.DoneSub,
        CleanupPhase.Empty => Strings.NothingSub,
        _ => Strings.CleanSub,
    };

    public string SelectedLine => string.Format(Strings.SelectedOf, ByteSize.Format(SelectedBytes), ByteSize.Format(TotalBytes));

    public double SelectedFraction => TotalBytes == 0 ? 0 : (double)SelectedBytes / TotalBytes;

    public string CleanButtonText => string.Format(Strings.CleanButton, ByteSize.Format(SelectedBytes));

    public bool CanClean => SelectedBytes > 0 && Phase == CleanupPhase.Select;

    public string SkippedText => SkippedFiles > 0 ? string.Format(Strings.Skipped, SkippedFiles) : Strings.SkippedNone;

    /// <summary>The measurement started by <see cref="MeasureAsync"/>; lets tests await it.</summary>
    public Task CurrentMeasure { get; private set; } = Task.CompletedTask;

    /// <summary>Measures every rule (a dry run). Safe items with something to free are pre-ticked.</summary>
    public Task MeasureAsync()
    {
        CurrentMeasure = MeasureCoreAsync();
        return CurrentMeasure;
    }

    /// <summary>Re-reads every text after a language switch.</summary>
    public void RefreshTexts()
    {
        foreach (CleanupItemViewModel item in Items)
        {
            item.Refresh();
        }

        Preview?.Refresh();

        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void Toggle(CleanupItemViewModel item)
    {
        if (item.IsAvailable && Phase == CleanupPhase.Select)
        {
            item.IsSelected = !item.IsSelected;
            OnSelectionChanged();
        }
    }

    /// <summary>Shows every file (or Recycle Bin item) that cleaning <paramref name="item"/> would delete.</summary>
    [RelayCommand]
    private void OpenPreview(CleanupItemViewModel item)
    {
        if (Phase == CleanupPhase.Select)
        {
            Preview = new CleanupPreviewViewModel(item, this, _explorer);
        }
    }

    public void ClosePreview() => Preview = null;

    /// <summary>"Ask about this": set by the shell when the assistant is available.</summary>
    public Action<AssistantTopic, string>? AskAbout { get; set; }

    public bool CanAsk => AskAbout is not null;

    [RelayCommand]
    private void AskAboutItem(CleanupItemViewModel item) =>
        AskAbout?.Invoke(new AssistantTopic(TopicKind.CleanupItem, item.Rule.Id), string.Format(CultureInfo.CurrentCulture, Strings.AskAboutCleanup, item.Title));

    [RelayCommand]
    private void AskClean()
    {
        if (CanClean)
        {
            _host.ShowDialog(new CleanConfirmDialogViewModel(this, _host));
        }
    }

    /// <summary>Runs the selected items one at a time (called by the confirmation dialog).</summary>
    public async Task CleanSelectedAsync()
    {
        var selected = Items.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        Preview = null;
        RunItems = selected;
        FreedBytes = 0;
        SkippedFiles = 0;
        Phase = CleanupPhase.Cleaning;
        var results = new List<CleanupResult>();
        foreach (CleanupItemViewModel item in selected)
        {
            var progress = new Progress<double>(value => item.Progress = value);
            CleanupResult result = await _service.CleanAsync(item.Estimate, progress, CancellationToken.None);
            item.Progress = 1;
            item.FreedBytes = result.BytesFreed;
            FreedBytes += result.BytesFreed;
            SkippedFiles += result.Skipped;
            results.Add(result);
        }

        _history?.Append(results, DateTimeOffset.Now);
        Phase = CleanupPhase.Done;
    }

    [RelayCommand]
    private async Task Finish()
    {
        RunItems = [];
        await MeasureAsync();
    }

    [RelayCommand]
    private async Task BackHome()
    {
        _goHome();
        await Finish();
    }

    private async Task MeasureCoreAsync()
    {
        Preview = null;
        Phase = CleanupPhase.Measuring;
        var estimates = await Task.WhenAll(_service.Rules.Select(rule => _service.MeasureAsync(rule, CancellationToken.None)));
        Items = estimates
            .Where(estimate => estimate.Bytes > 0)
            .Select(estimate => new CleanupItemViewModel(estimate))
            .ToArray();
        Phase = Items.Count == 0 ? CleanupPhase.Empty : CleanupPhase.Select;
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedLine));
        OnPropertyChanged(nameof(SelectedFraction));
        OnPropertyChanged(nameof(CleanButtonText));
        OnPropertyChanged(nameof(CanClean));
        AskCleanCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class CleanupItemViewModel : ObservableObject
{
    private const int ProgressWidth = 24;

    public CleanupItemViewModel(CleanupEstimate estimate)
    {
        Estimate = estimate;
        Rule = estimate.Rule;
        Bytes = estimate.Bytes;
        Items = estimate.Items;
        IsAvailable = estimate.IsAvailable;
        IsSelected = IsAvailable && Rule.Safety == SafetyLevel.Safe;
    }

    /// <summary>The exact list cleaning will delete; nothing found after it was made.</summary>
    public CleanupEstimate Estimate { get; }

    public CleanupRule Rule { get; }

    public long Bytes { get; }

    /// <summary>Files found; for the Recycle Bin, the items in it.</summary>
    public long Items { get; }

    /// <summary>False for items that need admin rights.</summary>
    public bool IsAvailable { get; }

    public bool NeedsFullScan => !IsAvailable;

    public bool IsRecycleBin => Rule.Kind == CleanupKind.RecycleBin;

    /// <summary>"See what will be deleted (1,204 files)".</summary>
    public string PreviewText => string.Format(IsRecycleBin ? Strings.PreviewLinkBin : Strings.PreviewLink, Items);

    /// <summary>"1,204 files" (Recycle Bin: "12 items").</summary>
    public string CountText => string.Format(IsRecycleBin ? Strings.CountBin : Strings.CountFiles, Items);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string Title => Rule.Title.Get(CultureInfo.CurrentUICulture);

    public string Description => Rule.Description.Get(CultureInfo.CurrentUICulture);

    public string Consequence => Rule.Consequence.Get(CultureInfo.CurrentUICulture);

    public string SizeText => ByteSize.Format(Bytes);

    public string Glyph => CleanupGroupViewModel.GlyphOf(Rule.Safety);

    public bool IsThinkFirst => Rule.Safety == SafetyLevel.ThinkFirst;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressBar), nameof(ProgressText))]
    public partial double Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FreedText), nameof(ProgressText))]
    public partial long FreedBytes { get; set; }

    /// <summary>"[############............]".</summary>
    public string ProgressBar => "[" + Ascii.Bar(Progress, ProgressWidth) + "]";

    public string ProgressText => Progress >= 1 ? ByteSize.Format(FreedBytes) : $"{Math.Round(Progress * 100):0}%";

    public string FreedText => ByteSize.Format(FreedBytes);

    internal void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>Items of one safety level, under a badge ("● SAFE").</summary>
public sealed record CleanupGroupViewModel(SafetyLevel Safety, IReadOnlyList<CleanupItemViewModel> Items)
{
    public string Glyph => GlyphOf(Safety);

    public string Label => Safety switch
    {
        SafetyLevel.Safe => Strings.Safety_Safe,
        SafetyLevel.UsuallySafe => Strings.Safety_UsuallySafe,
        _ => Strings.Safety_ThinkFirst,
    };

    public string Hint => Safety switch
    {
        SafetyLevel.Safe => Localizer.Instance.Resolve(Strings.SafetyHint_Safe),
        SafetyLevel.UsuallySafe => Strings.SafetyHint_UsuallySafe,
        _ => Strings.SafetyHint_ThinkFirst,
    };

    public bool IsThinkFirst => Safety == SafetyLevel.ThinkFirst;

    public static string GlyphOf(SafetyLevel safety) => safety switch
    {
        SafetyLevel.Safe => "●",
        SafetyLevel.UsuallySafe => "◐",
        _ => "○",
    };
}

/// <summary>"Clean 18.2 GB?" with the list of what goes, and a note when the Recycle Bin is included.</summary>
public sealed partial class CleanConfirmDialogViewModel(CleanupViewModel cleanup, IDialogHost host) : ObservableObject
{
    public string Title => string.Format(Strings.CleanConfirmTitle, ByteSize.Format(cleanup.SelectedBytes));

    public IReadOnlyList<CleanupItemViewModel> Rows { get; } = cleanup.Items.Where(item => item.IsSelected).ToArray();

    public bool ShowBinNote => Rows.Any(row => row.Rule.Kind == CleanupKind.RecycleBin);

    public string BinNote => string.Format(Localizer.Instance.Resolve(Strings.BinNote), Rows.FirstOrDefault(row => row.Rule.Kind == CleanupKind.RecycleBin)?.Items ?? 0);

    /// <summary>"18.2 GB in 2,310 files", above the reminder that only the listed files go.</summary>
    public string CountLine => string.Format(Strings.ConfirmCount, ByteSize.Format(cleanup.SelectedBytes), Rows.Sum(row => row.Items));

    /// <summary>The run started by <see cref="ConfirmCommand"/>; lets tests await it.</summary>
    public Task Run { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void Cancel() => host.CloseDialog();

    /// <summary>Back to the cleanup screen, on the list of what <paramref name="row"/> deletes.</summary>
    [RelayCommand]
    private void Review(CleanupItemViewModel row)
    {
        host.CloseDialog();
        cleanup.OpenPreviewCommand.Execute(row);
    }

    [RelayCommand]
    private void Confirm()
    {
        host.CloseDialog();
        Run = cleanup.CleanSelectedAsync();
    }
}
