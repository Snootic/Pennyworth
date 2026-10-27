using System.Globalization;
using System.Text;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Assistant;
using Pennyworth.Core.Formatting;
using Pennyworth.Monitoring.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>Full specs of the PC, in numbered sections of plain-language rows.</summary>
public sealed partial class SpecsViewModel(ISpecsReader reader, IDialogHost host, IClipboardService? clipboard, IMemoryModuleReader? moduleReader = null) : ObservableObject
{
    private const string Unknown = "—";

    private MemoryModulesOutcome? _modulesFailure;
    private CancellationTokenSource? _modulesCancellation;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial PcSpecs? Specs { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<SpecSection> Sections { get; private set; } = [];

    public bool IsLoaded => Specs is not null;

    /// <summary>True while the password prompt for the memory modules is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModulesMessage), nameof(CanReadModules))]
    [NotifyCanExecuteChangedFor(nameof(ReadModulesCommand))]
    public partial bool IsReadingModules { get; private set; }

    /// <summary>The one line under the memory modules button: waiting for the password, or why nothing was read.</summary>
    public string? ModulesMessage => IsReadingModules
        ? Strings.SpecsModulesReading
        : _modulesFailure switch
        {
            MemoryModulesOutcome.Cancelled => Strings.SpecsModulesCancelled,
            MemoryModulesOutcome.Unavailable => Strings.SpecsModulesUnavailable,
            _ => null,
        };

    /// <summary>The button is offered only where this OS can ask, and only while the modules are unknown.</summary>
    public bool CanReadModules => !IsReadingModules && Specs is { } specs && OffersModules(specs);

    private bool OffersModules(PcSpecs specs) => moduleReader is not null && specs.Memory.Modules.Count == 0;

    /// <summary>
    /// Asks for the administrator's password, once, to read the memory modules from the firmware tables.
    /// Nothing is changed and nothing is kept.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReadModules))]
    private async Task ReadModules()
    {
        if (moduleReader is null || Specs is null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _modulesCancellation = cancellation;
        _modulesFailure = null;
        IsReadingModules = true;
        MemoryModulesResult result;
        try
        {
            result = await Task.Run(() => moduleReader.ReadAsync(cancellation.Token), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return; // The screen was left or the app is closing: no message for nobody.
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            result = MemoryModulesResult.Unavailable;
        }
        finally
        {
            _modulesCancellation = null;
            IsReadingModules = false;
        }

        if (result.Outcome == MemoryModulesOutcome.Read && Specs is { } specs)
        {
            PcSpecs merged = specs with { Memory = specs.Memory with { Slots = result.Slots ?? specs.Memory.Slots, Modules = result.Modules } };
            Specs = merged;
            Sections = Build(merged, OffersModules(merged));
            OnPropertyChanged(nameof(CanReadModules));
            ReadModulesCommand.NotifyCanExecuteChanged();
        }
        else
        {
            _modulesFailure = result.Outcome;
            OnPropertyChanged(nameof(ModulesMessage));
        }
    }

    /// <summary>Withdraws a password request still waiting for the person (the screen was left, or the app closes).</summary>
    public void CancelModulesRead() => _modulesCancellation?.Cancel();

    /// <summary>"Ask about this": set by the shell when the assistant is available.</summary>
    public Action<AssistantTopic, string>? AskAbout { get; set; }

    public bool CanAsk => AskAbout is not null;

    [RelayCommand]
    private void AskAboutSection(SpecSection section) =>
        AskAbout?.Invoke(new AssistantTopic(TopicKind.SpecSection, section.Index), string.Format(CultureInfo.CurrentCulture, Strings.AskAboutSpec, section.Title));

    /// <summary>The load started by <see cref="EnsureLoaded"/>; lets tests await it.</summary>
    public Task CurrentLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the hardware the first time the screen opens.</summary>
    public Task EnsureLoaded()
    {
        if (Specs is null && !IsLoading)
        {
            CurrentLoad = LoadAsync();
        }

        return CurrentLoad;
    }

    /// <summary>Rebuilds every text after a language switch.</summary>
    public void RefreshTexts()
    {
        if (Specs is { } specs)
        {
            Sections = Build(specs, OffersModules(specs));
            OnPropertyChanged(nameof(ModulesMessage));
        }
    }

    /// <summary>Everything as plain text, for "Copy all".</summary>
    public string CopyText
    {
        get
        {
            var text = new StringBuilder().AppendLine(Strings.SpecsTitle).AppendLine();
            foreach (SpecSection section in Sections)
            {
                text.AppendLine($"{section.Title.ToUpper(CultureInfo.CurrentCulture)}: {section.Headline}");
                foreach (SpecRow row in section.Rows)
                {
                    text.AppendLine($"  {row.Label}: {row.Value}");
                }

                text.AppendLine();
            }

            return text.ToString().TrimEnd();
        }
    }

    [RelayCommand]
    private async Task Copy()
    {
        if (clipboard is not null && Sections.Count > 0)
        {
            await clipboard.SetTextAsync(CopyText);
            host.ShowToast(Strings.CopiedToast);
        }
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            PcSpecs specs = await Task.Run(reader.Read);
            Specs = specs;
            Sections = Build(specs, OffersModules(specs));
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(CanReadModules));
            ReadModulesCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal static IReadOnlyList<SpecSection> Build(PcSpecs specs, bool offersModules = false) =>
    [
        SystemSection(specs),
        ProcessorSection(specs.Cpu),
        MemorySection(specs.Memory, offersModules),
        GraphicsSection(specs),
        StorageSection(specs.Disks),
        BoardSection(specs.Board),
        NetworkSection(specs.Networks),
        PowerSection(specs.Battery),
    ];

    private static SpecSection SystemSection(PcSpecs specs)
    {
        OsSpecs os = specs.Os;
        string headline = specs.System.Manufacturer is null && specs.System.Model is null
            ? specs.System.ComputerName
            : Join(" ", specs.System.Manufacturer, specs.System.Model);
        return new SpecSection("01", Strings.Spec_System, headline,
        [
            new(Strings.SpecComputer, specs.System.ComputerName),
            new(Strings.SpecUser, specs.System.UserName),
            new("Windows", Join(" · ", os.Name, os.Version)),
            new(Strings.SpecBuild, $"{os.Build} · {os.Architecture}"),
            new(Strings.SpecInstalled, os.InstalledOn?.ToString("d", CultureInfo.CurrentCulture) ?? Unknown),
            new(Strings.SpecUptime, Uptime(os.Uptime)),
        ]);
    }

    private static SpecSection ProcessorSection(CpuSpecs cpu) => new("02", Strings.Spec_Processor, cpu.Name,
    [
        new(Strings.SpecCores, string.Format(Strings.SpecCoresValue, cpu.Cores, cpu.Threads)),
        new(Strings.SpecBaseSpeed, cpu.BaseMhz is { } mhz ? $"{mhz / 1000.0:0.00} GHz" : Unknown),
        new(Strings.SpecCache, $"L1 {Bytes(cpu.L1)} · L2 {Bytes(cpu.L2)} · L3 {Bytes(cpu.L3)}"),
        new(Strings.SpecSocket, cpu.Socket ?? Unknown),
        new(Strings.SpecArchitecture, Join(" · ", cpu.Architecture, cpu.Vendor)),
    ]);

    private static SpecSection MemorySection(MemorySpecs memory, bool offersModules)
    {
        var rows = new List<SpecRow>();
        if (memory.Slots is { } slots && memory.Modules.Count > 0)
        {
            rows.Add(new(Strings.SpecSlots, string.Format(Strings.SpecSlotsValue, memory.Modules.Count, slots)));
        }

        foreach (MemoryModule module in memory.Modules)
        {
            string speed = module.SpeedMts is { } rated
                ? module.ConfiguredMts is { } running && running != rated
                    ? $"{rated} MT/s ({string.Format(Strings.SpecRunningAt, $"{running} MT/s")})"
                    : $"{rated} MT/s"
                : "";
            rows.Add(new(module.Slot, Join(" · ", Bytes(module.Size), module.Type, speed, module.FormFactor, Parts(" ", module.Manufacturer, module.PartNumber))));
        }

        return new SpecSection("03", Strings.Spec_Memory, $"{Math.Round(memory.Total / (1024d * 1024 * 1024)):0} GB", rows, offersModules);
    }

    private static SpecSection GraphicsSection(PcSpecs specs)
    {
        var rows = new List<SpecRow>();
        foreach (GpuSpecs gpu in specs.Gpus)
        {
            if (specs.Gpus.Count > 1)
            {
                rows.Add(new(Strings.Spec_Graphics, gpu.Name));
            }

            // Drivers report slightly less than the card is sold with (7.96 GB for an 8 GB card).
            rows.Add(new(Strings.SpecVideoMemory, gpu.Memory is { } vram ? $"{Math.Round(vram / (1024d * 1024 * 1024)):0} GB" : Unknown));
            rows.Add(new(Strings.SpecDriver, Join(" · ", gpu.DriverVersion, gpu.DriverDate)));
        }

        int number = 1;
        foreach (DisplaySpecs display in specs.Displays.OrderByDescending(d => d.IsPrimary))
        {
            rows.Add(new(
                string.Format(Strings.SpecDisplay, number++),
                Join(" · ", $"{display.Width} × {display.Height}", $"{display.RefreshHz} Hz", display.IsPrimary ? Strings.SpecMainDisplay : null)));
        }

        if (specs.Compositor is { } compositor)
        {
            rows.Add(new(Strings.SpecCompositor, compositor.Text));
        }

        return new SpecSection("04", Strings.Spec_Graphics, specs.Gpus.FirstOrDefault()?.Name ?? Unknown, rows);
    }

    private static SpecSection StorageSection(IReadOnlyList<DiskSpecs> disks)
    {
        var rows = disks.Select(disk => new SpecRow(
            string.Format(Strings.SpecDisk, disk.Number),
            Join(" · ",
                disk.Model,
                Bytes(disk.Size),
                disk.IsSsd switch { true => "SSD", false => "HDD", null => null },
                disk.Bus,
                disk.Letters.Count > 0 ? string.Join(", ", disk.Letters) : Strings.SpecNoLetter))).ToArray();
        string headline = string.Format(Strings.SpecDisksHeadline, Bytes(disks.Sum(d => d.Size)), disks.Count);
        return new SpecSection("05", Strings.Spec_Storage, headline, rows);
    }

    private static SpecSection BoardSection(BoardSpecs board) => new("06", Strings.Spec_Board,
        Join(" ", board.Manufacturer, board.Product),
    [
        new(Strings.SpecMaker, board.Manufacturer ?? Unknown),
        new(Strings.SpecModel, board.Product ?? Unknown),
        new(Strings.SpecBios, Join(" · ", board.BiosVendor, board.BiosVersion)),
        new(Strings.SpecBiosDate, board.BiosDate ?? Unknown),
    ]);

    private static SpecSection NetworkSection(IReadOnlyList<NetworkSpecs> found)
    {
        // Real connections (Ethernet, Wi-Fi) before VPNs, whose "speed" is only nominal.
        var networks = found.OrderBy(n => n.Kind == "VPN").ToArray();
        var rows = networks.Select(n => new SpecRow(n.Name, Join(" · ", n.Description, Speed(n.SpeedBps), n.IPv4, n.Mac))).ToArray();
        return new SpecSection("07", Strings.Spec_Network,
            networks.FirstOrDefault() is { } main ? Join(" · ", main.Kind, Speed(main.SpeedBps)) : Strings.SpecNoNetwork, rows);
    }

    private static SpecSection PowerSection(BatterySpecs? battery) => battery is null
        ? new SpecSection("08", Strings.Spec_Power, Strings.SpecDesktop, [new(Strings.SpecBattery, Strings.SpecNoBattery)])
        : new SpecSection("08", Strings.Spec_Power, battery.Percent is { } percent ? $"{percent}%" : Unknown,
        [
            new(Strings.SpecBattery, battery.IsCharging ? Strings.SpecCharging : battery.OnAcPower ? Strings.SpecPluggedIn : Strings.SpecOnBattery),
        ]);

    private static string Bytes(long bytes) => bytes > 0 ? ByteSize.Format(bytes) : Unknown;

    private static string? Speed(long bps) => bps switch
    {
        <= 0 => null,
        >= 1_000_000_000 => $"{bps / 1_000_000_000.0:0.#} Gbps",
        _ => $"{bps / 1_000_000.0:0} Mbps",
    };

    private static string Uptime(TimeSpan uptime) => uptime.TotalDays >= 1
        ? string.Format(Strings.UptimeDays, (int)uptime.TotalDays, uptime.Hours)
        : string.Format(Strings.UptimeHours, uptime.Hours, uptime.Minutes);

    /// <summary>Joins the parts that are known; "—" when none are.</summary>
    private static string Join(string separator, params string?[] parts) => Parts(separator, parts) ?? Unknown;

    /// <summary>Joins the parts that are known; null when none are (for use inside another join).</summary>
    private static string? Parts(string separator, params string?[] parts)
    {
        string joined = string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return joined.Length > 0 ? joined : null;
    }
}

/// <param name="Index">"01".</param>
/// <param name="Headline">The one thing to know about this part ("AMD Ryzen 5 5600").</param>
/// <param name="OffersModules">The memory section, when the modules are unknown and the person can ask to read them.</param>
public sealed record SpecSection(string Index, string Title, string Headline, IReadOnlyList<SpecRow> Rows, bool OffersModules = false);

public sealed record SpecRow(string Label, string Value);
