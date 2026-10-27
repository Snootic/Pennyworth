using Pennyworth.App.Localization;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.Core.Storage;
using Pennyworth.Monitoring;
using Pennyworth.Platform;
using Pennyworth.Scanning;
using static Pennyworth.App.Tests.SampleTrees;

namespace Pennyworth.App.Tests;

public class MainWindowViewModelTests
{
    private static (MainWindowViewModel ViewModel, FakeScanner Scanner) Create(MemorySettings? settings = null)
    {
        var scanner = new FakeScanner(_ => Build());
        return (new MainWindowViewModel(TestApp.Services(scanner, settings: settings)), scanner);
    }

    [Fact]
    public async Task A_failed_scan_shows_a_message_instead_of_an_empty_screen()
    {
        var scanner = new FakeScanner(_ => throw new IOException("The drive went away."));
        var viewModel = new MainWindowViewModel(TestApp.Services(scanner));

        viewModel.Initialize();
        await viewModel.CurrentScan;

        Assert.False(viewModel.IsScanning);
        string toast = Assert.IsType<string>(viewModel.Toast);
        Assert.Contains("C:", toast, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_sample_does_not_escape_the_tick()
    {
        var viewModel = new MainWindowViewModel(TestApp.Services(monitor: new ThrowingMonitor()));

        await viewModel.TickAsync();

        Assert.False(viewModel.Live.HasSample);
    }

    [Fact]
    public async Task A_failed_rescan_keeps_the_summary_that_was_on_screen()
    {
        var results = new Queue<Func<StorageTree>>([() => Build(), () => throw new IOException("The drive went away.")]);
        var scanner = new FakeScanner(_ => results.Dequeue()());
        var viewModel = new MainWindowViewModel(TestApp.Services(scanner));
        viewModel.Initialize();
        await viewModel.CurrentScan;
        var shown = Assert.IsType<StorageSummaryViewModel>(viewModel.CurrentSummary);
        Assert.True(shown.IsComplete);

        viewModel.RescanCommand.Execute(null);
        await viewModel.CurrentScan;

        Assert.Same(shown, viewModel.CurrentSummary);
        Assert.True(shown.IsComplete);
        Assert.True(viewModel.HasToast);
    }

    [Fact]
    public async Task A_failed_scan_can_be_retried()
    {
        var scanner = new FakeScanner(_ => throw new IOException("The drive went away."));
        var viewModel = new MainWindowViewModel(TestApp.Services(scanner));
        viewModel.Initialize();
        await viewModel.CurrentScan;

        viewModel.RescanCommand.Execute(null);
        await viewModel.CurrentScan;

        Assert.Equal(2, scanner.ScannedRoots.Count);
    }

    [Fact]
    public async Task Starts_on_home_and_scans_the_windows_drive()
    {
        var (viewModel, scanner) = Create();

        viewModel.Initialize();
        await viewModel.CurrentScan;

        Assert.Equal(Page.Home, viewModel.Page);
        Assert.Same(viewModel.Home, viewModel.MainContent);
        Assert.Equal(2, viewModel.Drives.Count);
        Assert.Equal(@"C:\", viewModel.SelectedDrive?.Summary.RootPath);
        Assert.Equal([@"C:\"], scanner.ScannedRoots);
        Assert.True(viewModel.SystemSummary?.IsComplete);
        Assert.False(viewModel.IsScanning);
        Assert.Equal("Games use 50.0 GB", viewModel.Home.TeaserLine);
    }

    [Fact]
    public async Task First_run_shows_the_welcome_until_analyze()
    {
        var settings = new MemorySettings(new AppSettings { Language = LanguagePreference.English });
        var (viewModel, _) = Create(settings);
        viewModel.Initialize();

        Assert.Equal(Page.Welcome, viewModel.Page);
        Assert.False(viewModel.ShowSidebar);

        viewModel.Welcome.AnalyzeCommand.Execute(null);
        await viewModel.CurrentScan;

        Assert.Equal(Page.Storage, viewModel.Page);
        Assert.IsType<StorageSummaryViewModel>(viewModel.CurrentPage);
        Assert.True(settings.Current.WelcomeSeen);
    }

    [Fact]
    public async Task Reuses_finished_scans_when_switching_back()
    {
        var (viewModel, scanner) = Create();
        viewModel.Initialize();
        await viewModel.CurrentScan;

        viewModel.SelectedDrive = viewModel.Drives.Single(d => d.Summary.RootPath == @"D:\");
        await viewModel.CurrentScan;
        viewModel.SelectedDrive = viewModel.Drives.Single(d => d.Summary.RootPath == @"C:\");
        await viewModel.CurrentScan;

        Assert.Equal([@"C:\", @"D:\"], scanner.ScannedRoots);
    }

    [Fact]
    public async Task Rescan_scans_again()
    {
        var (viewModel, scanner) = Create();
        viewModel.Initialize();
        await viewModel.CurrentScan;

        viewModel.RescanCommand.Execute(null);
        await viewModel.CurrentScan;

        Assert.Equal([@"C:\", @"C:\"], scanner.ScannedRoots);
    }

    [Fact]
    public async Task Switching_drives_cancels_the_running_scan()
    {
        var (viewModel, scanner) = Create();
        scanner.Gate = new TaskCompletionSource();
        viewModel.Initialize();
        Task firstScan = viewModel.CurrentScan;
        Assert.True(viewModel.IsScanning);

        scanner.Gate = null;
        viewModel.ShowDrive(viewModel.Drives.Single(d => d.Summary.RootPath == @"D:\"));
        await viewModel.CurrentScan;
        await firstScan;

        var summary = Assert.IsType<StorageSummaryViewModel>(viewModel.CurrentPage);
        Assert.Equal(@"D:\", summary.Drive.RootPath);
        Assert.False(viewModel.IsScanning);
    }

    [Fact]
    public async Task Browse_all_opens_folders_and_the_sidebar_comes_back_to_the_summary()
    {
        var (viewModel, _) = Create();
        viewModel.Initialize();
        await viewModel.CurrentScan;
        viewModel.ShowStorageCommand.Execute(null);
        var summary = Assert.IsType<StorageSummaryViewModel>(viewModel.CurrentPage);

        summary.BrowseAllCommand.Execute(null);
        var browser = Assert.IsType<FolderBrowserViewModel>(viewModel.CurrentPage);
        Assert.Equal(@"C:\", browser.Current?.FullPath);
        Assert.True(viewModel.IsFoldersPage);

        viewModel.ShowStorageCommand.Execute(null);

        Assert.Same(summary, viewModel.CurrentPage);
    }

    [Fact]
    public async Task Folders_opens_this_pc_until_the_drive_is_scanned()
    {
        var (viewModel, scanner) = Create();
        scanner.Gate = new TaskCompletionSource();
        viewModel.Initialize();

        viewModel.ShowFoldersCommand.Execute(null);
        Assert.True(viewModel.Folders.IsThisPc);

        scanner.Gate.SetResult();
        await viewModel.CurrentScan;
        viewModel.GoHomeCommand.Execute(null);
        viewModel.ShowFoldersCommand.Execute(null);

        Assert.Equal(@"C:\", viewModel.Folders.Current?.FullPath);
    }

    [Fact]
    public async Task Keeps_the_drive_when_a_list_clears_its_selection()
    {
        var (viewModel, scanner) = Create();
        viewModel.Initialize();
        await viewModel.CurrentScan;

        viewModel.SelectedDrive = null; // what a ListBox does when its page unloads

        Assert.Equal(@"C:\", viewModel.SelectedDrive?.Summary.RootPath);
        Assert.Equal([@"C:\"], scanner.ScannedRoots);
    }

    [Fact]
    public void Three_d_mode_replaces_the_pages_until_a_page_is_chosen()
    {
        var (viewModel, _) = Create();
        viewModel.Initialize();

        viewModel.Set3dModeCommand.Execute(null);
        Assert.Same(viewModel.Pc, viewModel.MainContent);
        Assert.False(viewModel.ShowSidebar);

        viewModel.Pc.OpenMemoryCommand.Execute(null);

        Assert.False(viewModel.Is3d);
        Assert.Same(viewModel.Memory, viewModel.MainContent);
    }

    [Theory]
    [InlineData(false, "Light")]
    [InlineData(true, "Dark")]
    public void The_system_theme_is_used_until_one_is_chosen(bool systemIsDark, string label)
    {
        var settings = new MemorySettings();
        var (viewModel, _) = Create(settings);
        Assert.Null(viewModel.IsDarkTheme);

        viewModel.SyncSystemTheme(systemIsDark);

        Assert.Equal(systemIsDark, viewModel.IsDarkTheme);
        Assert.Equal(label, viewModel.ThemeLabel);
        Assert.Equal(ThemePreference.System, settings.Current.Theme);
    }

    [Fact]
    public void Theme_choice_is_saved()
    {
        var settings = new MemorySettings();
        var (viewModel, _) = Create(settings);
        viewModel.SyncSystemTheme(isDark: true);

        viewModel.ToggleThemeCommand.Execute(null);

        Assert.False(viewModel.IsDarkTheme);
        Assert.Equal(ThemePreference.Light, settings.Current.Theme);
    }

    [Fact]
    public async Task Unlocking_adds_the_hidden_space_to_the_summary()
    {
        var report = new HiddenSpaceReport(RestorePoints: 3 * Gb, NtfsMetadata: 1 * Gb);
        var hidden = new FakeHiddenSpace(new UnlockResult(UnlockOutcome.Measured, report));
        var viewModel = new MainWindowViewModel(TestApp.Services(hidden: hidden));
        viewModel.Initialize();
        await viewModel.CurrentScan;
        long before = viewModel.SystemSummary!.Unaccounted.Bytes;

        viewModel.RequestUnlock();
        var dialog = Assert.IsType<UnlockDialogViewModel>(viewModel.Dialog);
        dialog.UnlockCommand.Execute(null);
        await dialog.Run;

        Assert.True(viewModel.IsUnlocked);
        Assert.Null(viewModel.Dialog);
        Assert.NotNull(viewModel.Toast);
        var summary = viewModel.SystemSummary;
        Assert.Equal(before - (4 * Gb), summary.Unaccounted.Bytes);
        Assert.Equal(StorageCategory.HiddenSystem, summary.SelectedCategory?.Category);
        Assert.Contains(summary.SelectedCategory!.Areas, area => area.Bytes == 3 * Gb && area.Node is null);
        Assert.False(viewModel.Home.ShowUnlockBanner);
    }

    [Fact]
    public async Task Declining_unlock_keeps_normal_mode()
    {
        var viewModel = new MainWindowViewModel(TestApp.Services(hidden: new FakeHiddenSpace(new UnlockResult(UnlockOutcome.Declined))));
        viewModel.Initialize();

        viewModel.RequestUnlock();
        var dialog = Assert.IsType<UnlockDialogViewModel>(viewModel.Dialog);
        dialog.UnlockCommand.Execute(null);
        await dialog.Run;

        Assert.True(dialog.IsDeclined);
        Assert.False(viewModel.IsUnlocked);
        dialog.CloseCommand.Execute(null);
        Assert.Null(viewModel.Dialog);
    }

    [Fact]
    public async Task Ticks_fill_live_numbers_and_the_app_list()
    {
        var app = new AppGroup("chrome", null, "Google Chrome", "Google LLC", null, 2 * Gb, 4.0,
            [new AppProcess(10, "chrome", Gb, 3, []), new AppProcess(11, "chrome", Gb, 1, [])]);
        var sample = new MonitorSample(new MemoryStatus(16 * Gb, 5 * Gb, 3 * Gb, Gb), 12.4, 30, [app]);
        var viewModel = new MainWindowViewModel(TestApp.Services(monitor: new FakeMonitor(sample)));

        await viewModel.TickAsync();

        Assert.Equal("CPU 12%", viewModel.CpuStatus);
        Assert.Equal("16 GB", viewModel.Live.MemoryTotalText);
        Assert.Equal(12.4, viewModel.Live.CpuHistory[^1]);
        var row = Assert.Single(viewModel.Memory.Apps);
        Assert.Equal("Google Chrome", row.Name);
        Assert.Equal("Google Chrome 2.00 GB", viewModel.Home.TopAppLine);
    }

    [Fact]
    public void Drive_names_use_label_or_local_disk()
    {
        Assert.Equal("Games (D:)", new DriveViewModel(TestApp.DriveD).Name);
        Assert.EndsWith("(C:)", new DriveViewModel(TestApp.DriveC).Name);
        Assert.Equal(0.5, new DriveViewModel(TestApp.DriveC).UsedFraction, 3);
    }

    [Theory]
    [InlineData(@"C:\", "", "C:", "Local Disk (C:)", "Local Disk")]
    [InlineData("/", "/", "/", "/", "")]
    [InlineData("/home", "/home", "/home", "/home", "")]
    [InlineData("/data", "Backups", "/data", "Backups (/data)", "Backups")]
    public void Drives_are_named_by_their_root_without_repeating_it(string root, string label, string token, string name, string caption)
    {
        var drive = new DriveViewModel(new DriveSummary(root, label, 100, 50));

        Assert.Equal(token, drive.Token);
        Assert.Equal(name, drive.Name);
        Assert.Equal(caption, drive.Caption);
    }

    [Fact]
    public void The_window_extends_into_the_title_bar_unless_the_platform_says_not_to()
    {
        Assert.True(new MainWindowViewModel(TestApp.Services()).ExtendsIntoTitleBar);
        Assert.False(new MainWindowViewModel(TestApp.Services() with { ExtendsIntoTitleBar = false }).ExtendsIntoTitleBar);
    }

    [Fact]
    public void Drives_show_letter_label_and_percent_used()
    {
        var drive = new DriveViewModel(TestApp.DriveD);

        Assert.Equal("D:", drive.Token);
        Assert.Equal("Games", drive.Label);
        Assert.Equal(new DriveViewModel(TestApp.DriveC).UsedFraction.ToString("P0"), new DriveViewModel(TestApp.DriveC).UsedPercentText);
    }
}

[Collection(nameof(LanguageSwitching))]
public sealed class ShellLanguageTests : IDisposable
{
    public void Dispose() => Localizer.Instance.SetLanguage(LanguagePreference.English);

    [Fact]
    public void Switching_language_updates_texts_and_is_saved()
    {
        var settings = new MemorySettings();
        var viewModel = new MainWindowViewModel(TestApp.Services(settings: settings));
        viewModel.Initialize();
        Assert.True(viewModel.IsEnglish);

        viewModel.SetPortugueseCommand.Execute(null);

        Assert.True(viewModel.IsPortuguese);
        Assert.Equal(LanguagePreference.Portuguese, settings.Current.Language);
        Assert.Equal("Disco Local (C:)", viewModel.SystemDrive?.Name);
        Assert.Equal("Pronto", viewModel.StatusShort);
    }
}

/// <summary>A monitor that fails the way a driver can: not with the exception the shell expects.</summary>
internal sealed class ThrowingMonitor : ISystemMonitor
{
    public MonitorSample Sample() => throw new IOException("The counter went away.");
}
