using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public class LinuxGpuMeterTests
{
    [Fact]
    public void The_busiest_gpu_is_reported_and_clamped()
    {
        Assert.Equal(70, LinuxGpuMeter.Combine([10, 70, 30]));
        Assert.Equal(100, LinuxGpuMeter.Combine([250]));
        Assert.Equal(0, LinuxGpuMeter.Combine([-5]));
        Assert.Null(LinuxGpuMeter.Combine([]));
    }

    [Fact]
    public void Nvidia_smi_prints_one_gpu_per_line_and_unsupported_ones_are_left_out()
    {
        Assert.Equal([37, 5], LinuxGpuMeter.ParseNvidiaSmi("37\n5\n"));
        Assert.Equal([12.5], LinuxGpuMeter.ParseNvidiaSmi("[N/A]\n12.5\n[Not Supported]\n"));
        Assert.Empty(LinuxGpuMeter.ParseNvidiaSmi(""));
        Assert.Empty(LinuxGpuMeter.ParseNvidiaSmi("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.\n"));
    }

    [Fact]
    public void A_program_is_found_on_the_first_path_entry_that_has_it()
    {
        using var system = new FakeProcFs();
        string first = Path.Combine(system.Root, "bin1");
        string second = Path.Combine(system.Root, "bin2");
        system.Write(Path.Combine(second, "nvidia-smi"), "");

        Assert.Equal(Path.Combine(second, "nvidia-smi"), LinuxGpuMeter.FindOnPath("nvidia-smi", first + ":" + second));
        Assert.Null(LinuxGpuMeter.FindOnPath("nvidia-smi", first));
        Assert.Null(LinuxGpuMeter.FindOnPath("nvidia-smi", ""));
        Assert.Null(LinuxGpuMeter.FindOnPath("nvidia-smi", null));
    }

    private static LinuxGpuMeter Meter(FakeProcFs system, TimeProvider? clock = null, string? path = "", Func<string, IReadOnlyList<string>, TimeSpan, string?>? run = null) =>
        new(new ProcFileSystem(system.Proc), system.Sys, clock ?? new StepClock(), path, run);

    private static void Card(FakeProcFs system, string name, string vendor, string? busyPercent = null)
    {
        system.WriteSys(Path.Combine("class", "drm", name, "device", "vendor"), vendor + "\n");
        if (busyPercent is not null)
        {
            system.WriteSys(Path.Combine("class", "drm", name, "device", "gpu_busy_percent"), busyPercent + "\n");
        }
    }

    [Fact]
    public void An_amd_card_reports_its_busy_percent_and_the_busiest_of_several_wins()
    {
        using var system = new FakeProcFs();
        Card(system, "card0", "0x1002", "37");
        Card(system, "card1", "0x1002", "81");
        Card(system, "card0-DP-1", "0x1002", "99"); // a connector of card0, not a GPU

        Assert.Equal(81, Meter(system).Sample([]));
    }

    [Fact]
    public void An_unreadable_busy_value_is_ignored()
    {
        using var system = new FakeProcFs();
        Card(system, "card0", "0x1002", "not a number");

        Assert.Null(Meter(system).Sample([]));
    }

    [Fact]
    public void An_intel_card_without_clients_has_no_load_to_report()
    {
        using var system = new FakeProcFs();
        Card(system, "card0", "0x8086");
        LinuxGpuMeter meter = Meter(system);

        Assert.True(meter.IsAvailable);
        Assert.Null(meter.Sample([]));
        Assert.Null(meter.Sample([]));
    }

    [Fact]
    public void A_machine_without_any_gpu_source_is_not_available_and_reports_null()
    {
        using var system = new FakeProcFs();
        LinuxGpuMeter meter = Meter(system);

        Assert.False(meter.IsAvailable);
        Assert.Null(meter.Sample([]));
    }

    private static void NvidiaDevice(FakeProcFs system, string vendor = "0x10de", string pciClass = "0x030000")
    {
        system.WriteSys(Path.Combine("bus", "pci", "devices", "0000:01:00.0", "vendor"), vendor + "\n");
        system.WriteSys(Path.Combine("bus", "pci", "devices", "0000:01:00.0", "class"), pciClass + "\n");
    }

    [Fact]
    public void Nvidia_load_comes_from_nvidia_smi_when_an_nvidia_display_device_exists()
    {
        using var system = new FakeProcFs();
        NvidiaDevice(system);
        system.Write(Path.Combine(system.Root, "bin", "nvidia-smi"), "");
        var calls = new List<string>();
        LinuxGpuMeter meter = Meter(system, path: Path.Combine(system.Root, "bin"), run: (file, arguments, _) =>
        {
            calls.Add(file + " " + string.Join(' ', arguments));
            return "42\n";
        });

        Assert.Equal(42, meter.Sample([]));
        Assert.Equal([Path.Combine(system.Root, "bin", "nvidia-smi") + " --query-gpu=utilization.gpu --format=csv,noheader,nounits"], calls);
    }

    [Theory]
    [InlineData("0x8086", "0x030000")] // not NVIDIA
    [InlineData("0x10de", "0x040300")] // NVIDIA's HDMI audio function, not a display controller
    public void Nvidia_smi_is_not_started_without_an_nvidia_display_device(string vendor, string pciClass)
    {
        using var system = new FakeProcFs();
        NvidiaDevice(system, vendor, pciClass);
        system.Write(Path.Combine(system.Root, "bin", "nvidia-smi"), "");
        int calls = 0;
        LinuxGpuMeter meter = Meter(system, path: Path.Combine(system.Root, "bin"), run: (_, _, _) => { calls++; return "42\n"; });

        Assert.Null(meter.Sample([]));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_missing_nvidia_smi_is_remembered_and_never_started()
    {
        using var system = new FakeProcFs();
        NvidiaDevice(system);
        int calls = 0;
        LinuxGpuMeter meter = Meter(system, path: Path.Combine(system.Root, "empty"), run: (_, _, _) => { calls++; return "42\n"; });

        Assert.False(meter.IsAvailable);
        Assert.Null(meter.Sample([]));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_failing_nvidia_smi_is_not_retried_for_30_seconds()
    {
        using var system = new FakeProcFs();
        NvidiaDevice(system);
        system.Write(Path.Combine(system.Root, "bin", "nvidia-smi"), "");
        var clock = new StepClock();
        int calls = 0;
        string? answer = null;
        LinuxGpuMeter meter = Meter(system, clock, Path.Combine(system.Root, "bin"), (_, _, _) => { calls++; return answer; });

        Assert.Null(meter.Sample([]));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(meter.Sample([]));
        clock.Advance(TimeSpan.FromSeconds(28));
        Assert.Null(meter.Sample([]));
        Assert.Equal(1, calls);

        answer = "9\n";
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(9, meter.Sample([]));
        Assert.Equal(2, calls);

        clock.Advance(TimeSpan.FromSeconds(1)); // a working nvidia-smi runs every sample
        Assert.Equal(9, meter.Sample([]));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void An_nvidia_and_an_amd_gpu_report_the_busier_one()
    {
        using var system = new FakeProcFs();
        NvidiaDevice(system);
        Card(system, "card0", "0x1002", "20");
        system.Write(Path.Combine(system.Root, "bin", "nvidia-smi"), "");
        LinuxGpuMeter meter = Meter(system, path: Path.Combine(system.Root, "bin"), run: (_, _, _) => "64\n");

        Assert.Equal(64, meter.Sample([]));
    }

    [LinuxFact]
    public void This_machines_gpu_reports_a_load_or_nothing()
    {
        using var proc = new ProcFileSystem("/proc");
        var meter = new LinuxGpuMeter(proc, "/sys", TimeProvider.System);
        var pids = new List<int>();
        proc.ListProcessIds(pids);

        meter.Sample(pids);
        Thread.Sleep(500);
        double? load = meter.Sample(pids);

        TestContext.Current.SendDiagnosticMessage($"GPU source available: {meter.IsAvailable}, load: {load?.ToString("F1") ?? "null"}");
        if (load is { } value)
        {
            Assert.InRange(value, 0, 100);
        }
    }
}
