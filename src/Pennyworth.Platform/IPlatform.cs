using Pennyworth.Cleanup;
using Pennyworth.Core.Storage;
using Pennyworth.Core.Wording;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform;

/// <summary>
/// The only place the app learns which operating system it runs on. The composition root picks one
/// implementation; no screen, view model or domain project asks the OS anything itself. It also speaks
/// for the OS in words ("Explorer" or a file manager, "Recycle Bin" or "Trash"): <see cref="IPlatformText"/>,
/// a smaller interface so that the language switch and the assistant need not depend on the services here.
/// </summary>
public interface IPlatform : IPlatformText
{
    /// <summary>
    /// Which data set this OS uses: <see cref="KnowledgeBaseLoader.Windows"/> or <see cref="KnowledgeBaseLoader.Linux"/>.
    /// The composition root passes it to every <see cref="KnowledgeBaseLoader"/> call.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether the window extends into the title bar, so the system's caption buttons are drawn over its
    /// toolbar row (Windows), or sits under the title bar the system draws (Linux). The toolbar row stays either way.
    /// </summary>
    bool ExtendsIntoTitleBar { get; }

    IDriveService Drives { get; }

    IStorageScanner Scanner { get; }

    IExplorerLauncher Explorer { get; }

    IHiddenSpaceService HiddenSpace { get; }

    IProcessCloser Closer { get; }

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
