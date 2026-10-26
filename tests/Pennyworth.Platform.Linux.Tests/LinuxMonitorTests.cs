using System.Diagnostics;
using System.Globalization;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>The monitor against a pretend <c>/proc</c> and <c>/sys</c>, so every number is known.</summary>
public class LinuxMonitorTests
{
    private static readonly long PageSize = Environment.SystemPageSize;

    private static readonly ProcessExplanation DBus = new(
        "dbus", ["dbus-broker"], new("D-Bus", "D-Bus"), new("", ""), null, IsProtected: true, CanClose: false, ListServices: true);

    private static LinuxMonitor Start(FakeProcFs system, StepClock clock, params ProcessExplanation[] explanations) =>
        new(explanations, system.Proc, system.Sys, clock);

    private static FakeProcFs Machine()
    {
        var system = new FakeProcFs();
        system.WriteCpu(user: 1000, system: 1000, idle: 8000);
        system.WriteMemInfo(totalKb: 8_000_000, availableKb: 3_000_000);
        return system;
    }

    [Fact]
    public void Machine_load_is_the_busy_share_since_the_previous_sample()
    {
        using FakeProcFs system = Machine();
        var clock = new StepClock();
        using LinuxMonitor monitor = Start(system, clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        system.WriteCpu(user: 1200, system: 1000, idle: 8800); // +200 user, +800 idle
        double load = monitor.Sample().CpuPercent;

        Assert.Equal(20, load, 3);
    }

    [Fact]
    public void A_process_using_one_of_four_cores_is_25_percent()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(100, "busy", user: 10, system: 5);
        var clock = new StepClock();
        using LinuxMonitor monitor = Start(system, clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        system.WriteProcess(100, "busy", user: 70, system: 45); // 100 jiffies = one CPU-second
        AppGroup busy = Assert.Single(monitor.Sample().Apps);

        Assert.Equal(25, busy.Cpu, 3);
        Assert.Equal(25, busy.Processes[0].Cpu, 3);
    }

    [Fact]
    public void A_reused_process_id_starts_over_instead_of_inheriting_the_old_cpu_time()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(100, "old", user: 10, start: 1000);
        var clock = new StepClock();
        using LinuxMonitor monitor = Start(system, clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        system.WriteProcess(100, "new", user: 5000, start: 99_000);
        AppGroup group = Assert.Single(monitor.Sample().Apps);

        Assert.Equal("new", group.Key);
        Assert.Equal(0, group.Cpu);
    }

    [Fact]
    public void Private_memory_is_the_rollup_where_readable_and_resident_memory_elsewhere()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(1, "mine", residentPages: 100_000, privateKilobytes: 5_000);
        system.WriteProcess(2, "theirs", residentPages: 300);
        using LinuxMonitor monitor = Start(system, new StepClock());

        var memory = monitor.Sample().Apps.ToDictionary(g => g.Key, g => g.Memory);

        Assert.Equal(5_000L * 1024, memory["mine"]); // not the 100 000 resident pages
        Assert.Equal(300 * PageSize, memory["theirs"]);
    }

    private static long MemoryOf(LinuxMonitor monitor, string key) => monitor.Sample().Apps.Single(a => a.Key == key).Memory;

    [Fact]
    public void Private_memory_is_read_again_when_the_reading_is_sixty_samples_old()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(1, "app", residentPages: 100_000, privateKilobytes: 5_000);
        using LinuxMonitor monitor = Start(system, new StepClock());
        system.WritePrivate(1, 9_000);

        for (int sample = 1; sample <= 59; sample++)
        {
            Assert.Equal(5_000L * 1024, MemoryOf(monitor, "app")); // the cheap estimate, not a new walk of the page tables
        }

        Assert.Equal(9_000L * 1024, MemoryOf(monitor, "app"));
    }

    [Fact]
    public void Between_readings_private_memory_follows_the_growth_of_resident_memory()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(1, "app", residentPages: 100_000, privateKilobytes: 5_000);
        using LinuxMonitor monitor = Start(system, new StepClock());

        system.WriteProcess(1, "app", residentPages: 101_000); // 4 MB more resident, under the 64 MiB that forces a new reading
        Assert.Equal((5_000L * 1024) + (1_000 * PageSize), MemoryOf(monitor, "app"));

        system.WriteProcess(1, "app", residentPages: 99_000); // and shrinks with it
        Assert.Equal((5_000L * 1024) - (1_000 * PageSize), MemoryOf(monitor, "app"));
    }

    [Fact]
    public void A_large_change_in_resident_memory_triggers_a_new_reading_at_once()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(1, "app", residentPages: 100_000, privateKilobytes: 5_000);
        using LinuxMonitor monitor = Start(system, new StepClock());

        system.WriteProcess(1, "app", residentPages: 120_000, privateKilobytes: 20_000); // +78 MiB

        Assert.Equal(20_000L * 1024, MemoryOf(monitor, "app"));
    }

    [Fact]
    public void The_time_budget_limits_the_page_table_walks_and_the_biggest_new_process_goes_first()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(1, "small", residentPages: 100, privateKilobytes: 50);
        system.WriteProcess(2, "large", residentPages: 300, privateKilobytes: 150);
        system.WriteProcess(3, "medium", residentPages: 200, privateKilobytes: 100);

        // A zero budget still reads one process per sample, so everything is reached.
        using var monitor = new LinuxMonitor([], system.Proc, system.Sys, new StepClock(), TimeSpan.Zero);

        var first = monitor.Sample().Apps.ToDictionary(a => a.Key, a => a.Memory); // the constructor already read "large"
        Assert.Equal(150L * 1024, first["large"]);
        Assert.Equal(100L * 1024, first["medium"]);
        Assert.Equal(100 * PageSize, first["small"]);

        var second = monitor.Sample().Apps.ToDictionary(a => a.Key, a => a.Memory);
        Assert.Equal(50L * 1024, second["small"]);
    }

    [Fact]
    public void A_process_that_refuses_the_rollup_is_not_asked_again_for_its_lifetime()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(1, "theirs", residentPages: 300);
        using LinuxMonitor monitor = Start(system, new StepClock());
        Assert.Equal(300 * PageSize, MemoryOf(monitor, "theirs"));

        system.WritePrivate(1, 5); // it would be readable now

        for (int sample = 0; sample < 70; sample++)
        {
            Assert.Equal(300 * PageSize, MemoryOf(monitor, "theirs"));
        }
    }

    [Fact]
    public void A_reused_process_id_does_not_inherit_the_old_private_memory()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(1, "old", residentPages: 100_000, start: 1000, privateKilobytes: 5_000);
        using LinuxMonitor monitor = Start(system, new StepClock());

        system.WriteProcess(1, "new", residentPages: 100_500, start: 2000);
        system.WritePrivate(1, null);

        Assert.Equal(100_500 * PageSize, MemoryOf(monitor, "new")); // not 5 000 kB plus the growth
    }

    [Fact]
    public void Apps_are_grouped_by_name_largest_first_and_kernel_threads_are_left_out()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(10, "chrome", residentPages: 1_000, privateKilobytes: 300);
        system.WriteProcess(11, "chrome", residentPages: 1_000, privateKilobytes: 200);
        system.WriteProcess(20, "code", residentPages: 1_000, privateKilobytes: 900);
        system.WriteProcess(2, "kworker/0:1", flags: 0x208040 | 0x200000, residentPages: 1_000, privateKilobytes: 99_999);
        using LinuxMonitor monitor = Start(system, new StepClock());

        var apps = monitor.Sample().Apps;

        Assert.Equal(["code", "chrome"], apps.Select(a => a.Key));
        Assert.Equal(500L * 1024, apps[1].Memory);
        Assert.Equal([10, 11], apps[1].Processes.Select(p => p.Pid).Order());
    }

    [Fact]
    public void An_id_that_was_a_kernel_thread_is_looked_at_again_within_sixteen_samples()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(2, "kworker", flags: 0x200000, residentPages: 1_000, privateKilobytes: 1);
        using LinuxMonitor monitor = Start(system, new StepClock());
        Assert.Empty(monitor.Sample().Apps);

        system.WriteProcess(2, "app", start: 9_999, residentPages: 1_000, privateKilobytes: 1);

        int samples = 0;
        while (monitor.Sample().Apps.Count == 0 && ++samples < 40)
        {
        }

        Assert.InRange(samples, 0, 15);
    }

    [Fact]
    public void A_process_that_ends_leaves_the_list()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(10, "stays", privateKilobytes: 1);
        system.WriteProcess(11, "leaves", privateKilobytes: 1);
        using LinuxMonitor monitor = Start(system, new StepClock());
        Assert.Equal(2, monitor.Sample().Apps.Count);

        system.Remove(11);

        Assert.Equal("stays", Assert.Single(monitor.Sample().Apps).Key);
    }

    [Fact]
    public void A_process_that_renames_itself_is_listed_under_its_new_name()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(10, "launcher", privateKilobytes: 1);
        using LinuxMonitor monitor = Start(system, new StepClock());

        system.WriteProcess(10, "app", privateKilobytes: 1);

        Assert.Equal("app", Assert.Single(monitor.Sample().Apps).Key);
    }

    [Fact]
    public void An_explained_service_host_lists_the_services_from_the_cgroup_and_cannot_be_closed()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(50, "dbus-broker", privateKilobytes: 10, cgroup: "/system.slice/dbus-broker.service");
        system.WriteProcess(51, "dbus-broker", privateKilobytes: 10, cgroup: "/user.slice/user-1000.slice/user@1000.service/app.slice/app-x.scope");
        using LinuxMonitor monitor = Start(system, new StepClock(), DBus);

        AppGroup group = Assert.Single(monitor.Sample().Apps);

        Assert.Equal("dbus", group.Explanation?.Id);
        Assert.False(group.CanClose);
        Assert.Equal(["dbus-broker.service"], group.Processes.Single(p => p.Pid == 50).Services);
        Assert.Empty(group.Processes.Single(p => p.Pid == 51).Services);
    }

    [LinuxFact]
    public void The_executable_path_comes_from_the_exe_link_without_the_deleted_marker()
    {
        using FakeProcFs system = Machine();
        system.WriteProcess(60, "tool", privateKilobytes: 1);
        File.CreateSymbolicLink(Path.Combine(system.Proc, "60", "exe"), "/usr/bin/tool (deleted)");
        system.WriteProcess(61, "unreadable", privateKilobytes: 1); // no exe link: readlink fails, as it does for another user's process
        using LinuxMonitor monitor = Start(system, new StepClock());

        var apps = monitor.Sample().Apps.ToDictionary(a => a.Key);

        Assert.Equal("/usr/bin/tool", apps["tool"].ImagePath);
        Assert.Null(apps["unreadable"].ImagePath);
    }

    [Fact]
    public void Cached_memory_is_capped_so_that_free_memory_is_what_the_kernel_reports_free()
    {
        using FakeProcFs system = Machine();
        // Buffers + Cached + SReclaimable = 5 GB, more than the 3 GB available: shared memory is "cached" but not reclaimable.
        system.WriteMemInfo(8_000_000, 3_000_000, buffersKb: 1_000_000, cachedKb: 3_500_000, reclaimableKb: 500_000, freeKb: 400_000);
        using LinuxMonitor monitor = Start(system, new StepClock());

        MemoryStatus memory = monitor.Sample().Memory;

        Assert.Equal(8_000_000L * 1024, memory.Total);
        Assert.Equal(3_000_000L * 1024, memory.Available);
        Assert.Equal(2_600_000L * 1024, memory.Cached);
        Assert.Equal(400_000L * 1024, memory.Free);
        Assert.Equal(5_000_000L * 1024, memory.Used);
    }

    [Fact]
    public void Cached_memory_is_the_sum_of_buffers_cache_and_reclaimable_slab()
    {
        using FakeProcFs system = Machine();
        system.WriteMemInfo(8_000_000, 6_000_000, buffersKb: 100_000, cachedKb: 2_000_000, reclaimableKb: 400_000);
        using LinuxMonitor monitor = Start(system, new StepClock());

        MemoryStatus memory = monitor.Sample().Memory;

        Assert.Equal(2_500_000L * 1024, memory.Cached);
        Assert.Equal(3_500_000L * 1024, memory.Free);
    }

    [Fact]
    public void Compressed_memory_is_zswap_plus_every_zram_device()
    {
        using FakeProcFs system = Machine();
        system.WriteMemInfo(8_000_000, 3_000_000, zswapKb: 100);
        system.WriteSys(Path.Combine("block", "zram0", "mm_stat"), "9215328256 2710122928 2765144064 0 2765144064 1 2 3 4\n");
        system.WriteSys(Path.Combine("block", "zram1", "mm_stat"), "10 10 4096 0 4096 0 0 0 0\n");
        system.WriteSys(Path.Combine("block", "sda", "mm_stat"), "1 1 999999 0 0 0 0 0 0\n"); // not a zram device
        using LinuxMonitor monitor = Start(system, new StepClock());

        Assert.Equal((100L * 1024) + 2765144064 + 4096, monitor.Sample().Memory.Compressed);
    }

    [Fact]
    public void No_compression_is_zero_compressed()
    {
        using FakeProcFs system = Machine();
        using LinuxMonitor monitor = Start(system, new StepClock());

        Assert.Equal(0, monitor.Sample().Memory.Compressed);
    }

    private static void Limit(FakeProcFs system, string cgroup, string folder, string? max = null, string? high = null, string? current = null, string? inactiveFile = null)
    {
        system.WriteProc(Path.Combine("self", "cgroup"), "0::" + cgroup + "\n");
        string path = Path.Combine("fs", "cgroup", folder);
        if (max is not null)
        {
            system.WriteSys(Path.Combine(path, "memory.max"), max + "\n");
        }

        if (high is not null)
        {
            system.WriteSys(Path.Combine(path, "memory.high"), high + "\n");
        }

        if (current is not null)
        {
            system.WriteSys(Path.Combine(path, "memory.current"), current + "\n");
        }

        if (inactiveFile is not null)
        {
            system.WriteSys(Path.Combine(path, "memory.stat"), $"anon 1\ninactive_file {inactiveFile}\n");
        }
    }

    [Fact]
    public void A_cgroup_limit_replaces_the_machines_memory_and_leaves_what_the_cgroup_has_not_used()
    {
        using FakeProcFs system = Machine();
        Limit(system, "/app.slice/app.service", Path.Combine("app.slice", "app.service"), max: "1073741824", current: "500000000", inactiveFile: "100000000");
        using LinuxMonitor monitor = Start(system, new StepClock());

        MemoryStatus memory = monitor.Sample().Memory;

        Assert.Equal(1073741824, memory.Total);
        Assert.Equal(1073741824 - 400_000_000, memory.Available); // used = current - inactive file cache
    }

    [Fact]
    public void The_smaller_of_max_high_and_every_ancestors_limit_applies()
    {
        using FakeProcFs system = Machine();
        Limit(system, "/a/b", Path.Combine("a", "b"), max: "max", high: "2000000000");
        system.WriteSys(Path.Combine("fs", "cgroup", "a", "memory.max"), "1000000000\n");
        system.WriteSys(Path.Combine("fs", "cgroup", "memory.max"), "500000000\n"); // the root of a container's hierarchy
        using LinuxMonitor monitor = Start(system, new StepClock());

        Assert.Equal(500_000_000, monitor.Sample().Memory.Total);
    }

    [Fact]
    public void A_limit_larger_than_the_machine_or_no_limit_changes_nothing()
    {
        using FakeProcFs system = Machine();
        Limit(system, "/a", "a", max: "max", high: "max");
        using LinuxMonitor monitor = Start(system, new StepClock());
        Assert.Equal(8_000_000L * 1024, monitor.Sample().Memory.Total);

        Limit(system, "/a", "a", max: "999999999999999");
        using LinuxMonitor other = Start(system, new StepClock());
        Assert.Equal(8_000_000L * 1024, other.Sample().Memory.Total);
    }

    [Fact]
    public void A_machine_without_proc_files_degrades_every_field_and_does_not_throw()
    {
        using FakeProcFs system = new();
        var clock = new StepClock();
        using LinuxMonitor monitor = Start(system, clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        MonitorSample sample = monitor.Sample();

        Assert.Equal(default, sample.Memory);
        Assert.Equal(0, sample.CpuPercent);
        Assert.Null(sample.GpuPercent);
    }

    [LinuxFact]
    public void When_proc_shows_no_process_the_list_comes_from_the_runtime_without_private_memory()
    {
        using FakeProcFs system = Machine(); // /proc exists but lists no process: what hidepid does
        using LinuxMonitor monitor = Start(system, new StepClock());

        var processes = monitor.Sample().Apps.SelectMany(a => a.Processes).ToList();

        Assert.Contains(processes, p => p.Pid == Environment.ProcessId);
        Assert.All(processes, p => Assert.Equal(0, p.Memory));
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        using FakeProcFs system = Machine();
        LinuxMonitor monitor = Start(system, new StepClock());

        monitor.Dispose();
        monitor.Dispose();
    }

    // ---- The real machine ----------------------------------------------------------------------

    [LinuxFact]
    public void Memory_matches_the_kernels_own_numbers()
    {
        using var monitor = new LinuxMonitor([]);

        MemoryStatus memory = monitor.Sample().Memory;

        Assert.True(ProcFileSystem.TryParseMemInfo(File.ReadAllText("/proc/meminfo"), out MemInfo kernel));
        Assert.InRange(memory.Total, 1, kernel.Total); // a cgroup limit can only lower it
        Assert.True(memory.Available <= memory.Total);
        if (memory.Total == kernel.Total)
        {
            Assert.InRange(memory.Available, kernel.Available - (512L << 20), kernel.Available + (512L << 20));
        }

        Assert.True(memory.Cached <= memory.Available);
        Assert.Equal(memory.Total - memory.Available, memory.Used);
    }

    [LinuxFact]
    public void This_process_is_listed_with_its_real_name_and_sizes()
    {
        using var monitor = new LinuxMonitor([]);

        MonitorSample sample = monitor.Sample();

        AppProcess me = sample.Apps.SelectMany(a => a.Processes).Single(p => p.Pid == Environment.ProcessId);
        Assert.Equal(File.ReadAllText("/proc/self/comm").Trim(), me.Name);
        Assert.InRange(me.Memory, 4L << 20, kernelResident() + (16L << 20)); // private memory never exceeds what is resident
        Assert.Contains(sample.Apps, a => a.Processes.Any(p => p.Pid == Environment.ProcessId));

        static long kernelResident()
        {
            return ProcFileSystem.ParseResidentPages(File.ReadAllText("/proc/self/statm"))!.Value * Environment.SystemPageSize;
        }
    }

    [LinuxFact]
    public void The_resident_size_in_statm_is_the_VmRSS_of_status()
    {
        string status = File.ReadAllText("/proc/self/status");
        long pages = ProcFileSystem.ParseResidentPages(File.ReadAllText("/proc/self/statm"))!.Value;

        string line = status.Split('\n').Single(l => l.StartsWith("VmRSS:", StringComparison.Ordinal));
        long vmRss = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;

        Assert.InRange(pages * Environment.SystemPageSize, vmRss - (2L << 20), vmRss + (2L << 20));
    }

    [LinuxFact]
    public void A_busy_process_is_measured_against_the_machine_and_against_top()
    {
        int cores = Environment.ProcessorCount;
        int burners = Math.Max(1, Math.Min(4, cores / 2));
        using var stop = new CancellationTokenSource();
        Task[] work = [.. Enumerable.Range(0, burners).Select(_ => Task.Factory.StartNew(
            () => { long x = 0; while (!stop.IsCancellationRequested) { x++; } return x; }, TaskCreationOptions.LongRunning))];
        try
        {
            using var monitor = new LinuxMonitor([]);
            // top -bn2 -d2 prints twice, two seconds apart; the second line is the load over that window.
            Task<string?> top = Task.Run(() => ProcessProbe.Run("top", ["-bn2", "-d2"], TimeSpan.FromSeconds(30)));
            Thread.Sleep(TimeSpan.FromSeconds(2.05));

            MonitorSample sample = monitor.Sample();

            AppProcess me = sample.Apps.SelectMany(a => a.Processes).Single(p => p.Pid == Environment.ProcessId);
            double expected = 100.0 * burners / cores;
            Assert.InRange(me.Cpu, expected - 3, expected + 4);
            Assert.True(sample.CpuPercent >= me.Cpu - 1, $"machine {sample.CpuPercent:F1}% < this process {me.Cpu:F1}%");

            double? topLoad = TopBusyPercent(top.Result);
            if (topLoad is { } reference)
            {
                TestContext.Current.SendDiagnosticMessage($"machine {sample.CpuPercent:F1}% vs top {reference:F1}%, this process {me.Cpu:F1}% (expected {expected:F1}%)");
                Assert.InRange(sample.CpuPercent, reference - 5, reference + 5);
            }
        }
        finally
        {
            stop.Cancel();
            Task.WaitAll(work);
        }
    }

    /// <summary>Last "%Cpu(s):" line of <c>top -b</c>: everything that is not idle or I/O wait (the monitor counts I/O wait as idle).</summary>
    private static double? TopBusyPercent(string? output)
    {
        string? line = output?.Split('\n').LastOrDefault(l => l.StartsWith("%Cpu", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        double busy = 0;
        foreach (string part in line[(line.IndexOf(':') + 1)..].Split(','))
        {
            string[] pieces = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length == 2 && pieces[1] is "us" or "sy" or "ni" or "hi" or "si" or "st"
                && double.TryParse(pieces[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                busy += value;
            }
        }

        return busy;
    }

    [LinuxFact]
    public void A_sample_of_this_machine_is_cheap()
    {
        using var monitor = new LinuxMonitor([]);
        monitor.Sample();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        const int Samples = 10;

        for (int i = 0; i < Samples; i++)
        {
            monitor.Sample();
        }

        double millisecondsEach = clock.Elapsed.TotalMilliseconds / Samples;
        long bytesEach = (GC.GetAllocatedBytesForCurrentThread() - allocated) / Samples;
        TestContext.Current.SendDiagnosticMessage($"{millisecondsEach:F1} ms and {bytesEach / 1024} KiB per sample");
        Assert.True(millisecondsEach < 250, $"{millisecondsEach:F0} ms per sample");
        Assert.True(bytesEach < 16L << 20, $"{bytesEach >> 10} KiB per sample");
    }
}
