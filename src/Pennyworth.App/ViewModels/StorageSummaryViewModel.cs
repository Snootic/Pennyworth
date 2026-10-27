using System.Globalization;
using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using Pennyworth.Core.Wording;
using Pennyworth.Platform;
using Pennyworth.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <param name="ColorKey">Looked up as the resource <c>Brush.Category.{ColorKey}</c>.</param>
public sealed record BarSegment(string ColorKey, double Fraction);

/// <summary>The plain-language summary of one drive. Updates live while the scan runs.</summary>
public sealed partial class StorageSummaryViewModel : ObservableObject
{
    private const int MaxAreasPerCategory = 10;

    private readonly ILocationInfo _locations;
    private readonly Action<StorageNode> _openFolder;
    private readonly Action _requestUnlock;
    private StorageTree? _tree;
    private long[] _categoryBytes = new long[StorageCategories.Count];
    private HiddenSpaceReport? _hidden;

    public StorageSummaryViewModel(DriveSummary drive, ILocationInfo locations, Action<StorageNode> openFolder, Action? requestUnlock = null, bool isSystemDrive = false)
    {
        Drive = drive;
        IsSystemDrive = isSystemDrive;
        _locations = locations;
        _openFolder = openFolder;
        _requestUnlock = requestUnlock ?? (() => { });
        Categories = StorageCategories.All.Select(category => new CategoryRowViewModel(category)).ToArray();
        Unaccounted = new CategoryRowViewModel(null);
        Legend = [.. Categories, Unaccounted];
        Update();
    }

    public DriveSummary Drive { get; }

    public StorageTree? Tree => _tree;

    public IReadOnlyList<CategoryRowViewModel> Categories { get; }

    /// <summary>"Not measured yet": used space the scan couldn't see.</summary>
    public CategoryRowViewModel Unaccounted { get; }

    /// <summary>Every row of the legend: the categories, then "not measured yet".</summary>
    public IReadOnlyList<CategoryRowViewModel> Legend { get; }

    public string UsedText => string.Format(Strings.DriveUsed, ByteSize.Format(Drive.UsedBytes), ByteSize.Format(Drive.TotalBytes));

    public string FreeText => string.Format(Strings.DriveFree, ByteSize.Format(Drive.FreeBytes));

    /// <summary>The scan's file count, as of the last progress report.</summary>
    [ObservableProperty]
    public partial long FileCount { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<BarSegment> Segments { get; private set; } = [];

    /// <summary>The scan counted more than the system reports as used (hard links).</summary>
    [ObservableProperty]
    public partial bool HasOvercount { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseAllCommand))]
    [NotifyPropertyChangedFor(nameof(ShowPlacesPending), nameof(ShowPlaces), nameof(ShowUnaccountedExplanation))]
    public partial bool IsComplete { get; private set; }

    /// <summary>When the scan finished, for "Scanned 2 min ago".</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>True once the hidden system space has been measured.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(_hidden))]
    public bool IsUnlocked => _hidden is not null;

    /// <summary>The system drive: the only one the hidden-space measurement covers.</summary>
    public bool IsSystemDrive { get; }

    public bool ShowUnlockBanner => IsSystemDrive && !IsUnlocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlacesTitle), nameof(ShowPlaces), nameof(ShowUnaccountedExplanation), nameof(SelectedColorKey))]
    public partial CategoryRowViewModel? SelectedCategory { get; set; }

    public string? SelectedColorKey => SelectedCategory?.ColorKey;

    /// <summary>Heading of the biggest-places panel, naming the selected category.</summary>
    public string PlacesTitle => SelectedCategory is { } selected
        ? string.Format(Strings.PlacesIn, selected.Name)
        : Strings.BiggestPlaces;

    public bool ShowPlacesPending => !IsComplete;

    public bool ShowPlaces => IsComplete && SelectedCategory != Unaccounted;

    public bool ShowUnaccountedExplanation => IsComplete && SelectedCategory == Unaccounted;

    /// <summary>The category using the most space, for the home screen teaser.</summary>
    public CategoryRowViewModel? Biggest => Categories.Where(row => row.Bytes > 0).MaxBy(row => row.Bytes);

    public void ApplyProgress(ScanProgress progress)
    {
        if (!IsComplete)
        {
            _categoryBytes = [.. progress.CategoryBytes];
            FileCount = progress.FileCount;
            Update();
        }
    }

    public void ApplyResult(StorageTree tree, DateTimeOffset? completedAt = null)
    {
        _tree = tree;
        _categoryBytes = Enumerable.Range(0, StorageCategories.Count)
            .Select(i => tree.GetCategoryBytes((StorageCategory)i))
            .ToArray();
        FileCount = tree.FileCount;
        CompletedAt = completedAt ?? DateTimeOffset.Now;
        Update();
        BuildAreas();
        IsComplete = true;
        SelectedCategory ??= Biggest;
    }

    /// <summary>Adds the measured hidden space: restore points and NTFS data on Windows, snapshots and filesystem metadata on Linux.</summary>
    public void ApplyHiddenSpace(HiddenSpaceReport report)
    {
        _hidden = report;
        Unaccounted.IsMeasured = true;
        Update();
        BuildAreas();
        OnPropertyChanged(nameof(IsUnlocked));
        OnPropertyChanged(nameof(ShowUnlockBanner));
    }

    /// <summary>Selects the category drawn with this colour key (a click on the bar).</summary>
    public void SelectByColorKey(string colorKey) =>
        SelectedCategory = Legend.FirstOrDefault(row => row.ColorKey == colorKey) ?? SelectedCategory;

    /// <summary>Re-reads every text after a language switch.</summary>
    public void RefreshTexts()
    {
        foreach (CategoryRowViewModel row in Legend)
        {
            row.RefreshTexts();
        }

        BuildAreas();
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void OpenArea(CategoryAreaViewModel area)
    {
        if (area.Node is { } node)
        {
            // A file such as hiberfil.sys opens the folder that holds it.
            _openFolder(node.IsDirectory ? node : node.Parent ?? node);
        }
    }

    [RelayCommand(CanExecute = nameof(IsComplete))]
    private void BrowseAll() => _openFolder(_tree!.Root);

    [RelayCommand]
    private void Unlock() => _requestUnlock();

    private void BuildAreas()
    {
        if (_tree is not { } tree)
        {
            return;
        }

        foreach (CategoryRowViewModel row in Categories)
        {
            var areas = tree.GetCategoryAreas(row.Category!.Value)
                .SelectMany(area => SplitIntoPlaces(tree, area, row.Category!.Value))
                .OrderByDescending(area => area.Bytes)
                .Take(MaxAreasPerCategory)
                .Select(area => CreateArea(area, row.Bytes))
                .ToList();
            if (row.Category == StorageCategory.HiddenSystem && _hidden is { } hidden)
            {
                string root = Drive.RootPath;
                if (hidden.RestorePoints is > 0 and var restore)
                {
                    areas.Add(new CategoryAreaViewModel(null, Where(root, PlatformText.RestorePointsLocation), restore, Localizer.Instance.Resolve(Strings.Place_RestorePoints), Localizer.Instance.Resolve(Strings.Place_RestorePointsHint), Share(restore, row.Bytes)));
                }

                if (hidden.NtfsMetadata is > 0 and var ntfs)
                {
                    areas.Add(new CategoryAreaViewModel(null, Where(root, PlatformText.MetadataLocation), ntfs, Localizer.Instance.Resolve(Strings.Place_Ntfs), Localizer.Instance.Resolve(Strings.Place_NtfsHint), Share(ntfs, row.Bytes)));
                }

                areas.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            }

            row.Areas = areas;
        }
    }

    private CategoryAreaViewModel CreateArea(CategoryArea area, long categoryBytes)
    {
        var description = _locations.Describe(area.Node.FullPath);
        return new CategoryAreaViewModel(
            area.Node, area.Node.FullPath, area.Bytes, description?.Title ?? area.Node.Name, description?.Description is { Length: > 0 } text ? text : null, Share(area.Bytes, categoryBytes));
    }

    /// <summary>
    /// A folder that holds one place per app (Flatpak data, /opt, caches) becomes one place per folder inside it, each with
    /// only the bytes of its own category, so a 40 GB lump turns into the apps that fill it. What lies directly in the folder
    /// stays as a place of its own. Any other area is returned as it is.
    /// </summary>
    private IEnumerable<CategoryArea> SplitIntoPlaces(StorageTree tree, CategoryArea area, StorageCategory category)
    {
        if (!area.Node.IsDirectory || !_locations.SplitsIntoPlaces(area.Node.FullPath))
        {
            yield return area;
            yield break;
        }

        long inside = 0;
        foreach (StorageNode child in area.Node.GetChildren())
        {
            if (!child.IsDirectory || child.Category != category)
            {
                continue;
            }

            foreach (CategoryArea place in SplitIntoPlaces(tree, new CategoryArea(child, tree.GetCategoryBytes(child, category)), category))
            {
                if (place.Bytes <= 0)
                {
                    continue;
                }

                inside += place.Bytes;
                yield return place;
            }
        }

        if (area.Bytes - inside is > 0 and var loose)
        {
            yield return new CategoryArea(area.Node, loose);
        }
    }

    /// <summary>Where a measured hidden area sits: a name inside the drive, or the drive itself when the platform has none.</summary>
    private static string Where(string root, PlatformText entry) =>
        Localizer.Instance.Text(entry) is { Length: > 0 } name ? Path.Combine(root, name) : root;

    private static double Share(long bytes, long total) => total == 0 ? 0 : (double)bytes / total;

    private void Update()
    {
        long[] bytes = (long[])_categoryBytes.Clone();
        bytes[(int)StorageCategory.HiddenSystem] += _hidden?.Total ?? 0;
        long scanned = bytes.Sum();
        long unaccounted = Drive.UsedBytes - scanned;
        HasOvercount = unaccounted < 0;
        long whole = Math.Max(Math.Max(Drive.UsedBytes, scanned), 1);

        foreach (CategoryRowViewModel row in Categories)
        {
            row.SetBytes(bytes[(int)row.Category!.Value], whole);
        }

        Unaccounted.SetBytes(Math.Max(unaccounted, 0), whole);
        Segments = Legend
            .Where(row => row.Bytes > 0)
            .Select(row => new BarSegment(row.ColorKey, row.Fraction))
            .ToArray();
        OnPropertyChanged(nameof(Biggest));
    }
}

public sealed partial class CategoryRowViewModel : ObservableObject
{
    /// <param name="category">Null for the "not measured yet" row.</param>
    public CategoryRowViewModel(StorageCategory? category)
    {
        Category = category;
        ColorKey = category is { } k ? CategoryText.ColorKey(k) : CategoryText.UnaccountedKey;
        Swatch = [new BarSegment(ColorKey, 1)];
    }

    public StorageCategory? Category { get; }

    /// <summary>For the "not measured yet" row: true once the full scan measured the hidden space.</summary>
    public bool IsMeasured { get; internal set; }

    public string Name => Category is { } c ? CategoryText.Name(c)
        : IsMeasured ? Strings.Category_UnaccountedMeasured : Strings.Category_Unaccounted;

    public string Hint => Category is { } c ? CategoryText.Hint(c)
        : IsMeasured ? Strings.CategoryHint_UnaccountedMeasured : Strings.CategoryHint_Unaccounted;

    public string ColorKey { get; }

    /// <summary>A single full segment, used to draw the category's swatch.</summary>
    public IReadOnlyList<BarSegment> Swatch { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long Bytes { get; private set; }

    /// <summary>Share of the drive's used space, 0 to 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial double Fraction { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<CategoryAreaViewModel> Areas { get; set; } = [];

    public string SizeText => ByteSize.Format(Bytes);

    public string PercentText => Fraction.ToString("P1", CultureInfo.CurrentCulture);

    internal void SetBytes(long bytes, long whole)
    {
        Bytes = bytes;
        Fraction = (double)bytes / whole;
    }

    internal void RefreshTexts() => OnPropertyChanged(string.Empty);
}

/// <summary>One of the biggest places of a category.</summary>
/// <param name="Node">The folder or file; null for places only the full scan measures (restore points).</param>
/// <param name="Share">Share of its category's bytes, 0 to 1, for the small size bar.</param>
public sealed record CategoryAreaViewModel(StorageNode? Node, string Path, long Bytes, string Title, string? Description, double Share = 0)
{
    public bool CanOpen => Node is not null;

    public string SizeText => ByteSize.Format(Bytes);
}
