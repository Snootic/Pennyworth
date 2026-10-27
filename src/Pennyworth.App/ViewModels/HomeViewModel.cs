using Pennyworth.App.Resources;
using Pennyworth.Assistant;
using Pennyworth.Core.Formatting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>"Your PC, at a glance": drives, the storage teaser, what can be freed, and live memory/CPU.</summary>
public sealed partial class HomeViewModel(MainWindowViewModel shell) : ObservableObject
{
    private const double AlmostFull = 0.9;

    public MainWindowViewModel Shell => shell;

    public LiveViewModel Live => shell.Live;

    public string Greeting => shell.Greeting;

    public string StatusLine => shell.SystemDrive is { } system
        ? string.Format(system.UsedFraction > AlmostFull ? Strings.HomeStatusFull : Strings.HomeStatusOk, system.Token, system.UsedPercentText)
        : "";

    public IReadOnlyList<DriveCardViewModel> DriveCards => shell.Drives
        .Select(drive => new DriveCardViewModel(drive, drive.UsedFraction > AlmostFull))
        .ToArray();

    public string StorageLabel => $"{Strings.NavStorage} · {shell.SystemDrive?.Token}";

    /// <summary>"Apps use 413 GB", from the system drive's scan (live while it runs).</summary>
    public string TeaserLine => shell.SystemSummary?.Biggest is { } biggest
        ? string.Format(Strings.CategoryUses, biggest.Name, biggest.SizeText)
        : Strings.StatusScanning;

    public IReadOnlyList<BarSegment> TeaserSegments => shell.SystemSummary?.Segments ?? [];

    public string? TeaserHighlight => shell.SystemSummary?.Biggest?.ColorKey;

    public string SafeText => ByteSize.Format(shell.Cleanup.SafeBytes);

    public bool IsCleanupMeasured => !shell.Cleanup.IsMeasuring;

    /// <summary>"Chrome 2.1 GB": the app using the most memory.</summary>
    public string TopAppLine => shell.Memory.Apps.FirstOrDefault() is { } top ? $"{top.Name} {top.MemoryText}" : "—";

    /// <summary>The last 30 CPU readings for the small chart.</summary>
    public IReadOnlyList<double> CpuMini => shell.Live.CpuHistory.Skip(LiveViewModel.CpuHistoryLength - 30).ToArray();

    public bool ShowUnlockBanner => !shell.IsUnlocked;

    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Only what changes every second, so the drive cards aren't rebuilt.</summary>
    public void RefreshLive()
    {
        OnPropertyChanged(nameof(TopAppLine));
        OnPropertyChanged(nameof(CpuMini));
    }

    public bool CanAsk => shell.HasAssistant;

    [RelayCommand]
    private void AskAboutPc() => shell.AskAbout(AssistantTopic.Overview, Strings.AskAboutHome);

    [RelayCommand]
    private void OpenDrive(DriveCardViewModel card) => shell.ShowDrive(card.Drive);

    [RelayCommand]
    private void GoStorage()
    {
        if (shell.SystemDrive is { } system)
        {
            shell.ShowDrive(system);
        }
    }

    [RelayCommand]
    private void GoCleanup() => shell.GoCleanupCommand.Execute(null);

    [RelayCommand]
    private void GoMemory() => shell.GoMemoryCommand.Execute(null);

    [RelayCommand]
    private void Unlock() => shell.RequestUnlock();
}

public sealed record DriveCardViewModel(DriveViewModel Drive, bool IsAlmostFull)
{
    public string Token => Drive.Token;

    public string Caption => Drive.Caption;

    public string UsedLine => Drive.UsedText;

    public double Fraction => Drive.UsedFraction;

    public string PercentText => Drive.UsedPercentText;

    public string WarningText => IsAlmostFull ? "▲ " + Strings.AlmostFull.ToUpper(System.Globalization.CultureInfo.CurrentCulture) : "";
}
