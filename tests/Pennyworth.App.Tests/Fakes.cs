using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.Assistant;
using Pennyworth.Assistant.Local;
using Pennyworth.Assistant.Providers;
using Pennyworth.Cleanup;
using Pennyworth.Core.Storage;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Hardware;
using Pennyworth.Platform;
using Pennyworth.Scanning;

namespace Pennyworth.App.Tests;

internal static class SampleTrees
{
    public const long Gb = 1024L * 1024 * 1024;

    // C:\  Windows\ (20 GB)  Games\ (50 GB)  Users\Bob\ (10 GB)  stray.bin (5 GB, Other)
    public static StorageTree Build()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        int windows = b.AddDirectory(StorageTreeBuilder.RootIndex, "Windows", StorageCategory.Windows);
        b.AddFile(windows, "system.dll", 20 * Gb, 20 * Gb, StorageCategory.Windows);
        int games = b.AddDirectory(StorageTreeBuilder.RootIndex, "Games", StorageCategory.Games);
        b.AddFile(games, "game.pak", 50 * Gb, 50 * Gb, StorageCategory.Games);
        int users = b.AddDirectory(StorageTreeBuilder.RootIndex, "Users", StorageCategory.Other);
        int bob = b.AddDirectory(users, "Bob", StorageCategory.YourFiles);
        b.AddFile(bob, "video.mp4", 10 * Gb, 10 * Gb, StorageCategory.YourFiles);
        b.AddFile(StorageTreeBuilder.RootIndex, "stray.bin", 5 * Gb, 5 * Gb, StorageCategory.Other);
        return b.Build();
    }
}

internal sealed class FakeDriveService(params DriveSummary[] drives) : IDriveService
{
    public string SystemDriveRoot { get; init; } = @"C:\";

    public IReadOnlyList<DriveSummary> GetDrives() => drives;
}

internal sealed class FakeScanner(Func<string, StorageTree> result) : IStorageScanner
{
    public List<string> ScannedRoots { get; } = [];

    public TaskCompletionSource? Gate { get; set; }

    public async Task<StorageTree> ScanAsync(string rootPath, IPathClassifier classifier, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ScannedRoots.Add(rootPath);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return result(rootPath);
    }
}

internal sealed class NullClassifier : IPathClassifier
{
    public StorageCategory Classify(string fullPath, StorageCategory inherited) =>
        fullPath.Contains("Games", StringComparison.OrdinalIgnoreCase) ? StorageCategory.Games : inherited;
}

internal sealed class FakeLocations(Dictionary<string, (string Title, string Description)>? known = null) : ILocationInfo
{
    public (string Title, string Description)? Describe(string fullPath) =>
        known is not null && known.TryGetValue(fullPath, out var value) ? value : null;
}

internal sealed class FakeExplorer : IExplorerLauncher
{
    public List<string> Shown { get; } = [];

    public void ShowInExplorer(string path) => Shown.Add(path);
}

internal sealed class MemorySettings(AppSettings? initial = null) : ISettingsStore
{
    public AppSettings Current { get; private set; } = initial ?? new AppSettings { WelcomeSeen = true, Language = Localization.LanguagePreference.English };

    public AppSettings Load() => Current;

    public void Save(AppSettings settings) => Current = settings;
}

internal sealed class FakeHiddenSpace(UnlockResult result) : IHiddenSpaceService
{
    public int Calls { get; private set; }

    public Task<UnlockResult> MeasureAsync(string driveRoot, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(result);
    }
}

/// <summary>Cleanup without touching the disk: fixed sizes, records what was cleaned.</summary>
internal sealed class FakeCleanup(params (CleanupRule Rule, long Bytes)[] items) : ICleanupService
{
    public List<string> Cleaned { get; } = [];

    /// <summary>The exact lists handed to <see cref="CleanAsync"/>.</summary>
    public List<CleanupEstimate> CleanedLists { get; } = [];

    public IReadOnlyList<CleanupRule> Rules { get; } = items.Select(i => i.Rule).ToArray();

    public Task<CleanupEstimate> MeasureAsync(CleanupRule rule, CancellationToken cancellationToken)
    {
        long bytes = Cleaned.Contains(rule.Id) ? 0 : items.Single(i => i.Rule == rule).Bytes;
        string folder = $@"C:\fake\{rule.Id}";
        bool bin = rule.Kind == CleanupKind.RecycleBin;
        CleanupFile[] files = bytes == 0 ? [] :
        [
            new($@"{folder}\big.tmp", bytes / 2, DateTime.UnixEpoch, bin ? @"C:\Users\Bob\big.doc" : null),
            new($@"{folder}\sub\notes.log", bytes / 4, DateTime.UnixEpoch, bin ? @"C:\Users\Bob\notes.log" : null),
            new($@"{folder}\small.tmp", bytes - (bytes / 2) - (bytes / 4), DateTime.UnixEpoch, bin ? @"D:\small.txt" : null),
        ];
        CleanupFolder[] folders = bin ? [] : [new(folder, bytes, files.Length)];
        return Task.FromResult(new CleanupEstimate(rule, bytes, files.Length, !rule.RequiresAdmin, folders, files));
    }

    public Task<CleanupResult> CleanAsync(CleanupEstimate estimate, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        CleanupRule rule = estimate.Rule;
        Cleaned.Add(rule.Id);
        CleanedLists.Add(estimate);
        progress?.Report(1);
        return Task.FromResult(new CleanupResult(rule, items.Single(i => i.Rule == rule).Bytes, 3, rule.Id == "temp" ? 2 : 0));
    }

    public static CleanupRule Rule(string id, SafetyLevel safety, bool admin = false, CleanupKind kind = CleanupKind.FolderContents) =>
        new(id, safety, kind, [], admin, new(id, id), new("", ""), new("", ""));
}

internal sealed class FakeMonitor(MonitorSample sample) : ISystemMonitor
{
    public MonitorSample Sample() => sample;
}

internal sealed class FakeCloser(bool closes) : IProcessCloser
{
    public List<int> Closed { get; } = [];

    public List<int> Forced { get; } = [];

    public Task<bool> CloseAsync(IReadOnlyList<int> pids, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Closed.AddRange(pids);
        return Task.FromResult(closes);
    }

    public void ForceClose(IReadOnlyList<int> pids) => Forced.AddRange(pids);
}

/// <summary>Specs of a made-up PC, so no test reads the hardware it runs on.</summary>
internal sealed class SamplePcReader : ISpecsReader
{
    public PcSpecs Read() => SpecsViewModelTests.SamplePc();
}

/// <summary>A shell wired to fakes; pieces can be replaced per test.</summary>
internal static class TestApp
{
    public static readonly DriveSummary DriveC = new(@"C:\", "", 200 * SampleTrees.Gb, 100 * SampleTrees.Gb);
    public static readonly DriveSummary DriveD = new(@"D:\", "Games", 500 * SampleTrees.Gb, 400 * SampleTrees.Gb);

    public static AppServices Services(
        FakeScanner? scanner = null,
        ICleanupService? cleanup = null,
        IHiddenSpaceService? hidden = null,
        ISettingsStore? settings = null,
        ISystemMonitor? monitor = null,
        IProcessCloser? closer = null,
        FakeExplorer? explorer = null,
        IPrivilegedOperations? privileged = null) => new(
        new FakeDriveService(DriveD, DriveC),
        scanner ?? new FakeScanner(_ => SampleTrees.Build()),
        new NullClassifier(),
        new FakeLocations(),
        explorer ?? new FakeExplorer(),
        cleanup ?? new FakeCleanup(),
        hidden ?? new FakeHiddenSpace(new UnlockResult(UnlockOutcome.Declined)),
        settings ?? new MemorySettings(),
        new SamplePcReader(),
        monitor,
        closer ?? new FakeCloser(closes: true),
        Clock: () => new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero),
        Privileged: privileged);
}

/// <summary>Assistant services that work on temp folders and never call a model.</summary>
internal static class AskTestServices
{
    public static AssistantServices Create(string dir) => new(
        new NoKeys(),
        new ConversationStore(Path.Combine(dir, "conversations")),
        new UsageMeter(Path.Combine(dir, "usage.json")),
        ModelCatalog.Load(),
        new LocalModelStore(Path.Combine(dir, "models")),
        (_, _) => throw new InvalidOperationException("No model in this test."),
        (_, _, _, _) => Task.CompletedTask,
        new Anonymizer("user", @"C:\Users\user", "PC"),
        () => 0,
        _ => 0);

    private sealed class NoKeys : ICredentialStore
    {
        public void Save(string profileId, string secret)
        {
        }

        public string? Read(string profileId) => null;

        public void Delete(string profileId)
        {
        }
    }
}
