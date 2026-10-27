using System.Globalization;
using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.Assistant;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using Pennyworth.Core.Wording;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>The six parts of the 3D PC, in the order of the chips under it.</summary>
public enum PcPart
{
    Drive,
    Memory,
    Processor,
    Graphics,
    Power,
    Trash,
}

/// <summary>
/// 3D mode: an ASCII PC you can rotate, and a panel with live status for the selected part.
/// </summary>
public sealed partial class PcViewModel(MainWindowViewModel shell) : ObservableObject
{
    public const double MinZoom = 0.5;
    public const double MaxZoom = 2.6;

    private static readonly Dictionary<StorageCategory, char> CategoryChars = new()
    {
        [StorageCategory.Games] = '#',
        [StorageCategory.Apps] = '%',
        [StorageCategory.YourFiles] = '+',
        [StorageCategory.Windows] = '=',
        [StorageCategory.HiddenSystem] = '*',
        [StorageCategory.Other] = ':',
    };

    public LiveViewModel Live => shell.Live;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chips), nameof(PartIndexText), nameof(PartName), nameof(PartHint),
        nameof(IsDrive), nameof(IsMemory), nameof(IsProcessor), nameof(IsGraphics), nameof(IsPower), nameof(IsTrash))]
    public partial PcPart Part { get; set; }

    [ObservableProperty]
    public partial bool ShowHint { get; private set; } = true;

    /// <summary>Target zoom; the 3D view eases toward it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial double Zoom { get; set; } = 1;

    /// <summary>0 to 1: the drive drawing fills up when the drive part is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriveArt))]
    public partial double DriveArtProgress { get; private set; }

    public bool IsDrive => Part == PcPart.Drive;

    public bool IsMemory => Part == PcPart.Memory;

    public bool IsProcessor => Part == PcPart.Processor;

    public bool IsGraphics => Part == PcPart.Graphics;

    public bool IsPower => Part == PcPart.Power;

    public bool IsTrash => Part == PcPart.Trash;

    public string ZoomText => $"{Math.Round(Zoom * 100):0}%";

    /// <summary>Upper-case labels floating above the parts, in <see cref="PcPart"/> order.</summary>
    public IReadOnlyList<string> PartLabels => Enum.GetValues<PcPart>().Select(Label).ToArray();

    public IReadOnlyList<PartChipViewModel> Chips => Enum.GetValues<PcPart>()
        .Select(part => new PartChipViewModel(part, $"0{(int)part + 1}", Label(part), part == Part))
        .ToArray();

    public string PartIndexText => $"0{(int)Part + 1} / 06 · {Label(Part)}";

    public string PartName => Localizer.Instance[$"PartName_{Part}"];

    public string PartHint => Localizer.Instance[$"PartHint_{Part}"];

    public string MachineText => string.Format(Strings.Machine, shell.SystemDriveToken, Live.HasSample ? Live.MemoryTotalText : "—");

    // Drive
    public string DriveUsedText => shell.SystemDrive is { } d ? ByteSize.Format(d.Summary.UsedBytes) : "—";

    public string DriveFreeText => shell.SystemDrive is { } d
        ? $"{string.Format(Strings.DriveFree, ByteSize.Format(d.Summary.FreeBytes))} · {ByteSize.Format(d.Summary.TotalBytes)}"
        : "";

    public string DriveArt
    {
        get
        {
            var summary = shell.SystemSummary;
            long total = Math.Max(shell.SystemDrive?.Summary.TotalBytes ?? 1, 1);
            var parts = summary is null ? [] : summary.Categories
                .Select(row => (CategoryChars[row.Category!.Value], (double)row.Bytes / total))
                .Append(('-', (double)summary.Unaccounted.Bytes / total));
            return Ascii.DriveArt(parts, DriveArtProgress, shell.SystemDriveRoot);
        }
    }

    public IReadOnlyList<LegendLine> DriveLegend
    {
        get
        {
            if (shell.SystemSummary is not { } summary)
            {
                return [];
            }

            return summary.Categories
                .Select(row => new LegendLine(CategoryChars[row.Category!.Value].ToString(), row.Name, row.SizeText))
                .Append(new LegendLine("-", summary.Unaccounted.Name, summary.Unaccounted.SizeText))
                .Append(new LegendLine(".", Strings.MemFree, ByteSize.Format(summary.Drive.FreeBytes)))
                .ToArray();
        }
    }

    /// <summary>The five biggest places across all categories, with a 16-character bar.</summary>
    public IReadOnlyList<BarLine> TopFolders
    {
        get
        {
            var places = shell.SystemSummary?.Categories.SelectMany(row => row.Areas).OrderByDescending(a => a.Bytes).Take(5).ToArray() ?? [];
            long largest = Math.Max(places.FirstOrDefault()?.Bytes ?? 1, 1);
            return places.Select(place => new BarLine(place.Title, Ascii.Bar((double)place.Bytes / largest, 16), place.SizeText)).ToArray();
        }
    }

    // Memory
    public string MemoryAscii
    {
        get
        {
            var memory = Live.Memory;
            double total = Math.Max(memory.Total, 1);
            return "[" + Ascii.StackedBar(
                [('#', memory.InUse / total), ('%', memory.Compressed / total), (':', memory.Cached / total)], 32) + "]";
        }
    }

    public IReadOnlyList<LegendLine> MemoryLegend =>
    [
        new("#", Strings.InUse, Live.InUseText),
        new("%", Strings.Compressed, Live.CompressedText),
        new(":", Strings.Cached, Live.CachedText),
        new(".", Strings.MemFree, Live.FreeText),
    ];

    public IReadOnlyList<LegendLine> TopMemory => shell.Memory.Apps
        .OrderByDescending(app => app.Memory).Take(4)
        .Select(app => new LegendLine("", app.Name, app.MemoryText)).ToArray();

    // Processor
    public string CpuSpark => Ascii.Spark(Live.CpuHistory.Skip(LiveViewModel.CpuHistoryLength - 30), 25);

    public IReadOnlyList<LegendLine> TopCpu => shell.Memory.Apps
        .OrderByDescending(app => app.Cpu).Take(4)
        .Select(app => new LegendLine("", app.Name, app.CpuText)).ToArray();

    // Graphics
    public string GpuSpark => Ascii.Spark(Live.GpuHistory, 30);

    public bool IsGpuAvailable => Live.Gpu is not null;

    public string ShaderNote => shell.Cleanup.ShaderBytes > 0
        ? string.Format(Strings.ShaderNote, ByteSize.Format(shell.Cleanup.ShaderBytes))
        : Strings.ShaderCleaned;

    // Power
    public long HibernationBytes => shell.SystemSummary?.Tree?.Root.GetChildren()
        .Where(n => string.Equals(n.Name, Localizer.Instance.Text(PlatformText.HibernationFile), StringComparison.OrdinalIgnoreCase))
        .Select(n => n.SizeOnDisk)
        .FirstOrDefault() ?? 0;

    public bool HasHibernation => HibernationBytes > 0;

    /// <summary>The file the power panel names: the hibernation file on Windows, the swap file on Linux.</summary>
    public string HibernationFileName => Localizer.Instance.Text(PlatformText.HibernationFile);

    public string HibernationText => ByteSize.Format(HibernationBytes);

    /// <summary>The hibernation file next to the size of RAM it can hold.</summary>
    public string HibernationBar => Ascii.Bar(Live.Memory.Total == 0 ? 0 : (double)HibernationBytes / Live.Memory.Total, 12);

    public string PowerDiagram =>
        "  +----------------+\n" +
        "  | > ON   > SLEEP |--+\n" +
        "  | > HIBERNATE    |  | RAM -> DISK\n" +
        "  +----------------+  v\n" +
        $"       {Localizer.Instance.Text(PlatformText.HibernationFile)}  [{HibernationBar}]";

    // Trash
    public string CleanupTotalText => ByteSize.Format(shell.Cleanup.TotalBytes);

    public IReadOnlyList<CleanupItemViewModel> TrashItems => shell.Cleanup.Items;

    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Only the live panels, so the part chips aren't rebuilt every second.</summary>
    public void RefreshLive()
    {
        foreach (string name in (string[])[nameof(MachineText), nameof(MemoryAscii), nameof(MemoryLegend), nameof(TopMemory),
            nameof(CpuSpark), nameof(TopCpu), nameof(GpuSpark), nameof(IsGpuAvailable), nameof(HibernationBar), nameof(PowerDiagram)])
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Moves the drive drawing's fill animation on (1.6 s, easing out).</summary>
    public void AdvanceAnimation(TimeSpan sinceSelected)
    {
        double t = Math.Clamp(sinceSelected.TotalSeconds / 1.6, 0, 1);
        DriveArtProgress = 1 - Math.Pow(1 - t, 3);
    }

    partial void OnPartChanged(PcPart value)
    {
        ShowHint = false;
        DriveArtProgress = 0;
    }

    [RelayCommand]
    private void SelectPart(PcPart part) => Part = part;

    public bool CanAsk => shell.HasAssistant;

    [RelayCommand]
    private void AskAboutPart()
    {
        TopicKind topic = Part switch
        {
            PcPart.Memory => TopicKind.Memory,
            PcPart.Processor => TopicKind.Processor,
            PcPart.Graphics => TopicKind.Graphics,
            PcPart.Power => TopicKind.Power,
            _ => TopicKind.Storage,
        };
        shell.AskAbout(new AssistantTopic(topic), string.Format(CultureInfo.CurrentCulture, Strings.AskAboutSpec, PartName));
    }

    [RelayCommand]
    private void DismissHint() => ShowHint = false;

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Clamp(Zoom * 1.25, MinZoom, MaxZoom);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Clamp(Zoom / 1.25, MinZoom, MaxZoom);

    [RelayCommand]
    private void ZoomReset() => Zoom = 1;

    [RelayCommand]
    private void OpenStorage() => shell.ShowStorageCategory(null);

    [RelayCommand]
    private void OpenHidden() => shell.ShowStorageCategory(StorageCategory.HiddenSystem);

    [RelayCommand]
    private void OpenMemory() => shell.GoMemoryCommand.Execute(null);

    [RelayCommand]
    private void OpenCleanup() => shell.GoCleanupCommand.Execute(null);

    [RelayCommand]
    private void OpenSpecs() => shell.GoSpecsCommand.Execute(null);

    private static string Label(PcPart part) =>
        Localizer.Instance[$"PartLabel_{part}"].ToUpper(CultureInfo.CurrentCulture);
}

public sealed record PartChipViewModel(PcPart Part, string Index, string Label, bool IsSelected);

/// <param name="Mark">A legend character ("#") or empty.</param>
public sealed record LegendLine(string Mark, string Name, string Value);

public sealed record BarLine(string Name, string Bar, string Value);
