using System.Globalization;
using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Cleanup;
using Pennyworth.Core.Formatting;
using Pennyworth.KnowledgeBase;
using Pennyworth.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>
/// Everything one cleanup item would delete, before anything is deleted, browsed like Explorer:
/// folders first, then files, largest first, with Back, Up, breadcrumbs and a search that looks
/// through the current folder and everything under it. Recycle Bin items are one flat list that
/// says where each came from.
/// </summary>
public sealed partial class CleanupPreviewViewModel : ObservableObject
{
    private readonly CleanupViewModel _cleanup;
    private readonly IExplorerLauncher? _explorer;
    private readonly IReadOnlyList<string> _roots;
    private readonly Stack<PreviewFolder> _history = new();

    public CleanupPreviewViewModel(CleanupItemViewModel item, CleanupViewModel cleanup, IExplorerLauncher? explorer)
    {
        Item = item;
        _cleanup = cleanup;
        _explorer = explorer;
        _roots = [.. (item.Estimate.Folders ?? []).Select(folder => folder.Path)];
        Top = PreviewFolder.Build(item.Estimate);
        Current = Top;
    }

    public CleanupItemViewModel Item { get; }

    /// <summary>Where browsing starts: the one cleaned folder, or all of them side by side.</summary>
    public PreviewFolder Top { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Breadcrumbs), nameof(CanShowCurrent))]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    public partial PreviewFolder Current { get; private set; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>Back, Up and breadcrumbs; the Recycle Bin has no folders to walk through.</summary>
    public bool CanNavigate => !Item.IsRecycleBin;

    /// <summary>
    /// The current folder's subfolders, then its files, largest first; while searching, every
    /// matching file in it and below, with where it is.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchText), nameof(HasNoMatch))]
    public partial IReadOnlyList<PreviewEntry> Entries { get; private set; } = [];

    public IReadOnlyList<PreviewCrumb> Breadcrumbs
    {
        get
        {
            var crumbs = new List<PreviewCrumb>();
            for (PreviewFolder? folder = Current; folder is not null; folder = folder.Parent)
            {
                crumbs.Insert(0, new PreviewCrumb(folder, folder.IsAll ? Strings.PreviewAllFolders : folder.Name, folder == Current));
            }

            return crumbs;
        }
    }

    /// <summary>"18.2 GB in 1,204 files. Only these will be deleted; anything newer stays."</summary>
    public string Summary => string.Format(Localizer.Instance.Resolve(Item.IsRecycleBin ? Strings.PreviewSubBin : Strings.PreviewSub), Item.SizeText, Item.Items);

    public string ListLabel => Localizer.Instance.Resolve(Item.IsRecycleBin ? Strings.PreviewItemsBin : Strings.PreviewFiles);

    /// <summary>"3.20 GB · 1,204 files" for this folder, or how many files match the search.</summary>
    public string MatchText => Search.Trim().Length == 0
        ? string.Format(Item.IsRecycleBin ? Strings.PreviewFolderLineBin : Strings.PreviewFolderLine, ByteSize.Format(Current.Bytes), Current.Count)
        : string.Format(Strings.PreviewMatches, Entries.Count);

    public bool HasNoMatch => Entries.Count == 0;

    public string NoMatchText => Top.Count == 0 ? Strings.PreviewNothing : Strings.PreviewNoMatch;

    public bool CanShowCurrent => Current.Path is not null && _explorer is not null;

    [RelayCommand]
    private void Open(PreviewEntry entry)
    {
        if (entry.Folder is { } folder)
        {
            GoTo(folder);
        }
    }

    [RelayCommand]
    private void NavigateTo(PreviewCrumb crumb)
    {
        if (!crumb.IsLast)
        {
            GoTo(crumb.Folder);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up() => GoTo(Current.Parent!);

    private bool CanGoUp() => Current.Parent is not null;

    /// <summary>Back in the browsing history, like Explorer.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (_history.TryPop(out PreviewFolder? previous))
        {
            Search = "";
            Current = previous;
            BackCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanGoBack() => _history.Count > 0;

    [RelayCommand]
    private void Close() => _cleanup.ClosePreview();

    /// <summary>The same tick as on the list: whether this item is part of the cleanup.</summary>
    [RelayCommand]
    private void ToggleInclude() => _cleanup.ToggleCommand.Execute(Item);

    [RelayCommand]
    private void ShowInExplorer(string path) => _explorer?.ShowInExplorer(path);

    [RelayCommand]
    private void ShowCurrentInExplorer()
    {
        if (Current.Path is { } path)
        {
            _explorer?.ShowInExplorer(path);
        }
    }

    internal void Refresh()
    {
        Rebuild();
        OnPropertyChanged(string.Empty);
    }

    partial void OnCurrentChanged(PreviewFolder value) => Rebuild();

    partial void OnSearchChanged(string value) => Rebuild();

    private void GoTo(PreviewFolder folder)
    {
        _history.Push(Current);
        Search = "";
        Current = folder;
        BackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The current folder's subfolders, then its files, largest first; while searching, every
    /// matching file in it and below, with where it is.
    /// </summary>
    private void Rebuild()
    {
        if (Current is null)
        {
            return;
        }

        string term = Search.Trim();
        Entries = term.Length > 0
            ? [.. Current.AllFiles()
                .Where(file => NameOf(file).Contains(term, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(file => file.Size)
                .Select(file => PreviewEntry.ForFile(file, Current.Bytes, LocationOf(file)))]
            :
            [
                .. Current.Folders.Values.OrderByDescending(folder => folder.Bytes).Select(folder => PreviewEntry.ForFolder(folder, Current.Bytes)),
                .. Current.Files.OrderByDescending(file => file.Size).Select(file => PreviewEntry.ForFile(file, Current.Bytes, BinLocationOf(file))),
            ];
    }

    private static string NameOf(CleanupFile file) => Path.GetFileName(file.OriginalPath ?? file.Path);

    /// <summary>
    /// Where a found file is, from the cleaned folder ("Temp\jnb0mdtr"); Recycle Bin: the folder it
    /// was deleted from and when.
    /// </summary>
    private string LocationOf(CleanupFile file)
    {
        if (file.OriginalPath is not null)
        {
            return BinLocationOf(file)!;
        }

        string folder = Path.GetDirectoryName(file.Path) ?? "";
        foreach (string root in _roots)
        {
            string trimmed = Path.TrimEndingDirectorySeparator(root);
            if ((folder.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                 || folder.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                && Path.GetDirectoryName(trimmed) is { } parent)
            {
                return Path.GetRelativePath(parent, folder);
            }
        }

        return folder;
    }

    /// <summary>"C:\Users\Bob · deleted 10/1/2026 9:00" for Recycle Bin items; null for files.</summary>
    private static string? BinLocationOf(CleanupFile file)
    {
        if (file.OriginalPath is not { } original)
        {
            return null;
        }

        string folder = Path.GetDirectoryName(original) ?? "";
        return file.ChangedUtc == DateTime.MinValue
            ? folder
            : $"{folder} · {string.Format(Strings.PreviewDeletedOn, file.ChangedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture))}";
    }
}

/// <summary>A folder in the preview: what will be deleted in it and below it.</summary>
public sealed class PreviewFolder
{
    private PreviewFolder(string name, string? path, PreviewFolder? parent)
    {
        Name = name;
        Path = path;
        Parent = parent;
    }

    public string Name { get; }

    /// <summary>Null for "All folders", the top level when a rule cleans several folders.</summary>
    public string? Path { get; }

    public PreviewFolder? Parent { get; }

    public bool IsAll => Path is null && Parent is null;

    public Dictionary<string, PreviewFolder> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<CleanupFile> Files { get; } = [];

    /// <summary>Bytes listed in this folder and below.</summary>
    public long Bytes { get; private set; }

    /// <summary>Files listed in this folder and below.</summary>
    public long Count { get; private set; }

    public IEnumerable<CleanupFile> AllFiles() => Files.Concat(Folders.Values.SelectMany(folder => folder.AllFiles()));

    /// <summary>
    /// Arranges the listed files under the folders they were found in. One cleaned folder is the
    /// top itself; several sit side by side under "All folders". Recycle Bin items stay flat.
    /// </summary>
    public static PreviewFolder Build(CleanupEstimate estimate)
    {
        string[] roots = estimate.Rule.Kind == CleanupKind.RecycleBin
            ? []
            : [.. (estimate.Folders ?? []).Select(folder => System.IO.Path.TrimEndingDirectorySeparator(folder.Path))];
        var all = new PreviewFolder("", null, null);
        PreviewFolder top = roots.Length == 1 ? new PreviewFolder(System.IO.Path.GetFileName(roots[0]), roots[0], null) : all;
        var rootNodes = roots.Length == 1
            ? new Dictionary<string, PreviewFolder>(StringComparer.OrdinalIgnoreCase) { [roots[0]] = top }
            : roots.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(root => root, root => all.Folders[root] = new PreviewFolder(root, root, all), StringComparer.OrdinalIgnoreCase);

        foreach (CleanupFile file in estimate.Files ?? [])
        {
            string folder = System.IO.Path.GetDirectoryName(file.Path) ?? "";
            string? root = file.OriginalPath is null
                ? rootNodes.Keys.FirstOrDefault(r => folder.Equals(r, StringComparison.OrdinalIgnoreCase)
                    || folder.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                : null;

            PreviewFolder node = root is null ? top : rootNodes[root];
            if (root is not null && folder.Length > root.Length)
            {
                foreach (string part in folder[(root.Length + 1)..].Split(System.IO.Path.DirectorySeparatorChar))
                {
                    if (!node.Folders.TryGetValue(part, out PreviewFolder? child))
                    {
                        child = new PreviewFolder(part, System.IO.Path.Combine(node.Path!, part), node);
                        node.Folders[part] = child;
                    }

                    node = child;
                }
            }

            node.Files.Add(file);
            for (PreviewFolder? up = node; up is not null; up = up.Parent)
            {
                up.Bytes += file.Size;
                up.Count++;
            }
        }

        return top;
    }
}

/// <summary>One row: a folder to open or a file that will be deleted.</summary>
public sealed partial class PreviewEntry : ObservableObject
{
    private readonly long _bytes;
    private readonly long _count;

    private PreviewEntry(string name, string path, long bytes, long count, long parentBytes, PreviewFolder? folder, string? location, bool canShow)
    {
        Name = name;
        Path = path;
        _bytes = bytes;
        _count = count;
        Fraction = parentBytes == 0 ? 0 : (double)bytes / parentBytes;
        Folder = folder;
        Location = location;
        CanShow = canShow;
    }

    public static PreviewEntry ForFolder(PreviewFolder folder, long parentBytes) =>
        new(folder.Name, folder.Path ?? "", folder.Bytes, folder.Count, parentBytes, folder, null, folder.Path is not null);

    public static PreviewEntry ForFile(CleanupFile file, long parentBytes, string? location) =>
        new(System.IO.Path.GetFileName(file.OriginalPath ?? file.Path), file.Path, file.Size, 1, parentBytes, null, location, file.OriginalPath is null);

    public string Name { get; }

    public string Path { get; }

    /// <summary>Set for folders: what opening the row shows.</summary>
    public PreviewFolder? Folder { get; }

    public bool IsFolder => Folder is not null;

    public bool IsFile => Folder is null;

    public double Fraction { get; }

    public string SizeText => ByteSize.Format(_bytes);

    /// <summary>Folders: "1,204 files". Found files: where they are. Recycle Bin: where it was and when it was deleted.</summary>
    public string? Detail => IsFolder ? string.Format(Strings.CountFiles, _count) : Location;

    public bool HasDetail => Detail is not null;

    public string? Location { get; }

    /// <summary>Recycle Bin items have no place in Explorer worth showing.</summary>
    public bool CanShow { get; }
}

public sealed record PreviewCrumb(PreviewFolder Folder, string Name, bool IsLast);
