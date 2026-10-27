using System.Runtime.Versioning;
using Pennyworth.Assistant;
using Pennyworth.Assistant.Local;
using Pennyworth.Assistant.Providers;
using Pennyworth.Cleanup;
using Pennyworth.Core.Storage;
using Pennyworth.Core.Wording;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Hardware;
using Pennyworth.Platform;
using Pennyworth.Scanning;

namespace Pennyworth.App.Services.Windows;

/// <summary>Windows: the Win32 services Pennyworth has always used, behind <see cref="IPlatform"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatform
{
    public string Name => KnowledgeBaseLoader.Windows;

    public bool ExtendsIntoTitleBar => true;

    public IDriveService Drives { get; } = new WindowsDriveService();

    public IStorageScanner Scanner { get; } = new DirectoryScanner();

    public IExplorerLauncher Explorer { get; } = new ExplorerLauncher();

    public IHiddenSpaceService HiddenSpace { get; } = new ElevatedHiddenSpaceService();

    public IProcessCloser Closer { get; } = new ProcessCloser();

    public ISpecsReader Specs { get; } = new WindowsSpecsReader();

    public string SettingsPath => FileSettingsStore.DefaultPath;

    public string CleanupHistoryPath => CleanupHistory.DefaultPath;

    public string? ResolveVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string Text(PlatformText text, bool portuguese) => WindowsText.Instance.Text(text, portuguese);

    public ICleanupService CreateCleanup(IReadOnlyList<CleanupRule> rules) =>
        new CleanupService(rules, environment: CleanupEnvironment.Windows(ResolveVariable, new WindowsRecycleBin()));

    public ISystemMonitor? CreateMonitor(IEnumerable<ProcessExplanation> explanations) => new SystemMonitor(explanations);

    public AssistantServices CreateAssistant(Func<long> availableMemory) => AssistantServicesFactory.Create(
        new WindowsCredentialStore(),
        ConversationStore.DefaultFolder,
        UsageMeter.DefaultPath,
        LocalModelStore.DefaultRoot,
        availableMemory,
        folder => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace,
        platformName: Name);
}
