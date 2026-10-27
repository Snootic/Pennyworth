using System.Runtime.Versioning;
using Pennyworth.Assistant;
using Pennyworth.Cleanup;
using Pennyworth.Core.Storage;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Linux: <c>/proc</c>, <c>/sys</c>, the XDG trash and base directories, <c>statx</c> and the Secret
/// Service behind <see cref="IPlatform"/>. Nothing here asks for administrator rights.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPlatform : IPlatform
{
    private readonly XdgPaths _paths;
    private readonly EnvironmentScope _scope;

    /// <exception cref="InvalidOperationException">The home directory is unknown, so there is nowhere to keep files.</exception>
    public LinuxPlatform()
    {
        _paths = new XdgPaths();
        _scope = new EnvironmentScope(_paths);
        AppNames = new FlatpakAppNames(_paths.DataHome);
    }

    public string Name => KnowledgeBaseLoader.Linux;

    public IDriveService Drives { get; } = new MountDriveService();

    public IStorageScanner Scanner { get; } = new PosixDirectoryScanner();

    public IExplorerLauncher Explorer { get; } = new XdgFileManager();

    public IHiddenSpaceService HiddenSpace { get; } = new LinuxSystemSpaceService();

    public IProcessCloser Closer { get; } = new SignalProcessCloser();

    public IAppNames AppNames { get; }

    public ISpecsReader Specs { get; } = new LinuxSpecsReader();

    public string SettingsPath => _paths.SettingsPath;

    public string CleanupHistoryPath => _paths.CleanupHistoryPath;

    public string? ResolveVariable(string name) => _scope.Resolve(name);

    public ICleanupService CreateCleanup(IReadOnlyList<CleanupRule> rules) => new CleanupService(
        rules,
        environment: LinuxCleanupEnvironment.Create(_paths, _scope, () => MountTable.MountPoints(), XdgTrash.CurrentUid()));

    public ISystemMonitor? CreateMonitor(IEnumerable<ProcessExplanation> explanations) => new LinuxMonitor(explanations);

    public AssistantServices CreateAssistant(Func<long> availableMemory) =>
        LinuxAssistantServices.Create(_paths, new SecretServiceStore(), availableMemory);
}
