using Pennyworth.App.Resources;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>What the folder browser needs from the rest of the app.</summary>
public interface IFolderHost
{
    IReadOnlyList<DriveViewModel> Drives { get; }

    /// <summary>The finished scan of a drive, or null when it hasn't been scanned yet.</summary>
    StorageTree? TreeFor(DriveViewModel drive);

    /// <summary>Opening a drive that hasn't been scanned starts its scan.</summary>
    void ScanDrive(DriveViewModel drive);

    void ShowInExplorer(string path);

    void RequestUnlock();
}

/// <summary>Drill-down like Explorer, from "This PC" through drives and folders, largest first.</summary>
public sealed partial class FolderBrowserViewModel : ObservableObject
{
    private readonly IFolderHost _host;
    private readonly Stack<StorageNode?> _history = new();

    public FolderBrowserViewModel(IFolderHost host)
    {
        _host = host;
    }

    /// <summary>The open folder; null means "This PC" (the list of drives).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Items), nameof(Breadcrumbs), nameof(Title), nameof(SummaryText), nameof(IsEmpty), nameof(IsLockedFolder), nameof(IsMeasuredLockedFolder), nameof(LockedMeasuredBody), nameof(HasItems), nameof(IsThisPc))]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    public partial StorageNode? Current { get; private set; }

    public bool IsThisPc => Current is null;

    public IReadOnlyList<FolderItemViewModel> Items
    {
        get
        {
            if (Current is not { } folder)
            {
                return _host.Drives.Select(FolderItemViewModel.ForDrive).ToArray();
            }

            var children = folder.GetChildren();
            long largest = Math.Max(children.Where(child => !IsUnmeasured(child)).Select(child => child.SizeOnDisk).DefaultIfEmpty(0).Max(), 1);
            return children
                .OrderBy(IsUnmeasured) // Locked folders nobody measured have no size; they go last, the measured ones sit by size.
                .Select(child => FolderItemViewModel.ForNode(child, largest))
                .ToArray();
        }
    }

    public IReadOnlyList<BreadcrumbViewModel> Breadcrumbs
    {
        get
        {
            var crumbs = new List<BreadcrumbViewModel>();
            for (StorageNode? node = Current; node is { } current; node = current.Parent)
            {
                string label = current.Parent is null ? DriveLabel(current) : current.Name;
                crumbs.Insert(0, new BreadcrumbViewModel(current, label));
            }

            crumbs.Insert(0, new BreadcrumbViewModel(null, Strings.ThisPc));
            return crumbs.Select((crumb, i) => crumb with { IsLast = i == crumbs.Count - 1 }).ToArray();
        }
    }

    public string Title => Breadcrumbs[^1].Name;

    public string SummaryText => Current is { } folder
        ? string.Format(Strings.FolderSummary, ByteSize.Format(folder.SizeOnDisk), folder.GetChildren().Count)
        : string.Format(Strings.DriveCount, _host.Drives.Count);

    public bool HasItems => Current is not { } folder || folder.HasChildren;

    /// <summary>Nothing inside, and not because Windows refused to list it.</summary>
    public bool IsEmpty => Current is { } folder && !folder.HasChildren && !folder.IsLocked;

    /// <summary>A folder Windows or Linux refuses to list, whose size is not known either.</summary>
    public bool IsLockedFolder => Current is { IsLocked: true, IsMeasured: false };

    /// <summary>A folder that cannot be listed but whose size was measured with an administrator's rights.</summary>
    public bool IsMeasuredLockedFolder => Current is { IsLocked: true, IsMeasured: true };

    /// <summary>"The system keeps this folder closed to you. Its size (3 GB) was measured with your password."</summary>
    public string LockedMeasuredBody => string.Format(Strings.LockedMeasuredBody, Current is { } folder ? ByteSize.Format(folder.SizeOnDisk) : "");

    private static bool IsUnmeasured(StorageNode node) => node.IsLocked && !node.IsMeasured;

    public bool CanGoBack => _history.Count > 0;

    /// <summary>Opens a folder (or "This PC" for null). Opening from elsewhere in the app starts a fresh history.</summary>
    public void Show(StorageNode? location, bool resetHistory)
    {
        if (resetHistory)
        {
            _history.Clear();
        }
        else if (!Equals(location, Current))
        {
            _history.Push(Current);
        }

        Current = location;
        BackCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoBack));
    }

    /// <summary>Re-reads every text after a language switch or a finished scan.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void Open(FolderItemViewModel item)
    {
        if (item.Drive is { } drive)
        {
            if (_host.TreeFor(drive) is { } tree)
            {
                Show(tree.Root, resetHistory: false);
            }
            else
            {
                _host.ScanDrive(drive);
            }
        }
        else if (item.Node is { IsDirectory: true } node)
        {
            Show(node, resetHistory: false);
        }
    }

    [RelayCommand]
    private void NavigateTo(BreadcrumbViewModel crumb)
    {
        if (!crumb.IsLast)
        {
            Show(crumb.Node, resetHistory: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up() => Show(Current!.Value.Parent, resetHistory: false);

    private bool CanGoUp() => Current is not null;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (_history.TryPop(out StorageNode? previous))
        {
            Current = previous;
            BackCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanGoBack));
        }
    }

    [RelayCommand]
    private void ShowInExplorer(FolderItemViewModel item) => _host.ShowInExplorer(item.Path);

    [RelayCommand]
    private void Unlock() => _host.RequestUnlock();

    private string DriveLabel(StorageNode root)
    {
        var drive = _host.Drives.FirstOrDefault(d => string.Equals(d.Summary.RootPath, root.Tree.RootPath, StringComparison.OrdinalIgnoreCase));
        return drive?.Name ?? root.Name;
    }
}

public enum FolderItemKind
{
    Folder,
    File,
    Drive,
}

/// <summary>One row of the folder browser: a drive (on "This PC"), a folder or a file.</summary>
/// <param name="IsLocked">The scan could not list the folder. It stays true after its size was measured with a password.</param>
/// <param name="IsMeasured">A locked folder whose size is known because it was measured with an administrator's rights.</param>
public sealed record FolderItemViewModel(
    string Name,
    string Path,
    FolderItemKind Kind,
    bool IsLocked,
    string SizeText,
    double Fraction,
    StorageNode? Node,
    DriveViewModel? Drive,
    bool IsMeasured = false)
{
    public bool IsFolder => Kind == FolderItemKind.Folder;

    public bool IsFile => Kind == FolderItemKind.File;

    public bool IsDrive => Kind == FolderItemKind.Drive;

    public bool CanOpen => Kind != FolderItemKind.File;

    /// <summary>The "Locked" badge: locked and its size still unknown.</summary>
    public bool ShowLockedBadge => IsLocked && !IsMeasured;

    /// <param name="largest">The largest sibling: the design scales share bars to it.</param>
    public static FolderItemViewModel ForNode(StorageNode node, long largest)
    {
        bool unknown = node.IsLocked && !node.IsMeasured;
        return new(
            node.Name,
            node.FullPath,
            node.IsDirectory ? FolderItemKind.Folder : FolderItemKind.File,
            node.IsLocked,
            unknown ? "—" : ByteSize.Format(node.SizeOnDisk),
            unknown ? 0 : (double)node.SizeOnDisk / largest,
            node,
            null,
            node.IsLocked && node.IsMeasured);
    }

    /// <summary>Drives show their used space as Windows reports it, scanned or not.</summary>
    public static FolderItemViewModel ForDrive(DriveViewModel drive) => new(
        drive.Name,
        drive.Summary.RootPath,
        FolderItemKind.Drive,
        IsLocked: false,
        ByteSize.Format(drive.Summary.UsedBytes),
        drive.UsedFraction,
        null,
        drive);
}

/// <param name="Node">Null for "This PC".</param>
public sealed record BreadcrumbViewModel(StorageNode? Node, string Name, bool IsLast = false);
