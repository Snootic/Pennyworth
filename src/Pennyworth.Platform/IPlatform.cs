using Pennyworth.Cleanup;
using Pennyworth.Core.Storage;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform;

/// <summary>
/// The only place the app learns which operating system it runs on. The composition root picks one
/// implementation; no screen, view model or domain project asks the OS anything itself.
/// </summary>
public interface IPlatform
{
    /// <summary>
    /// Which data set this OS uses: <see cref="KnowledgeBaseLoader.Windows"/> or <see cref="KnowledgeBaseLoader.Linux"/>.
    /// The composition root passes it to every <see cref="KnowledgeBaseLoader"/> call.
    /// </summary>
    string Name { get; }

    IDriveService Drives { get; }

    IStorageScanner Scanner { get; }

    IExplorerLauncher Explorer { get; }

    IHiddenSpaceService HiddenSpace { get; }

    IProcessCloser Closer { get; }

    /// <summary>Names of the installed apps behind folders, for the storage page.</summary>
    IAppNames AppNames { get; }

    ISpecsReader Specs { get; }

    /// <summary>Where the settings file lives.</summary>
    string SettingsPath { get; }

    /// <summary>Where the log of cleanup runs lives.</summary>
    string CleanupHistoryPath { get; }

    /// <summary>
    /// Resolves the <c>%NAME%</c> variables the knowledge base writes its paths with, for this OS.
    /// Null when the variable has no meaning here, which drops the rule instead of guessing.
    /// </summary>
    string? ResolveVariable(string name);

    /// <summary>Cleaning with this OS's trash, its safety rules and its path comparison.</summary>
    ICleanupService CreateCleanup(IReadOnlyList<CleanupRule> rules);

    /// <summary>Live memory/CPU/GPU numbers; null when this OS cannot provide them.</summary>
    ISystemMonitor? CreateMonitor(IEnumerable<ProcessExplanation> explanations);

    /// <summary>Keys, conversations, usage and downloads for the Ask screen, in this OS's locations.</summary>
    AssistantServices CreateAssistant(Func<long> availableMemory);
}
