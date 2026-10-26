using Pennyworth.Monitoring.Hardware;
using static Pennyworth.Assistant.Tests.Samples;

namespace Pennyworth.Assistant.Tests;

public class FactCollectorTests
{
    [Fact]
    public void Describes_the_system_drive_and_what_fills_it()
    {
        var facts = FactCollector.Collect(Snapshot(diskUsedPercent: 92), En);

        Assert.Equal(92, facts.Get("disk.system.used_percent").Number);
        Assert.Equal("92%", facts.Get("disk.system.used_percent").Format(En));
        Assert.Equal("Drive C: full", facts.Get("disk.system.used_percent").Label);
        Assert.Equal("hard disk (HDD), ST1500DL003, SATA", facts.Get("disk.system.kind").Text);
        Assert.Equal(1, facts.Get("disk.system.is_hdd").Number);
        Assert.Equal(1, facts.Get("disk.unused_ssd_count").Number);
        Assert.Equal(90, facts.Get("disk.d.used_percent").Number);
        Assert.Equal(412 * Gb, facts.Get("storage.games").Number);
        Assert.DoesNotContain(facts, f => f.Id == "storage.other"); // empty categories are left out
    }

    [Fact]
    public void Names_Windows_when_the_specs_say_it_is_Windows()
    {
        var facts = FactCollector.Collect(Snapshot(), En);

        Assert.Equal("Windows", facts.Get("os.name").Label);
        Assert.Equal("Drive C: (Windows) size", facts.Get("disk.system.total").Label);
        Assert.Equal("Windows is installed on", facts.Get("disk.system.kind").Label);
        Assert.Equal("On the Windows drive: games", facts.Get("storage.games").Label);
        Assert.Equal("No disco do Windows: jogos", FactCollector.Collect(Snapshot(), Pt).Get("storage.games").Label);
    }

    [Fact]
    public void Never_calls_another_system_Windows()
    {
        PcSpecs linux = Specs() with
        {
            Os = new OsSpecs("openSUSE Tumbleweed", "20261024", "7.2.11", "X64", null, TimeSpan.FromHours(5)),
            Disks = [new DiskSpecs(0, "WD Blue", 500 * Gb, "NVMe", true, ["/"]), new DiskSpecs(1, "Seagate", 1000 * Gb, "SATA", false, ["/home"])],
        };
        var snapshot = Snapshot(specs: linux) with
        {
            Drives = [new DriveFact("/", "/", 953 * Gb, 400 * Gb, true), new DriveFact("/home", "/home", 1000 * Gb, 100 * Gb, false)],
        };

        foreach (var culture in new[] { En, Pt })
        {
            var facts = FactCollector.Collect(snapshot, culture);

            Assert.DoesNotContain(facts, f => f.Label.Contains("Windows") || (f.Text ?? "").Contains("Windows"));
        }

        var english = FactCollector.Collect(snapshot, En);
        Assert.Equal("Operating system", english.Get("os.name").Label);
        Assert.Equal("openSUSE Tumbleweed 20261024", english.Get("os.name").Text);
        Assert.Equal("Drive / (system) size", english.Get("disk.system.total").Label);
        Assert.Equal("NVMe", english.Get("disk.system.kind").Text!.Split(", ")[^1]);
        Assert.Equal("The operating system is installed on", english.Get("disk.system.kind").Label);
        Assert.Equal("On the system drive: games", english.Get("storage.games").Label);
        Assert.Equal("Drive /home full", english.Get("disk.home.used_percent").Label);
        Assert.Equal("No disco do sistema: jogos", FactCollector.Collect(snapshot, Pt).Get("storage.games").Label);
    }

    [Fact]
    public void Without_specs_no_operating_system_is_assumed()
    {
        var facts = FactCollector.Collect(Snapshot() with { Specs = null }, En);

        Assert.DoesNotContain(facts, f => f.Label.Contains("Windows"));
        Assert.Equal("Drive C: (system) size", facts.Get("disk.system.total").Label);
    }

    [Fact]
    public void Averages_memory_and_processor_over_the_last_minute_only()
    {
        var snapshot = Snapshot() with
        {
            RecentSamples = [.. Enumerable.Repeat(Sample(10, cpu: 0), 100), .. Enumerable.Repeat(Sample(90, cpu: 50), 60)],
        };

        var facts = FactCollector.Collect(snapshot, En);

        Assert.Equal(90, facts.Get("ram.sustained_percent").Number);
        Assert.Equal(50, facts.Get("cpu.sustained_percent").Number);
    }

    [Fact]
    public void Names_the_apps_using_the_most_memory()
    {
        var facts = FactCollector.Collect(
            Snapshot(apps: [App("discord", Gb), App("chrome", 4 * Gb, cpu: 12), App("svchost", 2 * Gb, canClose: false, title: "Windows services")]),
            En);

        Assert.Equal("Google Chrome 4.00 GB, Windows services 2.00 GB, discord 1.00 GB", facts.Get("ram.top_apps").Text);
        Assert.Equal(25, facts.Get("ram.top_app.share_percent").Number);
        Assert.Equal(1, facts.Get("ram.top_app.can_close").Number);
        Assert.Equal("Google Chrome 12%", facts.Get("cpu.top_apps").Text);
    }

    [Fact]
    public void Knows_laptops_and_their_battery()
    {
        var facts = FactCollector.Collect(Snapshot(specs: Specs(new BatterySpecs(18, IsCharging: false, OnAcPower: false))), Pt);

        Assert.Equal("Notebook", facts.Get("pc.kind").Text);
        Assert.Equal(18, facts.Get("battery.percent").Number);
        Assert.Equal(0, facts.Get("battery.on_ac").Number);
    }

    [Fact]
    public void Reports_cleanup_and_formats_in_portuguese()
    {
        var facts = FactCollector.Collect(Snapshot(), Pt);

        Assert.Equal("18,0 GB", facts.Get("cleanup.safe_bytes").Format(Pt));
        Assert.Equal("67.808", facts.Get("cleanup.item.temp.items").Format(Pt));
        Assert.Equal("Limpeza: temp arquivos", facts.Get("cleanup.item.temp.bytes").Label);
        Assert.Equal(1, facts.Get("ram.slots_free").Number);
        Assert.Equal("16,0 GB DDR4 3200 MT/s", facts.Get("ram.modules").Text);
        Assert.Equal(4 * Gb, facts.Get("gpu.memory").Number); // the card with the most memory
        Assert.Equal(200, facts.Get("os.uptime_hours").Number);
    }

    [Fact]
    public void Missing_data_means_fewer_facts_not_errors()
    {
        var facts = FactCollector.Collect(PcSnapshot.Empty, En);

        Assert.Equal(["unlock.state"], facts.Select(f => f.Id));
    }

    [Fact]
    public void Fact_ids_are_unique()
    {
        var facts = FactCollector.Collect(Snapshot(apps: [App("chrome", Gb)]), En);

        Assert.Equal(facts.Count, facts.Select(f => f.Id).Distinct().Count());
    }
}
