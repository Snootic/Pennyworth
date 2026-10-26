using Pennyworth.Platform.Linux;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>Parsers over text captured from a real machine (openSUSE Tumbleweed, kernel 7.x).</summary>
public class ProcFileSystemTests
{
    private const string MemInfo = """
        MemTotal:       32681548 kB
        MemFree:         2032360 kB
        MemAvailable:   19331584 kB
        Buffers:         2218660 kB
        Cached:         16834864 kB
        SwapCached:         1316 kB
        Active(anon):    3462336 kB
        Zswap:               512 kB
        Zswapped:           2048 kB
        Shmem:            962712 kB
        SReclaimable:    1286292 kB
        HugePages_Total:       7
        Hugepagesize:       2048 kB
        """;

    [Fact]
    public void Meminfo_sizes_are_kibibytes_and_counts_stay_counts()
    {
        Assert.True(ProcFileSystem.TryParseMemInfo(MemInfo, out MemInfo info));

        Assert.Equal(32681548L * 1024, info.Total);
        Assert.Equal(19331584L * 1024, info.Available);
        Assert.Equal(2032360L * 1024, info.Free);
        Assert.Equal(2218660L * 1024, info.Buffers);
        Assert.Equal(16834864L * 1024, info.Cached);
        Assert.Equal(1286292L * 1024, info.Reclaimable); // SReclaimable, not SwapCached or Shmem
        Assert.Equal(512L * 1024, info.Zswap); // Zswap, not Zswapped
    }

    [Fact]
    public void Meminfo_without_MemAvailable_derives_it_and_never_exceeds_the_total()
    {
        const string old = "MemTotal: 1000 kB\nMemFree: 100 kB\nBuffers: 50 kB\nCached: 200 kB\n";

        Assert.True(ProcFileSystem.TryParseMemInfo(old, out MemInfo info));
        Assert.Equal(350L * 1024, info.Available);

        Assert.True(ProcFileSystem.TryParseMemInfo("MemTotal: 10 kB\nMemFree: 9 kB\nCached: 9 kB\n", out info));
        Assert.Equal(10L * 1024, info.Available);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MemFree: 100 kB\n")]
    [InlineData("MemTotal: nonsense kB\n")]
    [InlineData("MemTotal: 0 kB\n")]
    public void Meminfo_without_a_total_is_unreadable(string text)
    {
        Assert.False(ProcFileSystem.TryParseMemInfo(text, out _));
    }

    private const string CpuStat = """
        cpu  18742395 22944 3634684 402401944 64379 0 13852 5 0 0
        cpu0 505351 729 143126 14343730 3237 0 850 0 0 0
        cpu1 746008 1020 138993 14256417 2075 0 409 0 0 0
        cpu2 768522 591 121386 14275827 1749 0 170 0 0 0
        intr 123456 0 0 12
        ctxt 5443213
        cpufreq_dummy 1 2 3
        """;

    [Fact]
    public void Cpu_time_is_laid_out_for_CpuTracker()
    {
        Assert.True(ProcFileSystem.TryParseCpuStat(CpuStat, out CpuTimes times));

        Assert.Equal(402401944 + 64379, times.Idle); // iowait is idle: a core waiting on a disk is not busy
        Assert.Equal(18742395 + 22944, times.User); // user + nice
        Assert.Equal(3634684 + 0 + 13852 + 5 + times.Idle, times.Kernel); // system+irq+softirq+steal, and idle inside it
        Assert.Equal(3, times.Cores); // cpu0..cpu2; "cpu " and what follows intr are not cores
    }

    [Fact]
    public void Cpu_time_of_an_old_kernel_with_four_columns()
    {
        Assert.True(ProcFileSystem.TryParseCpuStat("cpu  100 10 50 800\ncpu0 100 10 50 800\n", out CpuTimes times));

        Assert.Equal(800, times.Idle);
        Assert.Equal(110, times.User);
        Assert.Equal(850, times.Kernel);
        Assert.Equal(1, times.Cores);
    }

    [Fact]
    public void Cpu_load_of_two_snapshots_matches_the_busy_share()
    {
        ProcFileSystem.TryParseCpuStat("cpu  100 0 100 800 0 0 0 0\n", out CpuTimes first);
        ProcFileSystem.TryParseCpuStat("cpu  200 0 200 1000 100 0 0 0\n", out CpuTimes second);
        var tracker = new Pennyworth.Monitoring.CpuTracker();
        tracker.UpdateSystem(first.Idle, first.Kernel, first.User);

        // 500 jiffies passed; idle plus iowait grew by 300, so 200 were busy.
        Assert.Equal(40, tracker.UpdateSystem(second.Idle, second.Kernel, second.User), 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("intr 1 2 3\n")]
    [InlineData("cpu0 1 2 3 4\n")]
    public void Stat_without_the_aggregate_line_is_unreadable(string text)
    {
        Assert.False(ProcFileSystem.TryParseCpuStat(text, out _));
    }

    // A real process whose executable is called "a) (b c".
    private const string TrickyStat =
        "1373412 (a) (b c) S 1048699 1373412 1373412 0 -1 4194304 73 0 0 0 7 5 0 0 20 0 1 0 15094258 2953216 426 18446744073709551615 94630208176128 94630208194161 140726993837616 0 0 0 0 16777216 0 1 0 0 17 3 0 0 0 0 0 94630208207760 94630208209048 94630828589056 140726993843609 140726993843625 140726993843625 140726993850347 0\n";

    [Fact]
    public void A_name_with_parentheses_and_spaces_is_split_at_the_last_closing_parenthesis()
    {
        Assert.True(ProcFileSystem.TryParseProcessStat(TrickyStat, out ProcessStat stat));

        Assert.Equal("a) (b c", stat.Comm.ToString());
        Assert.Equal(1048699, stat.ParentPid);
        Assert.Equal(4194304, stat.Flags);
        Assert.Equal(7, stat.UserTicks);
        Assert.Equal(5, stat.SystemTicks);
        Assert.Equal(15094258, stat.StartTime);
    }

    [Theory]
    [InlineData("Web Content")]
    [InlineData("tmux: server")]
    [InlineData("x)")]
    [InlineData("(x")]
    [InlineData("tab(1048188")]
    [InlineData("")]
    public void The_fields_after_the_name_do_not_shift_with_the_name(string comm)
    {
        string line = $"42 ({comm}) S 1 42 42 0 -1 0 0 0 0 0 1500 250 0 0 20 0 1 0 9000 1000000 321 18446744073709551615 0\n";

        Assert.True(ProcFileSystem.TryParseProcessStat(line, out ProcessStat stat));

        Assert.Equal(comm, stat.Comm.ToString());
        Assert.Equal(1500, stat.UserTicks);
        Assert.Equal(250, stat.SystemTicks);
        Assert.Equal(9000, stat.StartTime);
    }

    [Fact]
    public void Kernel_threads_are_recognised_by_their_flag()
    {
        // kthreadd: flags 0x208040 has PF_KTHREAD (0x200000).
        Assert.True(ProcFileSystem.TryParseProcessStat("2 (kthreadd) S 0 0 0 0 -1 2129984 0 0 0 0 0 4 0 0 20 0 1 0 5 0 0 18446744073709551615 0\n", out ProcessStat kernel));
        Assert.True((kernel.Flags & ProcFileSystem.KernelThreadFlag) != 0);

        Assert.True(ProcFileSystem.TryParseProcessStat(TrickyStat, out ProcessStat user));
        Assert.True((user.Flags & ProcFileSystem.KernelThreadFlag) == 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("42 no parentheses S 1 2 3")]
    [InlineData(") (")]
    [InlineData("42 (cut) S 1 42 42 0 -1 0 0 0 0 0 1500")]
    [InlineData("42 (bad) S 1 42 42 0 -1 0 0 0 0 0 x 250 0 0 20 0 1 0 9000 1000000 321\n")]
    public void A_process_file_that_is_cut_short_or_garbled_is_unreadable(string text)
    {
        Assert.False(ProcFileSystem.TryParseProcessStat(text, out _));
    }

    private const string SmapsRollup = """
        5610cff16000-7ffd8e7a9000 ---p 00000000 00:00 0                          [rollup]
        Rss:                1996 kB
        Pss:                 174 kB
        Shared_Clean:       1832 kB
        Shared_Dirty:          0 kB
        Private_Clean:        12 kB
        Private_Dirty:       164 kB
        Referenced:         1996 kB
        Shared_Hugetlb:        0 kB
        Private_Hugetlb:    2048 kB
        """;

    [Fact]
    public void Private_memory_is_clean_plus_dirty_and_ignores_hugetlb()
    {
        Assert.Equal((12L + 164) * 1024, ProcFileSystem.ParsePrivateBytes(SmapsRollup));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Rss: 1996 kB\nPss: 174 kB\n")]
    public void Private_memory_is_unknown_when_the_kernel_prints_none(string text)
    {
        Assert.Null(ProcFileSystem.ParsePrivateBytes(text));
    }

    [Fact]
    public void Private_memory_of_zero_is_known_not_unknown()
    {
        Assert.Equal(0, ProcFileSystem.ParsePrivateBytes("Private_Clean: 0 kB\nPrivate_Dirty: 0 kB\n"));
    }

    [Theory]
    [InlineData("99999 5000 10 1 0 5 0\n", 5000)]
    [InlineData("0 0 0 0 0 0 0\n", 0)]
    public void Resident_memory_is_the_second_column_of_statm(string statm, long pages)
    {
        Assert.Equal(pages, ProcFileSystem.ParseResidentPages(statm));
    }

    [Theory]
    [InlineData("")]
    [InlineData("99999\n")]
    [InlineData("99999 x 1\n")]
    [InlineData("99999 -4 1\n")]
    public void Statm_without_a_resident_size_is_unknown(string statm)
    {
        Assert.Null(ProcFileSystem.ParseResidentPages(statm));
    }

    [Fact]
    public void Zram_memory_is_the_third_column_of_mm_stat()
    {
        // orig_data_size compr_data_size mem_used_total mem_limit mem_used_max same_pages pages_compacted huge_pages
        Assert.Equal(2765144064, ProcFileSystem.ParseZramBytes("9215328256 2710122928 2765144064        0 2765144064   133387   422933   165230   282190\n"));
        Assert.Equal(0, ProcFileSystem.ParseZramBytes("9215328256 2710122928\n"));
        Assert.Equal(0, ProcFileSystem.ParseZramBytes(""));
    }

    [Theory]
    [InlineData("max\n", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("2147483648\n", true, 2147483648)]
    [InlineData("0\n", true, 0)]
    public void A_cgroup_limit_is_a_number_or_max(string text, bool isLimit, long bytes)
    {
        Assert.Equal(isLimit ? bytes : null, ProcFileSystem.ParseLimit(text));
    }

    [Fact]
    public void A_keyed_value_matches_the_whole_key()
    {
        const string stat = "anon 100\nfile 200\ninactive_file_x 5\ntotal_inactive_file 6\ninactive_file 7\n";

        Assert.True(ProcFileSystem.TryParseKeyed(stat, "inactive_file", out long value));
        Assert.Equal(7, value);
        Assert.False(ProcFileSystem.TryParseKeyed(stat, "active_file", out _));
    }

    private const string KonsoleTab = "1:net_cls:/\n0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-org.kde.konsole-4526.scope/tab(1048188).scope\n";

    [Theory]
    [InlineData("1:net_cls:/\n0::/system.slice/dbus-broker.service\n", "dbus-broker.service")]
    [InlineData("0::/user.slice/user-1000.slice/user@1000.service/session.slice/pipewire.service\n", "pipewire.service")]
    [InlineData("0::/user.slice/user-1000.slice/user@1000.service/app.slice/dbus-broker.service\n", "dbus-broker.service")]
    [InlineData("0::/system.slice/docker.service/payload/inner\n", "docker.service")] // a service that delegates its subtree
    [InlineData("0::/system.slice/system-getty.slice/getty@tty1.service\n", "getty@tty1.service")]
    [InlineData("0::/system.slice/system-systemd\\x2dfsck.slice/systemd-fsck@dev-disk-by\\x2duuid-1234.service\n", "systemd-fsck@dev-disk-by-uuid-1234.service")]
    [InlineData("12:cpu:/\n1:name=systemd:/system.slice/sshd.service\n0::/\n", "sshd.service")] // legacy hierarchy
    [InlineData("0::/system.slice/sshd.service\n1:name=systemd:/system.slice/sshd.service\n", "sshd.service")]
    public void A_process_in_a_service_cgroup_is_attributed_to_the_service(string cgroup, string expected)
    {
        Assert.Equal(expected, ProcFileSystem.ParseServiceUnit(cgroup));
    }

    [Theory]
    [InlineData(KonsoleTab)] // a desktop app is a scope under the user manager, not the manager's service
    [InlineData("0::/user.slice/user-1000.slice/session-3.scope\n")]
    [InlineData("0::/user.slice/user-1000.slice/user@1000.service/init.scope\n")] // systemd --user itself
    [InlineData("0::/user.slice/user-1000.slice/user@1000.service/app.slice\n")]
    [InlineData("0::/init.scope\n")]
    [InlineData("0::/system.slice/docker-0123abcd.scope\n")]
    [InlineData("0::/docker/0123abcd\n")]
    [InlineData("0::/\n")]
    [InlineData("1:net_cls:/\n")]
    [InlineData("")]
    [InlineData("garbage")]
    public void A_process_outside_any_service_has_none(string cgroup)
    {
        Assert.Null(ProcFileSystem.ParseServiceUnit(cgroup));
    }

    [Fact]
    public void Unit_name_escapes_are_undone()
    {
        Assert.Equal("dev-disk-by-uuid-1.device", ProcFileSystem.UnescapeUnitName("dev-disk-by\\x2duuid\\x2d1.device"));
        Assert.Equal("plain.service", ProcFileSystem.UnescapeUnitName("plain.service"));
        Assert.Equal("cut\\x2", ProcFileSystem.UnescapeUnitName("cut\\x2")); // not an escape: left alone
        Assert.Equal("a\\xzzb", ProcFileSystem.UnescapeUnitName("a\\xzzb"));
    }

    [Fact]
    public void The_unified_cgroup_path_is_the_zero_hierarchy()
    {
        Assert.Equal("/user.slice/x.scope", ProcFileSystem.ParseUnifiedCgroupPath("1:net_cls:/\n0::/user.slice/x.scope\n"));
        Assert.Null(ProcFileSystem.ParseUnifiedCgroupPath("1:name=systemd:/a\n"));
    }

    [Theory]
    [InlineData("/usr/bin/firefox (deleted)", "/usr/bin/firefox")]
    [InlineData("/usr/bin/firefox", "/usr/bin/firefox")]
    [InlineData("/opt/a (deleted) b/app", "/opt/a (deleted) b/app")]
    public void The_kernels_deleted_marker_is_not_part_of_the_path(string link, string expected)
    {
        Assert.Equal(expected, ProcFileSystem.CleanExePath(link));
    }
}
