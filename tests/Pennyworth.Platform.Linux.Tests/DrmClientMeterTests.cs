using System.Globalization;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public class DrmClientMeterTests
{
    // fdinfo of a render node opened by gnome-shell on an Intel Arc B580 (xe driver).
    private const string XeFdInfo = """
        pos:	0
        flags:	02100002
        mnt_id:	39
        ino:	623
        drm-driver:	xe
        drm-client-id:	22
        drm-pdev:	0000:05:00.0
        drm-total-system:	532 KiB
        drm-resident-vram0:	25996 KiB
        drm-cycles-rcs:	212191984
        drm-total-cycles-rcs:	2891041013683
        drm-cycles-vcs:	0
        drm-total-cycles-vcs:	2891041013683
        drm-engine-capacity-vcs:	2
        drm-cycles-bcs:	330
        drm-total-cycles-bcs:	2891041013683
        """;

    // The nanosecond flavour i915 and amdgpu print (Documentation/gpu/drm-usage-stats.rst).
    private const string NanosecondFdInfo = """
        drm-driver:	i915
        drm-pdev:	0000:00:02.0
        drm-client-id:	4
        drm-engine-render:	25662044495 ns
        drm-engine-copy:	0 ns
        drm-engine-video:	3 ns
        drm-engine-capacity-video:	2
        """;

    [Fact]
    public void Cycle_counters_of_an_xe_client_are_read_per_engine_class()
    {
        var engines = new NameTable();
        var devices = new NameTable();
        var readings = new List<DrmEngine>();

        Assert.True(DrmClientMeter.TryParseFdInfo(XeFdInfo, engines, devices, readings, out int device, out long client));

        Assert.Equal(22, client);
        Assert.Equal(devices.Id("0000:05:00.0"), device);
        DrmEngine render = Assert.Single(readings, r => r.Engine == engines.Id("rcs"));
        Assert.Equal(212191984, render.Busy);
        Assert.Equal(2891041013683, render.Total);
        Assert.Equal(1, render.Capacity);
        Assert.Equal(2, Assert.Single(readings, r => r.Engine == engines.Id("vcs")).Capacity);
        Assert.Equal(3, readings.Count);
    }

    [Fact]
    public void Nanosecond_counters_have_no_cycle_total_and_a_capacity_line_is_not_an_engine()
    {
        var engines = new NameTable();
        var readings = new List<DrmEngine>();

        Assert.True(DrmClientMeter.TryParseFdInfo(NanosecondFdInfo, engines, new NameTable(), readings, out _, out long client));

        Assert.Equal(4, client);
        Assert.Equal(3, readings.Count); // render, copy, video: "capacity-video" is not a fourth engine
        DrmEngine render = Assert.Single(readings, r => r.Engine == engines.Id("render"));
        Assert.Equal(25662044495, render.Busy);
        Assert.Equal(-1, render.Total);
        Assert.Equal(2, Assert.Single(readings, r => r.Engine == engines.Id("video")).Capacity);
    }

    [Theory]
    [InlineData("pos:\t0\nflags:\t02100002\nmnt_id:\t39\nino:\t623\n")] // a plain file
    [InlineData("drm-driver:\txe\ndrm-cycles-rcs:\t5\n")] // no client id
    [InlineData("")]
    public void An_fdinfo_without_a_client_id_is_not_a_drm_client(string text)
    {
        Assert.False(DrmClientMeter.TryParseFdInfo(text, new NameTable(), new NameTable(), [], out _, out _));
    }

    private sealed class Clients(FakeProcFs system)
    {
        public void Open(int pid, int fd, string fdinfo)
        {
            string fdFolder = Path.Combine(system.Proc, pid.ToString(CultureInfo.InvariantCulture), "fd");
            Directory.CreateDirectory(fdFolder);
            string link = Path.Combine(fdFolder, fd.ToString(CultureInfo.InvariantCulture));
            if (new FileInfo(link).LinkTarget is null)
            {
                File.CreateSymbolicLink(link, "/dev/dri/renderD128");
            }

            Update(pid, fd, fdinfo);
        }

        public void Update(int pid, int fd, string fdinfo) =>
            system.WriteProc(Path.Combine(pid.ToString(CultureInfo.InvariantCulture), "fdinfo", fd.ToString(CultureInfo.InvariantCulture)), fdinfo);
    }

    private static string Xe(int client, long cycles, long total, string engine = "rcs", int capacity = 1) =>
        $"drm-driver:\txe\ndrm-client-id:\t{client}\ndrm-pdev:\t0000:05:00.0\ndrm-cycles-{engine}:\t{cycles}\ndrm-total-cycles-{engine}:\t{total}\ndrm-engine-capacity-{engine}:\t{capacity}\n";

    private static string Ns(int client, long busy, string engine = "render", int capacity = 1) =>
        $"drm-driver:\ti915\ndrm-client-id:\t{client}\ndrm-pdev:\t0000:00:02.0\ndrm-engine-{engine}:\t{busy} ns\ndrm-engine-capacity-{engine}:\t{capacity}\n";

    [Fact]
    public void Cycles_used_over_cycles_elapsed_is_the_load()
    {
        using var system = new FakeProcFs();
        var clients = new Clients(system);
        var clock = new StepClock();
        var meter = new DrmClientMeter(new ProcFileSystem(system.Proc), clock);
        clients.Open(100, 3, Xe(22, cycles: 100, total: 1000));
        Assert.Null(meter.Sample([100])); // primes

        clock.Advance(TimeSpan.FromSeconds(1));
        clients.Update(100, 3, Xe(22, cycles: 350, total: 2000));

        Assert.Equal(25, meter.Sample([100])!.Value, 3);
    }

    [Fact]
    public void The_busiest_engine_class_is_the_load_and_capacity_divides_it()
    {
        using var system = new FakeProcFs();
        var clients = new Clients(system);
        var clock = new StepClock();
        var meter = new DrmClientMeter(new ProcFileSystem(system.Proc), clock);
        string Both(long render, long video, long total) =>
            Xe(22, render, total, "rcs") + $"drm-cycles-vcs:\t{video}\ndrm-total-cycles-vcs:\t{total}\ndrm-engine-capacity-vcs:\t2\n";
        clients.Open(100, 3, Both(0, 0, 0));
        meter.Sample([100]);

        clock.Advance(TimeSpan.FromSeconds(1));
        clients.Update(100, 3, Both(render: 250, video: 900, total: 1000));

        // render 25%; the video class has two engines, so 900 busy cycles of 2000 available are 45%.
        Assert.Equal(45, meter.Sample([100])!.Value, 3);
    }

    [Fact]
    public void Nanoseconds_are_compared_with_the_wall_clock_and_clients_add_up()
    {
        using var system = new FakeProcFs();
        var clients = new Clients(system);
        var clock = new StepClock();
        var meter = new DrmClientMeter(new ProcFileSystem(system.Proc), clock);
        clients.Open(100, 3, Ns(1, busy: 1_000_000_000));
        clients.Open(200, 5, Ns(2, busy: 50));
        meter.Sample([100, 200]);

        clock.Advance(TimeSpan.FromSeconds(2));
        clients.Update(100, 3, Ns(1, busy: 1_800_000_000)); // 0.8 s of 2 s
        clients.Update(200, 5, Ns(2, busy: 600_000_050)); // 0.6 s of 2 s

        Assert.Equal(70, meter.Sample([100, 200])!.Value, 3);
    }

    [Fact]
    public void Two_descriptors_of_one_client_count_once()
    {
        using var system = new FakeProcFs();
        var clients = new Clients(system);
        var clock = new StepClock();
        var meter = new DrmClientMeter(new ProcFileSystem(system.Proc), clock);
        clients.Open(100, 3, Ns(7, busy: 0));
        clients.Open(100, 4, Ns(7, busy: 0)); // a dup() of the same open file
        meter.Sample([100]);

        clock.Advance(TimeSpan.FromSeconds(1));
        clients.Update(100, 3, Ns(7, busy: 400_000_000));
        clients.Update(100, 4, Ns(7, busy: 400_000_000));

        Assert.Equal(40, meter.Sample([100])!.Value, 3);
    }

    [Fact]
    public void A_duplicate_descriptor_takes_over_when_the_first_one_closes()
    {
        using var system = new FakeProcFs();
        var clients = new Clients(system);
        var clock = new StepClock();
        var meter = new DrmClientMeter(new ProcFileSystem(system.Proc), clock);
        clients.Open(105, 3, Ns(7, busy: 0));
        clients.Open(105, 4, Ns(7, busy: 0));
        meter.Sample([105]);
        for (int sample = 1; sample <= 3; sample++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            clients.Update(105, 3, Ns(7, busy: sample * 300_000_000L));
            clients.Update(105, 4, Ns(7, busy: sample * 300_000_000L));
            Assert.Equal(30, meter.Sample([105])!.Value, 3);
        }

        Directory.Delete(Path.Combine(system.Proc, "105", "fd"), recursive: true); // the client goes on through fd 4 only
        Directory.CreateDirectory(Path.Combine(system.Proc, "105", "fd"));
        File.CreateSymbolicLink(Path.Combine(system.Proc, "105", "fd", "4"), "/dev/dri/renderD128");
        File.Delete(Path.Combine(system.Proc, "105", "fdinfo", "3"));

        double? last = null;
        for (int sample = 4; sample <= 20; sample++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            clients.Update(105, 4, Ns(7, busy: sample * 300_000_000L));
            last = meter.Sample([105]);
        }

        Assert.Equal(30, last!.Value, 3);
    }

    [Fact]
    public void A_client_seen_for_the_first_time_has_no_baseline_and_adds_nothing()
    {
        using var system = new FakeProcFs();
        var clients = new Clients(system);
        var clock = new StepClock();
        var meter = new DrmClientMeter(new ProcFileSystem(system.Proc), clock);
        clients.Open(101, 3, Ns(1, busy: 0));
        meter.Sample([101]);

        clock.Advance(TimeSpan.FromSeconds(1));
        clients.Update(101, 3, Ns(1, busy: 100_000_000));
        clients.Open(101, 4, Ns(2, busy: 900_000_000_000)); // an old client with a huge total, found by this sample's slice of the scan
        Assert.Equal(10, meter.Sample([101])!.Value, 3);

        clock.Advance(TimeSpan.FromSeconds(1));
        clients.Update(101, 3, Ns(1, busy: 200_000_000));
        clients.Update(101, 4, Ns(2, busy: 900_100_000_000));
        Assert.Equal(20, meter.Sample([101])!.Value, 3);
    }

    [Fact]
    public void A_counter_that_goes_backwards_and_a_standing_clock_are_ignored_not_divided_by()
    {
        using var system = new FakeProcFs();
        var clients = new Clients(system);
        var clock = new StepClock();
        var meter = new DrmClientMeter(new ProcFileSystem(system.Proc), clock);
        clients.Open(100, 3, Ns(1, busy: 5_000_000_000));
        meter.Sample([100]);

        clock.Advance(TimeSpan.FromSeconds(1));
        clients.Update(100, 3, Ns(1, busy: 10)); // the client was replaced under the same number
        Assert.Equal(0, meter.Sample([100])!.Value);

        clients.Update(100, 3, Ns(1, busy: 500_000_000));
        Assert.Null(meter.Sample([100])); // no time passed: no rate
    }

    [Fact]
    public void A_closed_descriptor_and_an_ended_process_are_forgotten_without_error()
    {
        using var system = new FakeProcFs();
        var clients = new Clients(system);
        var clock = new StepClock();
        var meter = new DrmClientMeter(new ProcFileSystem(system.Proc), clock);
        clients.Open(100, 3, Ns(1, busy: 0));
        meter.Sample([100]);

        system.Remove(100);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Null(meter.Sample([100]));
        Assert.Null(meter.Sample([]));
    }

    [Fact]
    public void A_descriptor_number_reused_for_an_ordinary_file_is_dropped()
    {
        using var system = new FakeProcFs();
        var clients = new Clients(system);
        var clock = new StepClock();
        var meter = new DrmClientMeter(new ProcFileSystem(system.Proc), clock);
        clients.Open(100, 3, Ns(1, busy: 0));
        meter.Sample([100]);

        clock.Advance(TimeSpan.FromSeconds(1));
        clients.Update(100, 3, "pos:\t0\nflags:\t02100002\n");

        Assert.Null(meter.Sample([100]));
    }

    [Fact]
    public void Processes_are_scanned_a_few_at_a_time_and_a_new_client_is_found_within_one_round()
    {
        using var system = new FakeProcFs();
        var clients = new Clients(system);
        var clock = new StepClock();
        var meter = new DrmClientMeter(new ProcFileSystem(system.Proc), clock, TimeSpan.Zero); // one process per sample
        int[] pids = [.. Enumerable.Range(100, 10)];
        meter.Sample(pids); // the first call scans everything

        clients.Open(107, 9, Ns(1, busy: 0));
        double? last = null;
        for (int sample = 1; sample <= 12; sample++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            clients.Update(107, 9, Ns(1, busy: sample * 300_000_000L));
            last = meter.Sample(pids);
            if (sample < 8)
            {
                Assert.Null(last); // sample 8 is the one that reaches the 8th process
            }
        }

        Assert.Equal(30, last!.Value, 3);
    }

    [LinuxFact]
    public void A_descriptor_that_is_not_a_drm_device_is_not_a_client()
    {
        using var system = new FakeProcFs();
        string fdFolder = Path.Combine(system.Proc, "100", "fd");
        Directory.CreateDirectory(fdFolder);
        File.CreateSymbolicLink(Path.Combine(fdFolder, "3"), "/dev/null");
        File.CreateSymbolicLink(Path.Combine(fdFolder, "4"), "/dev/dri/renderD128");
        using var proc = new ProcFileSystem(system.Proc);

        Assert.False(proc.IsDrmFile(100, 3));
        Assert.True(proc.IsDrmFile(100, 4));
        Assert.False(proc.IsDrmFile(100, 5)); // no such descriptor
    }
}
