using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.Core.Formatting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>First run: who Pennyworth is, a privacy note, the language, and "Analyze my PC".</summary>
public sealed partial class WelcomeViewModel(MainWindowViewModel shell) : ObservableObject
{
    /// <summary>How many boot lines are on screen; the view advances it every 300 ms.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BootScreen))]
    public partial int BootStep { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BootScreen))]
    public partial bool CursorOn { get; private set; }

    public string Title => string.Format(Strings.WelcomeTitle, shell.Greeting);

    public IReadOnlyList<WelcomePoint> Points =>
    [
        new("01", Strings.WelcomePoint1),
        new("02", Strings.WelcomePoint2),
        new("03", Strings.WelcomePoint3),
    ];

    public IReadOnlyList<LanguageOptionViewModel> Languages =>
    [
        new(LanguagePreference.System, Strings.LanguageSystem, shell.Language == LanguagePreference.System),
        new(LanguagePreference.English, "English", shell.Language == LanguagePreference.English),
        new(LanguagePreference.Portuguese, "Português", shell.Language == LanguagePreference.Portuguese),
    ];

    public IReadOnlyList<string> BootLines
    {
        get
        {
            string drives = string.Join("  ", shell.Drives.Select(d => d.Token));
            var system = shell.SystemDrive;
            return
            [
                "PENNYWORTH 0.1",
                "",
                "> " + shell.Greeting.ToLowerInvariant(),
                Ascii.Leader(Strings.BootDrives, drives),
                Ascii.Leader(string.Format(Strings.BootUsedOn, shell.SystemDriveToken), system is null ? "—" : ByteSize.Format(system.Summary.UsedBytes)),
                Ascii.Leader(Strings.BootMemory, shell.Live.HasSample ? shell.Live.MemoryTotalText : "—"),
                Ascii.Leader(Strings.BootAccount, Strings.BootAccountValue),
                "> " + Strings.BootReady,
            ];
        }
    }

    public string BootScreen => Ascii.BootScreen(BootLines, BootStep, CursorOn);

    public string BitsBlock => Ascii.BitsBlock(shell.Live.Bits);

    /// <summary>Shows the next boot line and blinks the cursor.</summary>
    public void AdvanceBoot()
    {
        if (BootStep < BootLines.Count + 1)
        {
            BootStep++;
        }

        CursorOn = !CursorOn;
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void SelectLanguage(LanguageOptionViewModel option) => shell.SetLanguage(option.Preference);

    [RelayCommand]
    private void Analyze() => shell.Analyze();
}

public sealed record WelcomePoint(string Index, string Text);

public sealed record LanguageOptionViewModel(LanguagePreference Preference, string Label, bool IsSelected);
