using Pennyworth.Core.Privilege;
using Pennyworth.KnowledgeBase;

namespace Pennyworth.Cleanup;

/// <summary>One file (or, in the Recycle Bin, one deleted item) the cleanup will remove, as it was when listed.</summary>
/// <param name="ChangedUtc">Last write time when listed (Recycle Bin: when it was deleted). A file changed since is left alone.</param>
/// <param name="OriginalPath">Recycle Bin only: where the item was before it was deleted.</param>
/// <param name="CompanionPath">Recycle Bin only: the bin's record file ($I...) removed together with the item.</param>
public sealed record CleanupFile(string Path, long Size, DateTime ChangedUtc, string? OriginalPath = null, string? CompanionPath = null);

/// <summary>A folder whose contents a rule cleans, with what is in it right now.</summary>
public sealed record CleanupFolder(string Path, long Bytes, long Files);

/// <summary>What cleaning a rule would remove, file by file. Cleaning removes exactly these files and nothing else.</summary>
/// <param name="IsAvailable">False when the rule needs admin rights this app doesn't have yet.</param>
/// <param name="Items">Files found (Recycle Bin: items in it).</param>
/// <param name="Admin">
/// Only for a rule that needs admin rights, on an OS whose cleanup can ask for them (<see cref="IAdminCleaner"/>):
/// whether that can be done now (then <paramref name="IsAvailable"/> is true and cleaning shows a password prompt) or why not.
/// Null everywhere else.
/// </param>
public sealed record CleanupEstimate(
    CleanupRule Rule,
    long Bytes,
    long Items,
    bool IsAvailable,
    IReadOnlyList<CleanupFolder>? Folders = null,
    IReadOnlyList<CleanupFile>? Files = null,
    PrivilegedCapability? Admin = null);

/// <param name="Skipped">Files left alone: in use, protected, changed since they were listed, or already gone.</param>
/// <param name="Outcome">
/// <see cref="PrivilegedOutcome.Done"/> for every unprivileged rule. For a rule that needs admin rights it can also say the
/// person cancelled the password prompt, it could not be asked for (<paramref name="Reason"/>), or it failed; nothing was deleted then.
/// </param>
public sealed record CleanupResult(
    CleanupRule Rule,
    long BytesFreed,
    long FilesDeleted,
    long Skipped,
    PrivilegedOutcome Outcome = PrivilegedOutcome.Done,
    PrivilegedUnavailableReason? Reason = null);

/// <summary>
/// Cleans the rules that need an administrator (<c>requiresAdmin</c>) on an OS that can ask for one. The preview is
/// still made without privileges; this only deletes, and only the files of that preview.
/// </summary>
public interface IAdminCleaner
{
    /// <summary>Whether admin rules can be cleaned now; evaluated on every call, never shows a prompt.</summary>
    PrivilegedCapability Capability { get; }

    /// <summary>Deletes the files of <paramref name="estimate"/>, a rule that needs admin rights. Shows the system's password prompt.</summary>
    Task<PrivilegedResult<CleanupResult>> CleanAsync(CleanupEstimate estimate, CancellationToken cancellationToken);
}

public interface ICleanupService
{
    IReadOnlyList<CleanupRule> Rules { get; }

    /// <summary>Dry run: lists every file cleaning the rule would remove right now.</summary>
    Task<CleanupEstimate> MeasureAsync(CleanupRule rule, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes exactly the files of <paramref name="estimate"/>, nothing found later. Rules needing
    /// admin rights are refused.
    /// </summary>
    Task<CleanupResult> CleanAsync(CleanupEstimate estimate, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Measures and cleans what the knowledge base allows. Hard rules: only files listed in the
/// preview are deleted, only inside the rule's folders (never the folders themselves), nothing
/// the environment's safety rule refuses (system folders, the user's profile), links and junctions
/// are never followed, files in use or changed since the preview are skipped.
/// </summary>
/// <param name="getEnvironmentVariable">Variables for the Windows rules used when no environment is given; defaults to the process environment.</param>
/// <param name="environment">The operating system's trash, safety rule and path comparison. Null means the Windows rules without a Recycle Bin.</param>
public sealed class CleanupService(
    IReadOnlyList<CleanupRule> rules,
    Func<string, string?>? getEnvironmentVariable = null,
    CleanupEnvironment? environment = null) : ICleanupService
{
    private readonly CleanupEnvironment _environment =
        environment ?? CleanupEnvironment.Windows(getEnvironmentVariable ?? Environment.GetEnvironmentVariable);

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public IReadOnlyList<CleanupRule> Rules { get; } = rules;

    public Task<CleanupEstimate> MeasureAsync(CleanupRule rule, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (rule.Kind == CleanupKind.RecycleBin)
        {
            var items = RecycleBinOf(rule).List();
            return new CleanupEstimate(rule, items.Sum(i => i.Size), items.Count, IsAvailable: true, [], items);
        }

        var folders = new List<CleanupFolder>();
        var files = new List<CleanupFile>();
        foreach (string folder in FoldersOf(rule))
        {
            long bytes = 0, count = 0;
            foreach (FileInfo file in Files(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Describe(file) is { } listed)
                {
                    files.Add(listed);
                    bytes += listed.Size;
                    count++;
                }
            }

            folders.Add(new CleanupFolder(folder, bytes, count));
        }

        return new CleanupEstimate(rule, files.Sum(f => f.Size), files.Count, IsAvailable: !rule.RequiresAdmin, folders, files);
    }, cancellationToken);

    public Task<CleanupResult> CleanAsync(CleanupEstimate estimate, IProgress<double>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        CleanupRule rule = estimate.Rule;
        if (rule.RequiresAdmin)
        {
            throw new InvalidOperationException($"Cleanup rule '{rule.Id}' needs admin rights.");
        }

        IReadOnlyList<CleanupFile> files = estimate.Files ?? [];
        Func<CleanupFile, bool> delete = rule.Kind == CleanupKind.RecycleBin
            ? RecycleBinOf(rule).Delete
            : DeleteFileIn(FoldersOf(rule));

        long freed = 0, deleted = 0, skipped = 0;
        var touchedFolders = new HashSet<string>(_environment.PathComparison);
        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delete(files[i]))
            {
                freed += files[i].Size;
                deleted++;
                if (Path.GetDirectoryName(files[i].Path) is { } parent)
                {
                    touchedFolders.Add(parent);
                }
            }
            else
            {
                skipped++;
            }

            if (i % 64 == 0)
            {
                progress?.Report((double)i / files.Count);
            }
        }

        if (rule.Kind == CleanupKind.RecycleBin)
        {
            RecycleBinOf(rule).RefreshIcon();
        }
        else
        {
            RemoveEmptiedFolders(touchedFolders, FoldersOf(rule));
        }

        progress?.Report(1);
        return new CleanupResult(rule, freed, deleted, skipped);
    }, cancellationToken);

    private IRecycleBin RecycleBinOf(CleanupRule rule) =>
        _environment.RecycleBin ?? throw new InvalidOperationException($"Cleanup rule '{rule.Id}' needs a recycle bin and this system has none.");

    private IReadOnlyList<string> FoldersOf(CleanupRule rule) =>
        CleanupTargets.Resolve(rule.Paths, _environment.Variables, _environment.PathComparison)
            .Where(_environment.MayCleanInside)
            .ToArray();

    /// <summary>Whether <paramref name="path"/> lies strictly below <paramref name="root"/>, as this OS compares paths.</summary>
    private bool IsBelow(string path, string root)
    {
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.Length >= prefix.Length && _environment.PathComparison.Equals(path[..prefix.Length], prefix);
    }

    /// <summary>
    /// Deletes a listed file only if it is still inside one of the rule's folders (checked again now),
    /// is not a link, and hasn't changed since it was listed.
    /// </summary>
    private Func<CleanupFile, bool> DeleteFileIn(IReadOnlyList<string> roots) => listed =>
    {
        string full = Path.GetFullPath(listed.Path);
        if (!roots.Any(root => IsBelow(full, root)))
        {
            return false;
        }

        var file = new FileInfo(full);
        try
        {
            if (!file.Exists
                || (file.Attributes & FileAttributes.ReparsePoint) != 0
                || file.LastWriteTimeUtc != listed.ChangedUtc)
            {
                return false; // Gone, a link, or changed since the preview: leave it.
            }

            if (file.IsReadOnly)
            {
                file.IsReadOnly = false;
            }

            file.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // In use or protected: leave it.
        }
    };

    private static IEnumerable<FileInfo> Files(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", Walk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static CleanupFile? Describe(FileInfo file)
    {
        try
        {
            return new CleanupFile(file.FullName, file.Length, file.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes folders that this cleanup emptied, deepest first, never a rule's own folder and never
    /// a folder that was already empty before.
    /// </summary>
    private void RemoveEmptiedFolders(IEnumerable<string> touched, IReadOnlyList<string> roots)
    {
        var rootSet = roots.Select(Path.TrimEndingDirectorySeparator).ToHashSet(_environment.PathComparison);
        var candidates = new HashSet<string>(_environment.PathComparison);
        foreach (string folder in touched)
        {
            for (string? current = folder; current is not null && !rootSet.Contains(current); current = Path.GetDirectoryName(current))
            {
                if (!roots.Any(root => IsBelow(current, root)))
                {
                    break;
                }

                candidates.Add(current);
            }
        }

        foreach (string folder in candidates.OrderByDescending(path => path.Length))
        {
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0 && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
