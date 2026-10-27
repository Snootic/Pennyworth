using System.Globalization;
using Pennyworth.Cleanup;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Assistant;

/// <summary>
/// Turns a <see cref="PcSnapshot"/> into plain facts. Only what was measured becomes a fact;
/// nothing is guessed.
/// </summary>
public static class FactCollector
{
    /// <summary>How many recent samples count as "the last minute".</summary>
    public const int SustainedSamples = 60;

    public static IReadOnlyList<Fact> Collect(PcSnapshot snapshot, CultureInfo culture)
    {
        var facts = new List<Fact>();
        bool pt = culture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        string T(string en, string ptBr) => pt ? ptBr : en;
        void Add(string id, TopicKind group, string label, FactKind kind, double number, string? text = null) =>
            facts.Add(new Fact(id, group, label, kind, number, text));

        Storage(snapshot, culture, Add, T);
        Memory(snapshot, culture, Add, T);
        Processor(snapshot, culture, Add, T);
        Graphics(snapshot, Add, T);
        Power(snapshot, Add, T);
        Cleanup(snapshot, culture, Add, T);

        if (snapshot.Specs is { } specs)
        {
            Add("os.name", TopicKind.Overview, IsWindows(snapshot) ? "Windows" : T("Operating system", "Sistema operacional"), FactKind.Text, 0,
                string.Join(" ", new[] { specs.Os.Name, specs.Os.Version }.Where(s => !string.IsNullOrEmpty(s))));
            Add("os.uptime_hours", TopicKind.Overview, T("Hours since the PC was last restarted", "Horas desde que o PC foi reiniciado"), FactKind.Count, Math.Floor(specs.Os.Uptime.TotalHours));
        }

        Add("unlock.state", TopicKind.Overview, IsWindows(snapshot) || snapshot.Specs is null
            ? T("Full-scan permission granted (1 = yes)", "Permissão de verificação completa concedida (1 = sim)")
            : T("System space measured (1 = yes)", "Espaço do sistema medido (1 = sim)"), FactKind.Count, snapshot.IsUnlocked ? 1 : 0);
        return facts;
    }

    private static void Storage(PcSnapshot snapshot, CultureInfo culture, Action<string, TopicKind, string, FactKind, double, string?> add, Func<string, string, string> t)
    {
        // Only a measured OS gets a name. Without specs the facts say "system" rather than guess one.
        bool windows = IsWindows(snapshot);
        string os = windows ? "Windows" : "system";
        string osPt = windows ? "Windows" : "sistema";
        if (snapshot.Drives.FirstOrDefault(d => d.IsSystem) is { } system)
        {
            string name = DriveName(system.Root);
            add("disk.system.total", TopicKind.Storage, t($"Drive {name} ({os}) size", $"Tamanho do disco {name} ({osPt})"), FactKind.Bytes, system.TotalBytes, null);
            add("disk.system.used", TopicKind.Storage, t($"Drive {name} used", $"Disco {name} usado"), FactKind.Bytes, system.UsedBytes, null);
            add("disk.system.free", TopicKind.Storage, t($"Drive {name} free", $"Disco {name} livre"), FactKind.Bytes, system.FreeBytes, null);
            add("disk.system.used_percent", TopicKind.Storage, t($"Drive {name} full", $"Disco {name} ocupado"), FactKind.Percent, Math.Round(system.UsedPercent), null);

            if (snapshot.Specs?.Disks.FirstOrDefault(d => d.Letters.Contains(name, StringComparer.OrdinalIgnoreCase)) is { } disk)
            {
                string kind = disk.IsSsd switch
                {
                    true => "SSD",
                    false => t("hard disk (HDD)", "disco rígido (HDD)"),
                    null => t("unknown", "desconhecido"),
                };
                add("disk.system.kind", TopicKind.Storage, windows ? t("Windows is installed on", "O Windows está instalado em") : t("The operating system is installed on", "O sistema operacional está instalado em"), FactKind.Text, 0, $"{kind}, {disk.Model}, {disk.Bus}");
                add("disk.system.is_hdd", TopicKind.Storage, t($"{(windows ? "Windows" : "System")} drive is a hard disk (1 = yes)", $"Disco do {osPt} é HDD (1 = sim)"), FactKind.Count, disk.IsSsd == false ? 1 : 0, null);
            }
        }

        if (snapshot.Specs is { } specs)
        {
            var unused = specs.Disks.Where(d => d.IsSsd == true && d.Letters.Count == 0).ToList();
            add("disk.unused_ssd_count", TopicKind.Storage, windows ? t("SSDs with no drive letter (not in use)", "SSDs sem letra de unidade (sem uso)") : t("SSDs with nothing mounted (not in use)", "SSDs sem nada montado (sem uso)"), FactKind.Count, unused.Count, null);
            if (unused.Count > 0)
            {
                add("disk.unused_ssd", TopicKind.Storage, t("Unused SSD", "SSD sem uso"), FactKind.Text, 0,
                    string.Join("; ", unused.Select(d => $"{d.Model}, {ByteSize.Format(d.Size, culture)}")));
            }
        }

        foreach (DriveFact drive in snapshot.Drives.Where(d => !d.IsSystem))
        {
            string name = DriveName(drive.Root);
            string id = DriveId(name);
            add($"disk.{id}.used_percent", TopicKind.Storage, t($"Drive {name} full", $"Disco {name} ocupado"), FactKind.Percent, Math.Round(drive.UsedPercent), null);
            add($"disk.{id}.free", TopicKind.Storage, t($"Drive {name} free", $"Disco {name} livre"), FactKind.Bytes, drive.FreeBytes, null);
        }

        if (snapshot.SystemDriveCategories is { } categories)
        {
            foreach (var (category, bytes) in categories.Where(c => c.Value > 0).OrderByDescending(c => c.Value))
            {
                add($"storage.{category.ToString().ToLowerInvariant()}", TopicKind.Storage, t($"On the {os} drive: {CategoryName(category, false, windows)}", $"No disco do {osPt}: {CategoryName(category, true, windows)}"), FactKind.Bytes, bytes, null);
            }
        }
    }

    private static void Memory(PcSnapshot snapshot, CultureInfo culture, Action<string, TopicKind, string, FactKind, double, string?> add, Func<string, string, string> t)
    {
        if (snapshot.Latest is { } latest && latest.Memory.Total > 0)
        {
            MemoryStatus memory = latest.Memory;
            add("ram.total", TopicKind.Memory, t("Memory (RAM) installed", "Memória (RAM) instalada"), FactKind.Bytes, memory.Total, null);
            add("ram.used", TopicKind.Memory, t("Memory in use", "Memória em uso"), FactKind.Bytes, memory.Used, null);
            add("ram.used_percent", TopicKind.Memory, t("Memory in use", "Memória em uso"), FactKind.Percent, Math.Round(100.0 * memory.Used / memory.Total), null);
            add("ram.sustained_percent", TopicKind.Memory, t("Memory in use, average of the last minute", "Memória em uso, média do último minuto"), FactKind.Percent,
                Math.Round(Recent(snapshot).Where(s => s.Memory.Total > 0).Average(s => 100.0 * s.Memory.Used / s.Memory.Total)), null);

            var apps = latest.Apps.OrderByDescending(a => a.Memory).ToList();
            if (apps.Count > 0)
            {
                add("ram.top_apps", TopicKind.Memory, t("Apps using the most memory", "Apps que mais usam memória"), FactKind.Text, 0,
                    string.Join(", ", apps.Take(3).Select(a => $"{AppName(a, culture)} {ByteSize.Format(a.Memory, culture)}")));
                AppGroup top = apps[0];
                add("ram.top_app.bytes", TopicKind.Memory, t($"Memory used by {AppName(top, culture)}", $"Memória usada por {AppName(top, culture)}"), FactKind.Bytes, top.Memory, null);
                add("ram.top_app.share_percent", TopicKind.Memory, t($"Share of memory used by {AppName(top, culture)}", $"Parte da memória usada por {AppName(top, culture)}"), FactKind.Percent, Math.Round(100.0 * top.Memory / memory.Total), null);
                add("ram.top_app.can_close", TopicKind.Memory, t($"{AppName(top, culture)} can be closed (1 = yes)", $"{AppName(top, culture)} pode ser fechado (1 = sim)"), FactKind.Count, top.CanClose && !top.IsProtected ? 1 : 0, null);
            }
        }

        if (snapshot.Specs?.Memory is { } specs)
        {
            if (specs.Slots is int slots)
            {
                add("ram.slots_total", TopicKind.Memory, t("Memory slots", "Slots de memória"), FactKind.Count, slots, null);
                add("ram.slots_free", TopicKind.Memory, t("Free memory slots", "Slots de memória livres"), FactKind.Count, Math.Max(slots - specs.Modules.Count, 0), null);
            }

            if (specs.Modules.Count > 0)
            {
                add("ram.modules", TopicKind.Memory, t("Memory modules", "Módulos de memória"), FactKind.Text, 0,
                    string.Join("; ", specs.Modules.Select(m => string.Join(" ", new[] { ByteSize.Format(m.Size, culture), m.Type, m.SpeedMts is int s ? $"{s} MT/s" : null }.Where(x => x is not null)))));
            }
        }
    }

    private static void Processor(PcSnapshot snapshot, CultureInfo culture, Action<string, TopicKind, string, FactKind, double, string?> add, Func<string, string, string> t)
    {
        if (snapshot.Specs?.Cpu is { } cpu)
        {
            add("cpu.name", TopicKind.Processor, t("Processor", "Processador"), FactKind.Text, 0, cpu.Name);
            add("cpu.cores", TopicKind.Processor, t("Processor cores", "Núcleos do processador"), FactKind.Count, cpu.Cores, null);
            add("cpu.threads", TopicKind.Processor, t("Processor threads", "Threads do processador"), FactKind.Count, cpu.Threads, null);
        }

        if (snapshot.Latest is { } latest)
        {
            add("cpu.percent", TopicKind.Processor, t("Processor busy now", "Processador ocupado agora"), FactKind.Percent, Math.Round(latest.CpuPercent), null);
            add("cpu.sustained_percent", TopicKind.Processor, t("Processor busy, average of the last minute", "Processador ocupado, média do último minuto"), FactKind.Percent, Math.Round(Recent(snapshot).Average(s => s.CpuPercent)), null);
            var busy = latest.Apps.Where(a => a.Cpu >= 1).OrderByDescending(a => a.Cpu).Take(3).ToList();
            if (busy.Count > 0)
            {
                add("cpu.top_apps", TopicKind.Processor, t("Apps using the processor most", "Apps que mais usam o processador"), FactKind.Text, 0,
                    string.Join(", ", busy.Select(a => $"{AppName(a, culture)} {a.Cpu.ToString("0", culture)}%")));
            }
        }
    }

    private static void Graphics(PcSnapshot snapshot, Action<string, TopicKind, string, FactKind, double, string?> add, Func<string, string, string> t)
    {
        if (snapshot.Specs?.Gpus.OrderByDescending(g => g.Memory ?? 0).FirstOrDefault() is { } gpu)
        {
            add("gpu.name", TopicKind.Graphics, t("Graphics card", "Placa de vídeo"), FactKind.Text, 0, gpu.Name);
            if (gpu.Memory is long memory)
            {
                add("gpu.memory", TopicKind.Graphics, t("Video memory", "Memória de vídeo"), FactKind.Bytes, memory, null);
            }
        }

        if (snapshot.Latest?.GpuPercent is double percent)
        {
            add("gpu.percent", TopicKind.Graphics, t("Graphics card busy now", "Placa de vídeo ocupada agora"), FactKind.Percent, Math.Round(percent), null);
        }

        if ((snapshot.Specs?.Displays.FirstOrDefault(d => d.IsPrimary) ?? snapshot.Specs?.Displays.FirstOrDefault()) is { } display)
        {
            add("display.main", TopicKind.Graphics, t("Main display", "Tela principal"), FactKind.Text, 0, $"{display.Width} × {display.Height}, {display.RefreshHz} Hz");
        }

        if (snapshot.Specs?.Compositor is { } compositor)
        {
            add("display.compositor", TopicKind.Graphics, t("Compositor (what draws and manages the windows)", "Compositor (o que desenha e gerencia as janelas)"), FactKind.Text, 0, compositor.Text);
        }
    }

    private static void Power(PcSnapshot snapshot, Action<string, TopicKind, string, FactKind, double, string?> add, Func<string, string, string> t)
    {
        if (snapshot.Specs is not { } specs)
        {
            return;
        }

        bool laptop = specs.Battery is not null;
        add("pc.kind", TopicKind.Power, t("Kind of PC", "Tipo de PC"), FactKind.Text, 0, laptop ? t("Laptop", "Notebook") : t("Desktop", "Desktop"));
        add("pc.is_laptop", TopicKind.Power, t("Laptop (1 = yes)", "Notebook (1 = sim)"), FactKind.Count, laptop ? 1 : 0, null);
        if (specs.Battery is { } battery)
        {
            if (battery.Percent is int percent)
            {
                add("battery.percent", TopicKind.Power, t("Battery", "Bateria"), FactKind.Percent, percent, null);
            }

            add("battery.on_ac", TopicKind.Power, t("Plugged in (1 = yes)", "Na tomada (1 = sim)"), FactKind.Count, battery.OnAcPower ? 1 : 0, null);
            add("battery.charging", TopicKind.Power, t("Charging (1 = yes)", "Carregando (1 = sim)"), FactKind.Count, battery.IsCharging ? 1 : 0, null);
        }
    }

    private static void Cleanup(PcSnapshot snapshot, CultureInfo culture, Action<string, TopicKind, string, FactKind, double, string?> add, Func<string, string, string> t)
    {
        if (snapshot.Cleanup.Count == 0)
        {
            return;
        }

        long safe = snapshot.Cleanup.Where(e => e.IsAvailable && e.Rule.Safety == SafetyLevel.Safe).Sum(e => e.Bytes);
        add("cleanup.safe_bytes", TopicKind.Storage, t("Cleanup can free safely", "A limpeza pode liberar com segurança"), FactKind.Bytes, safe, null);
        foreach (CleanupEstimate estimate in snapshot.Cleanup.Where(e => e.Bytes > 0).OrderByDescending(e => e.Bytes))
        {
            string title = estimate.Rule.Title.Get(culture);
            string id = estimate.Rule.Id;
            add($"cleanup.item.{id}.bytes", TopicKind.CleanupItem, t($"Cleanup: {title}", $"Limpeza: {title}"), FactKind.Bytes, estimate.Bytes, null);
            add($"cleanup.item.{id}.items", TopicKind.CleanupItem, t($"Cleanup: {title}, files", $"Limpeza: {title}, arquivos"), FactKind.Count, estimate.Items, null);
        }
    }

    private static IEnumerable<MonitorSample> Recent(PcSnapshot snapshot) =>
        snapshot.RecentSamples.Skip(Math.Max(snapshot.RecentSamples.Count - SustainedSamples, 0));

    /// <summary>"C:" for <c>C:\</c>, "/home" for <c>/home/</c>; a root that is only separators stays as it is ("/").</summary>
    private static string DriveName(string root) => root.TrimEnd('\\', '/') is { Length: > 0 } trimmed ? trimmed : root;

    /// <summary>A fact id for a drive: "c" for "C:", "home" for "/home", "root" for "/".</summary>
    private static string DriveId(string name)
    {
        string id = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')).Trim('_');
        return id.Length > 0 ? id : "root";
    }

    /// <summary>
    /// Whether the measured OS is Windows. Without specs nothing is known, and the facts then use the
    /// neutral word "system" instead of asserting an OS that was not measured.
    /// </summary>
    private static bool IsWindows(PcSnapshot snapshot) =>
        snapshot.Specs?.Os.Name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) == true;

    private static string AppName(AppGroup app, CultureInfo culture) =>
        app.Explanation?.Title.Get(culture) ?? app.Description ?? app.Key;

    private static string CategoryName(StorageCategory category, bool pt, bool windows) => category switch
    {
        StorageCategory.Games => pt ? "jogos" : "games",
        StorageCategory.Apps => pt ? "apps" : "apps",
        StorageCategory.YourFiles => pt ? "seus arquivos" : "your files",
        StorageCategory.Windows => windows ? "Windows" : pt ? "sistema" : "system",
        StorageCategory.HiddenSystem => pt ? "sistema oculto" : "hidden system space",
        _ => pt ? "outros" : "other",
    };
}
