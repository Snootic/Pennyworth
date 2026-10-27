using System.Collections.ObjectModel;
using System.Diagnostics;
using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Assistant;
using Pennyworth.Cleanup;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Hardware;
using Pennyworth.Platform;
using Pennyworth.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>Everything the app talks to; tests pass fakes.</summary>
public sealed record AppServices(
    IDriveService Drives,
    IStorageScanner Scanner,
    IPathClassifier Classifier,
    ILocationInfo Locations,
    IExplorerLauncher Explorer,
    ICleanupService Cleanup,
    IHiddenSpaceService HiddenSpace,
    ISettingsStore Settings,
    ISpecsReader Specs,
    ISystemMonitor? Monitor = null,
    IProcessCloser? Closer = null,
    CleanupHistory? CleanupHistory = null,
    Func<DateTimeOffset>? Clock = null,
    IClipboardService? Clipboard = null,
    AssistantServices? Assistant = null,
    IMemoryModuleReader? MemoryModules = null);

public enum Page
{
    Welcome,
    Home,
    Storage,
    Cleanup,
    Memory,
    Folders,
    Specs,
    Ask,
}

/// <summary>
/// The app shell: pages, Simple/3D mode, scans per drive, live monitoring, dialogs, toasts,
/// language and theme. Pages call back into it through small host interfaces.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IFolderHost, IMemoryHost, IAskHost
{
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(3.4);

    private readonly AppServices _services;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, StorageSummaryViewModel> _summaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HiddenSpaceReport> _hidden = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _settings;
    private CancellationTokenSource? _scanCancellation;
    private int _toastVersion;
    private readonly Queue<MonitorSample> _recentSamples = new();

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        _clock = services.Clock ?? (() => DateTimeOffset.Now);
        _settings = services.Settings.Load();
        Language = _settings.Language;
        Localizer.Instance.SetLanguage(Language);
        IsDarkTheme = _settings.Theme switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => null,
        };

        Live = new LiveViewModel();
        Welcome = new WelcomeViewModel(this);
        Home = new HomeViewModel(this);
        Cleanup = new CleanupViewModel(services.Cleanup, this, () => Page = Page.Home, services.CleanupHistory, services.Explorer);
        Memory = new MemoryViewModel(Live, this, services.Closer ?? new ProcessCloser());
        Folders = new FolderBrowserViewModel(this);
        Pc = new PcViewModel(this);
        Specs = new SpecsViewModel(services.Specs, this, services.Clipboard, services.MemoryModules);
        Ask = services.Assistant is { } assistant ? new AskViewModel(assistant, this, _clock) : null;
        if (Ask is not null)
        {
            Memory.AskAbout = AskAbout;
            Cleanup.AskAbout = AskAbout;
            Specs.AskAbout = AskAbout;
        }
        Page = _settings.WelcomeSeen ? Page.Home : Page.Welcome;
        Localizer.Instance.LanguageChanged += (_, _) => RefreshTexts();
    }

    public LiveViewModel Live { get; }

    public WelcomeViewModel Welcome { get; }

    public HomeViewModel Home { get; }

    public CleanupViewModel Cleanup { get; }

    public MemoryViewModel Memory { get; }

    public FolderBrowserViewModel Folders { get; }

    public PcViewModel Pc { get; }

    public SpecsViewModel Specs { get; }

    /// <summary>The assistant screen; null when the app runs without assistant services (some tests).</summary>
    public AskViewModel? Ask { get; }

    public bool HasAssistant => Ask is not null;

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    IReadOnlyList<DriveViewModel> IFolderHost.Drives => Drives;

    /// <summary>The drive Windows runs from.</summary>
    public DriveViewModel? SystemDrive => Drives.FirstOrDefault(d => string.Equals(d.Summary.RootPath, _services.Drives.SystemDriveRoot, StringComparison.OrdinalIgnoreCase))
        ?? Drives.FirstOrDefault();

    public StorageSummaryViewModel? SystemSummary => SystemDrive is { } drive ? _summaries.GetValueOrDefault(drive.Summary.RootPath) : null;

    /// <summary>
    /// The drive shown. Clearing it is ignored: a drive list clears its selection when its
    /// page unloads, and that must not forget the drive.
    /// </summary>
    public DriveViewModel? SelectedDrive
    {
        get;
        set
        {
            if (value is null && field is not null)
            {
                OnPropertyChanged(); // Lets the list restore its selection.
                return;
            }

            if (SetProperty(ref field, value) && value is not null)
            {
                CurrentScan = ShowDriveAsync(value, forceRescan: false);
            }
        }
    }

    /// <summary>The summary of the selected drive, shown by the Storage page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(MainContent), nameof(StatusText))]
    public partial StorageSummaryViewModel? CurrentSummary { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(MainContent), nameof(ShowSidebar),
        nameof(IsHomePage), nameof(IsStoragePage), nameof(IsCleanupPage), nameof(IsMemoryPage), nameof(IsFoldersPage), nameof(IsSpecsPage), nameof(IsAskPage))]
    public partial Page Page { get; private set; }

    /// <summary>3D mode replaces the pages with the ASCII PC.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MainContent), nameof(ShowSidebar))]
    public partial bool Is3d { get; private set; }

    // Leaving the Specs screen withdraws a pending password request: nobody is looking at it any more.
    partial void OnPageChanged(Page value)
    {
        if (value != Page.Specs)
        {
            Specs.CancelModulesRead();
        }
    }

    partial void OnIs3dChanged(bool value)
    {
        if (value)
        {
            Specs.CancelModulesRead();
        }
    }

    /// <summary>The page view model for <see cref="Page"/>.</summary>
    public object? CurrentPage => Page switch
    {
        Page.Welcome => Welcome,
        Page.Home => Home,
        Page.Storage => CurrentSummary,
        Page.Cleanup => Cleanup,
        Page.Memory => Memory,
        Page.Specs => Specs,
        Page.Ask => Ask,
        _ => Folders,
    };

    /// <summary>What fills the window: the welcome screen, the 3D PC, or the current page.</summary>
    public object? MainContent => Page == Page.Welcome ? Welcome : Is3d ? Pc : CurrentPage;

    public bool ShowSidebar => Page != Page.Welcome && !Is3d;

    public bool IsHomePage => Page == Page.Home;

    public bool IsStoragePage => Page == Page.Storage;

    public bool IsCleanupPage => Page == Page.Cleanup;

    public bool IsMemoryPage => Page == Page.Memory;

    public bool IsFoldersPage => Page == Page.Folders;

    public bool IsSpecsPage => Page == Page.Specs;

    public bool IsAskPage => Page == Page.Ask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusShort))]
    public partial bool IsScanning { get; private set; }

    /// <summary>A dialog over the window: unlock, clean confirmation or closing an app.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial object? Dialog { get; private set; }

    public bool HasDialog => Dialog is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    public partial string? Toast { get; private set; }

    public bool HasToast => Toast is not null;

    /// <summary>True once the hidden system space of the Windows drive has been measured.</summary>
    [ObservableProperty]
    public partial bool IsUnlocked { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnglish), nameof(IsPortuguese))]
    public partial LanguagePreference Language { get; private set; }

    public bool IsPortuguese => Localizer.Instance.IsPortuguese;

    public bool IsEnglish => !IsPortuguese;

    /// <summary>Null until the view reports the system theme.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeLabel))]
    public partial bool? IsDarkTheme { get; private set; }

    public string ThemeLabel => IsDarkTheme == false ? Strings.ThemeLight : Strings.ThemeDark;

    public string Greeting => _clock().Hour switch
    {
        < 12 => Strings.GreetMorning,
        < 18 => Strings.GreetAfternoon,
        _ => Strings.GreetEvening,
    };

    /// <summary>"Scanning… 1,552 files found" or "Scanned 2 min ago · 1,747,000 files".</summary>
    public string StatusText => IsScanning
        ? string.Format(Strings.ScanningFiles, CurrentSummary?.FileCount ?? 0)
        : CurrentSummary is { IsComplete: true, CompletedAt: { } at } summary
            ? string.Format(Strings.ScannedAgo, Ago(at), summary.FileCount)
            : "";

    public string StatusShort => IsScanning ? Strings.StatusScanning : Strings.StatusReady;

    public string SystemDriveStatus => SystemDrive is { } d ? $"{d.Letter} {d.UsedPercentText}" : "";

    public string RamStatus => Live.HasSample ? $"RAM {Live.MemoryUsedText}/{Live.MemoryTotalText}" : "";

    public string CpuStatus => Live.HasSample ? $"CPU {Live.CpuText}" : "";

    public string BitsLine => Ascii.BitsLine(Live.Bits);

    public string ChipArt => Ascii.ChipArt(Live.Bits);

    /// <summary>The scan started by the latest drive selection; lets tests await it.</summary>
    public Task CurrentScan { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the drives, starts scanning the Windows drive and measuring what can be cleaned.</summary>
    public void Initialize()
    {
        Drives.Clear();
        foreach (DriveSummary drive in _services.Drives.GetDrives())
        {
            Drives.Add(new DriveViewModel(drive));
        }

        SelectedDrive = SystemDrive;
        _ = MeasureCleanupAsync();
        RefreshPages();
    }

    private async Task MeasureCleanupAsync()
    {
        await Cleanup.MeasureAsync();
        RefreshPages();
    }

    /// <summary>Once per second: live numbers, and the bits that flicker.</summary>
    public async Task TickAsync()
    {
        if (_services.Monitor is { } monitor)
        {
            try
            {
                MonitorSample sample = await Task.Run(monitor.Sample);
                Live.Apply(sample);
                _recentSamples.Enqueue(sample);
                while (_recentSamples.Count > FactCollector.SustainedSamples)
                {
                    _recentSamples.Dequeue();
                }
            }
            catch (Exception ex)
            {
                // Sampling must never take the app down: Windows refuses, a driver disappears, a
                // counter query fails. Keep the last numbers and flicker the status.
                Debug.WriteLine(ex);
                Live.Flicker();
            }
        }
        else
        {
            Live.Flicker();
        }

        try
        {
            Memory.Update();
            Home.RefreshLive();
            Pc.RefreshLive();
        }
        catch (Exception ex)
        {
            // Refreshing the views must not escape either: TickAsync runs from an async void timer
            // handler, and a stale number is better than a dead window.
            Debug.WriteLine(ex);
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RamStatus));
        OnPropertyChanged(nameof(CpuStatus));
        OnPropertyChanged(nameof(BitsLine));
        OnPropertyChanged(nameof(ChipArt));
    }

    /// <summary>Opens a drive's storage summary (scanning it if needed).</summary>
    public void ShowDrive(DriveViewModel drive)
    {
        SelectedDrive = drive;
        Navigate(Page.Storage);
    }

    /// <summary>Storage for the Windows drive, with a category selected (null keeps the current one).</summary>
    public void ShowStorageCategory(StorageCategory? category)
    {
        if (SystemDrive is { } system)
        {
            ShowDrive(system);
        }

        if (category is { } wanted && CurrentSummary is { } summary)
        {
            summary.SelectedCategory = summary.Categories.First(row => row.Category == wanted);
        }
    }

    /// <summary>"Analyze my PC" on the welcome screen.</summary>
    public void Analyze()
    {
        SaveSettings(_settings with { WelcomeSeen = true });
        if (SystemDrive is { } system)
        {
            ShowDrive(system);
        }
        else
        {
            Navigate(Page.Home);
        }
    }

    public void SetLanguage(LanguagePreference language)
    {
        Language = language;
        Localizer.Instance.SetLanguage(language);
        SaveSettings(_settings with { Language = language });
        Welcome.Refresh();
    }

    /// <summary>The view reports the Windows theme until the user picks one.</summary>
    public void SyncSystemTheme(bool isDark) => IsDarkTheme ??= isDark;

    public void ShowDialog(object dialog) => Dialog = dialog;

    public void CloseDialog() => Dialog = null;

    public void ShowToast(string message)
    {
        Toast = message;
        int version = ++_toastVersion;
        _ = HideToastAsync(version);
    }

    public void RequestUnlock()
    {
        if (!IsUnlocked)
        {
            Dialog = new UnlockDialogViewModel(this);
        }
    }

    /// <summary>Asks Windows for permission and measures the hidden system space (called by the dialog).</summary>
    public async Task UnlockAsync(UnlockDialogViewModel dialog)
    {
        if (SystemDrive is not { } system)
        {
            CloseDialog();
            return;
        }

        UnlockResult result = await _services.HiddenSpace.MeasureAsync(system.Summary.RootPath, CancellationToken.None);
        switch (result.Outcome)
        {
            case UnlockOutcome.Measured when result.Report is { } report:
                _hidden[system.Summary.RootPath] = report;
                SystemSummary?.ApplyHiddenSpace(report);
                IsUnlocked = true;
                CloseDialog();
                ShowToast(Strings.UnlockedToast);
                ShowStorageCategory(StorageCategory.HiddenSystem);
                RefreshPages();
                break;
            case UnlockOutcome.Declined:
                dialog.Declined();
                break;
            default:
                CloseDialog();
                ShowToast(Strings.UnlockFailed);
                break;
        }
    }

    StorageTree? IFolderHost.TreeFor(DriveViewModel drive) =>
        _summaries.GetValueOrDefault(drive.Summary.RootPath) is { IsComplete: true } summary ? summary.Tree : null;

    void IFolderHost.ScanDrive(DriveViewModel drive) => ShowDrive(drive);

    void IFolderHost.ShowInExplorer(string path)
    {
        _services.Explorer.ShowInExplorer(path);
        ShowToast(string.Format(Strings.ExplorerToast, path));
    }

    /// <summary>The storage category of the folder an executable lives in.</summary>
    StorageCategory? IMemoryHost.CategoryOf(string imagePath)
    {
        string? folder = Path.GetDirectoryName(imagePath);
        if (folder is null || Path.GetPathRoot(folder) is not { Length: > 0 } root)
        {
            return null;
        }

        StorageCategory category = _services.Classifier.Classify(root, StorageCategory.Other);
        string path = root;
        foreach (string segment in folder[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            path = Path.Join(path, segment);
            category = _services.Classifier.Classify(path, category);
        }

        return category;
    }

    [RelayCommand]
    private void GoHome() => Navigate(Page.Home);

    [RelayCommand]
    private void ShowStorage()
    {
        if (CurrentSummary is null && SystemDrive is { } system)
        {
            SelectedDrive = system;
        }

        Navigate(Page.Storage);
    }

    [RelayCommand]
    private void GoCleanup() => Navigate(Page.Cleanup);

    [RelayCommand]
    private void GoMemory() => Navigate(Page.Memory);

    /// <summary>Folders opens the selected drive when its scan is done, otherwise "This PC".</summary>
    [RelayCommand]
    private void ShowFolders()
    {
        if (Page != Page.Folders)
        {
            Folders.Show(CurrentSummary is { IsComplete: true, Tree: { } tree } ? tree.Root : null, resetHistory: true);
        }

        Navigate(Page.Folders);
    }

    [RelayCommand]
    private void GoSpecs()
    {
        Navigate(Page.Specs);
        _ = Specs.EnsureLoaded();
    }

    [RelayCommand]
    private void GoAsk()
    {
        if (Ask is { } ask)
        {
            Navigate(Page.Ask);
            _ = ask.EnterAsync();
        }
    }

    /// <summary>"Ask about this" on another screen: opens Ask with the question ready, about that topic.</summary>
    public void AskAbout(AssistantTopic topic, string question)
    {
        if (Ask is { } ask)
        {
            ask.AskAbout(topic, question);
            GoAsk();
        }
    }

    AssistantSettings IAskHost.AssistantSettings => _settings.Assistant;

    void IAskHost.SaveAssistantSettings(AssistantSettings settings) => SaveSettings(_settings with { Assistant = settings });

    /// <summary>What the assistant may know about this PC: everything the app has measured so far.</summary>
    public async Task<PcSnapshot> SnapshotAsync()
    {
        try
        {
            await Specs.EnsureLoaded();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // The specs are optional; the assistant answers from the rest.
        }

        StorageTree? tree = SystemSummary is { IsComplete: true, Tree: { } t } ? t : null;
        return new PcSnapshot(
            Specs.Specs,
            [.. _recentSamples],
            [.. Drives.Select(d => new DriveFact(d.Summary.RootPath, d.Summary.Label, d.Summary.TotalBytes, d.Summary.FreeBytes, d == SystemDrive))],
            tree is null ? null : StorageCategories.All.ToDictionary(c => c, tree.GetCategoryBytes),
            [.. Cleanup.Items.Select(i => i.Estimate)],
            IsUnlocked);
    }

    /// <summary>The screen or dialog an assistant answer points to.</summary>
    public void Open(AssistantAction action)
    {
        switch (action.Kind)
        {
            case ActionKind.Cleanup:
                Navigate(Page.Cleanup);
                break;
            case ActionKind.CleanupItem:
                Navigate(Page.Cleanup);
                if (Cleanup.Items.FirstOrDefault(i => i.Rule.Id == action.Argument) is { } item)
                {
                    Cleanup.OpenPreviewCommand.Execute(item);
                }

                break;
            case ActionKind.Memory:
                Navigate(Page.Memory);
                break;
            case ActionKind.CloseApp:
                Navigate(Page.Memory);
                Memory.Update();
                if (Memory.Apps.FirstOrDefault(a => a.Key == action.Argument) is { } row)
                {
                    Memory.CloseCommand.Execute(row);
                }

                break;
            case ActionKind.Storage:
                ShowStorageCommand.Execute(null);
                break;
            case ActionKind.Folders:
                ShowFoldersCommand.Execute(null);
                break;
            case ActionKind.Specs:
                GoSpecsCommand.Execute(null);
                break;
            case ActionKind.Unlock:
                RequestUnlock();
                break;
        }
    }

    public string? AppName(string key) =>
        Live.Apps.FirstOrDefault(a => a.Key == key) is { } app
            ? app.Explanation?.Title.Get(Localizer.Instance.Culture) ?? app.Description ?? app.Key
            : null;

    [RelayCommand]
    private void SetSimpleMode() => Is3d = false;

    [RelayCommand]
    private void Set3dMode()
    {
        if (Page == Page.Welcome)
        {
            Navigate(Page.Home);
        }

        Is3d = true;
        Pc.Refresh();
    }

    [RelayCommand]
    private void SetEnglish() => SetLanguage(LanguagePreference.English);

    [RelayCommand]
    private void SetPortuguese() => SetLanguage(LanguagePreference.Portuguese);

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = IsDarkTheme == false;
        SaveSettings(_settings with { Theme = IsDarkTheme == true ? ThemePreference.Dark : ThemePreference.Light });
    }

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private void Rescan()
    {
        if (SelectedDrive is { } drive)
        {
            CurrentScan = ShowDriveAsync(drive, forceRescan: true);
        }
    }

    private bool CanRescan() => !IsScanning;

    [RelayCommand]
    private void Unlock() => RequestUnlock();

    private void Navigate(Page page)
    {
        Is3d = false;
        Page = page;
        if (page == Page.Home)
        {
            Home.Refresh();
        }
    }

    private async Task ShowDriveAsync(DriveViewModel drive, bool forceRescan)
    {
        _scanCancellation?.Cancel();
        string root = drive.Summary.RootPath;

        if (!forceRescan && _summaries.TryGetValue(root, out var existing))
        {
            CurrentSummary = existing;
            return;
        }

        // What the screen showed before this attempt: a failed rescan must not replace it with a
        // half-built summary.
        StorageSummaryViewModel? previous = _summaries.GetValueOrDefault(root);

        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        var summary = new StorageSummaryViewModel(drive.Summary, _services.Locations, OpenFolder, RequestUnlock, isSystemDrive: drive == SystemDrive);
        if (_hidden.TryGetValue(root, out var hidden))
        {
            summary.ApplyHiddenSpace(hidden);
        }

        _summaries[root] = summary;
        CurrentSummary = summary;
        IsScanning = true;

        var progress = new Progress<ScanProgress>(p =>
        {
            summary.ApplyProgress(p);
            if (_scanCancellation == cancellation)
            {
                OnPropertyChanged(nameof(StatusText));
            }
        });

        try
        {
            StorageTree tree = await _services.Scanner.ScanAsync(root, _services.Classifier, progress, cancellation.Token);
            summary.ApplyResult(tree, _clock());
            RefreshPages();
        }
        catch (OperationCanceledException)
        {
            // Forget the partial result so the drive is scanned again next time.
            if (_summaries.GetValueOrDefault(root) == summary)
            {
                _summaries.Remove(root);
            }
        }
        catch (Exception ex)
        {
            // A scan fails for reasons the person cannot control: a drive that went away, a folder
            // deleted mid-walk, permissions. Tell them, put back what was on screen before and let a
            // retry rescan, instead of leaving an empty screen with nothing to click.
            Debug.WriteLine(ex);
            if (_summaries.GetValueOrDefault(root) == summary)
            {
                if (previous is null)
                {
                    _summaries.Remove(root);
                }
                else
                {
                    _summaries[root] = previous;
                    CurrentSummary = previous;
                }
            }

            ShowToast(string.Format(Strings.ScanFailed, drive.Name));
        }
        finally
        {
            if (_scanCancellation == cancellation)
            {
                IsScanning = false;
            }
        }
    }

    private void OpenFolder(StorageNode node)
    {
        Folders.Show(node, resetHistory: true);
        Navigate(Page.Folders);
    }

    private void RefreshPages()
    {
        Home.Refresh();
        Pc.Refresh();
        Folders.Refresh();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SystemDriveStatus));
        OnPropertyChanged(nameof(RamStatus));
        OnPropertyChanged(nameof(CpuStatus));
    }

    private void RefreshTexts()
    {
        foreach (DriveViewModel drive in Drives)
        {
            drive.Refresh();
        }

        foreach (StorageSummaryViewModel summary in _summaries.Values)
        {
            summary.RefreshTexts();
        }

        Cleanup.RefreshTexts();
        Memory.RefreshTexts();
        Specs.RefreshTexts();
        Ask?.RefreshTexts();
        Welcome.Refresh();
        RefreshPages();
        OnPropertyChanged(nameof(IsPortuguese));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(StatusShort));
    }

    private void SaveSettings(AppSettings settings)
    {
        _settings = settings;
        _services.Settings.Save(settings);
    }

    private async Task HideToastAsync(int version)
    {
        await Task.Delay(ToastDuration);
        if (version == _toastVersion)
        {
            Toast = null;
        }
    }

    private string Ago(DateTimeOffset at)
    {
        TimeSpan elapsed = _clock() - at;
        return elapsed.TotalMinutes < 1 ? Strings.JustNow
            : elapsed.TotalHours < 1 ? string.Format(Strings.MinutesAgo, (int)elapsed.TotalMinutes)
            : string.Format(Strings.HoursAgo, (int)elapsed.TotalHours);
    }
}

public sealed partial class DriveViewModel(DriveSummary summary) : ObservableObject
{
    public DriveSummary Summary { get; } = summary;

    public string Name => $"{Label} ({Letter})";

    /// <summary>"C:".</summary>
    public string Letter => Summary.RootPath.TrimEnd('\\');

    /// <summary>The volume label, or "Local Disk" when the drive has none.</summary>
    public string Label => string.IsNullOrWhiteSpace(Summary.Label) ? Strings.LocalDisk : Summary.Label;

    public string UsedPercentText => UsedFraction.ToString("P0");

    public string UsedText => string.Format(Strings.DriveUsed, ByteSize.Format(Summary.UsedBytes), ByteSize.Format(Summary.TotalBytes));

    public double UsedFraction => Summary.TotalBytes == 0 ? 0 : (double)Summary.UsedBytes / Summary.TotalBytes;

    public void Refresh() => OnPropertyChanged(string.Empty);
}
