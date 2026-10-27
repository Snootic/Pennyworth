namespace Pennyworth.Cleanup;

/// <summary>Where deleted items wait before they are gone for good: the Recycle Bin, the XDG trash.</summary>
public interface IRecycleBin
{
    /// <summary>Every item in the current user's bins, largest first.</summary>
    IReadOnlyList<CleanupFile> List();

    /// <summary>Removes one listed item and its record; false when it is no longer in the bin.</summary>
    bool Delete(CleanupFile item);

    /// <summary>Lets the shell redraw the bin's icon (full or empty), where there is one.</summary>
    void RefreshIcon();
}

/// <summary>What <see cref="CleanupService"/> needs from the operating system.</summary>
/// <param name="Variables">Resolves the <c>%NAME%</c> variables of a rule's paths; null drops the rule.</param>
/// <param name="MayCleanInside">The hard safety rule: may a rule delete inside this folder at all?</param>
/// <param name="PathComparison">How this OS tells two paths apart (ignoring case on Windows, never on Linux).</param>
/// <param name="RecycleBin">Null when the OS has none; rules of kind <see cref="KnowledgeBase.CleanupKind.RecycleBin"/> then cannot run.</param>
/// <param name="AdminCleaner">Null when the OS cannot clean rules that need admin rights; those are then refused.</param>
public sealed record CleanupEnvironment(
    Func<string, string?> Variables,
    Func<string, bool> MayCleanInside,
    StringComparer PathComparison,
    IRecycleBin? RecycleBin = null,
    IAdminCleaner? AdminCleaner = null)
{
    /// <summary>
    /// Windows' rules without its Recycle Bin: a protected-folder guard built from the Windows
    /// variables, and case-insensitive paths. Pure managed code, so it works on any host.
    /// </summary>
    public static CleanupEnvironment Windows(Func<string, string?> variables, IRecycleBin? recycleBin = null) =>
        new(variables, folder => SafetyGuard.MayCleanInside(folder, variables), StringComparer.OrdinalIgnoreCase, recycleBin);
}
