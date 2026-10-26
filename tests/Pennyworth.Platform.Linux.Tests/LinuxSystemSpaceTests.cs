using Pennyworth.Platform.Linux;
using Pennyworth.Scanning;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public class LinuxSystemSpaceTests
{
    /// <summary>Captured on the development machine (openSUSE, btrfs root with subvolume mounts, ext4 /home, xfs data disk).</summary>
    private const string MountInfo = """
        43 1 0:39 /@/.snapshots/499/snapshot / rw,relatime shared:1 - btrfs /dev/nvme0n1p2 rw,seclabel,ssd,discard=async,space_cache=v2,subvolid=762,subvol=/@/.snapshots/499/snapshot
        54 43 259:1 / /home rw,relatime shared:47 - ext4 /dev/nvme0n1p1 rw,seclabel,data=ordered
        55 43 0:38 /@/.snapshots /.snapshots rw,relatime shared:45 - btrfs /dev/nvme0n1p2 rw,seclabel,ssd,discard=async,space_cache=v2,subvolid=262,subvol=/@/.snapshots
        64 43 0:49 / /tmp rw,nosuid,nodev shared:55 - tmpfs tmpfs rw,seclabel,size=16340776k,nr_inodes=1048576,inode64,usrquota
        67 43 0:51 /@/var /var rw,relatime shared:59 - btrfs /dev/nvme0n1p2 rw,seclabel,ssd,discard=async,space_cache=v2,subvolid=257,subvol=/@/var
        70 43 259:3 / /boot/efi rw,relatime shared:61 - vfat /dev/nvme0n1p3 rw,fmask=0022,dmask=0077,codepage=437,iocharset=iso8859-1,shortname=mixed,utf8,errors=remount-ro
        72 43 0:52 / /mnt/exram rw,relatime shared:63 - btrfs /dev/sda1 rw,seclabel,ssd,discard=async,space_cache=v2,subvolid=5,subvol=/
        97 43 8:16 / /mnt/wd-blue rw,relatime shared:65 - xfs /dev/sdb rw,seclabel,inode64,logbufs=8,logbsize=32k,noquota
        """;

    /// <summary><c>btrfs filesystem usage -b /</c> as an ordinary user (the warning goes to stderr).</summary>
    private const string BtrfsUsage = """
        Overall:
            Device size:		      272831086592
            Device allocated:		      164357996544
            Device unallocated:		      108473090048
            Device missing:		                 0
            Device slack:		                 0
            Used:			      156731396096
            Free (estimated):		      114137149440	(min: 59900604416)
            Free (statfs, df):		      114136100864
            Data ratio:			              1.00
            Metadata ratio:		              2.00
            Global reserve:		         186417152	(used: 0)
            Multiple profiles:		                no

        Data,single: Size:153553469440, Used:147889410048 (96.31%)

        Metadata,DUP: Size:5368709120, Used:4420976640 (82.35%)

        System,DUP: Size:33554432, Used:16384 (0.05%)

        """;

    private const string SnapperRootConfig = """

        # subvolume to snapshot
        SUBVOLUME="/"

        # filesystem type
        FSTYPE="btrfs"


        # btrfs qgroup for space aware cleanup algorithms
        QGROUP="1/0"


        # fraction or absolute size of the filesystems space the snapshots may use
        SPACE_LIMIT="0.5"
        ALLOW_USERS=""
        """;

    private const string BtrfsUuid = "3e6fd1ce-1ce0-4469-ab00-7555dd5a2ac0";

    /// <summary>Metadata and system chunk bytes on disk in <see cref="BtrfsUsage"/>: (4420976640 + 16384) copies of two.</summary>
    private const long CapturedMetadataBytes = (4420976640L + 16384L) * 2;

    /// <summary><c>statvfs("/")</c> on the btrfs root: free minus available is the unused part of the metadata chunks.</summary>
    private static readonly VfsCounters RootCounters = new(4096, 4096, 66609152, 28299139, 27865259);

    private static readonly long RootReserved = (28299139L - 27865259L) * 4096;

    /// <summary><c>statvfs("/home")</c> on the ext4 partition (5 % reserved for root).</summary>
    private static readonly VfsCounters HomeCounters = new(4096, 4096, 174021227, 50791857, 41933685);

    private static readonly long HomeReserved = (50791857L - 41933685L) * 4096;

    private static readonly long HomeOverhead = (1416652175L * 512) - (174021227L * 4096);

    // ---- mountinfo ----

    [Fact]
    public void Btrfs_subvolume_mounts_differ_in_device_number_but_share_their_source()
    {
        IReadOnlyList<MountEntry> mounts = MountTable.Parse(MountInfo);

        MountEntry root = Assert.Single(mounts, m => m.MountPoint == "/");
        MountEntry snapshots = Assert.Single(mounts, m => m.MountPoint == "/.snapshots");
        Assert.NotEqual(root.MajorMinor, snapshots.MajorMinor);
        Assert.Equal("/dev/nvme0n1p2", root.Source);
        Assert.Equal(root.Source, snapshots.Source);
        Assert.Equal("btrfs", root.FileSystemType);
        MountEntry home = Assert.Single(mounts, m => m.MountPoint == "/home");
        Assert.Equal(("259:1", "ext4", "/dev/nvme0n1p1"), (home.MajorMinor, home.FileSystemType, home.Source));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/etc/hosts", "/")]
    [InlineData("/home", "/home")]
    [InlineData("/home/user/file", "/home")]
    [InlineData("/homework", "/")]
    [InlineData("/.snapshots/1/snapshot", "/.snapshots")]
    [InlineData("/mnt/wd-blue", "/mnt/wd-blue")]
    [InlineData("/mnt/wd-blue-segundo", "/")]
    public void The_longest_mount_point_that_contains_the_path_wins(string path, string expectedMountPoint)
    {
        MountEntry? mount = SystemSpaceParsers.FindMount(MountTable.Parse(MountInfo), path);

        Assert.Equal(expectedMountPoint, mount?.MountPoint);
    }

    [Fact]
    public void The_latest_of_two_stacked_mounts_is_the_visible_one()
    {
        var mounts = new List<MountEntry>
        {
            new("/data", "ext4", "/dev/sda1", "8:1", "/", "rw"),
            new("/data", "xfs", "/dev/sda2", "8:2", "/", "rw"),
        };

        Assert.Equal("xfs", SystemSpaceParsers.FindMount(mounts, "/data/x")?.FileSystemType);
    }

    // ---- btrfs filesystem usage ----

    [Fact]
    public void Btrfs_metadata_is_used_bytes_of_metadata_and_system_chunks_times_their_copies()
    {
        Assert.Equal(CapturedMetadataBytes, SystemSpaceParsers.ParseBtrfsMetadataBytes(BtrfsUsage));
        Assert.Equal(8_841_986_048L, CapturedMetadataBytes);
    }

    [Fact]
    public void Data_chunks_never_count_as_metadata()
    {
        const string output = "Data,single: Size:1000, Used:900 (90.00%)\nMetadata,single: Size:100, Used:7 (7.00%)\n";

        Assert.Equal(7, SystemSpaceParsers.ParseBtrfsMetadataBytes(output));
    }

    [Theory]
    [InlineData("single", 10)]
    [InlineData("RAID0", 10)]
    [InlineData("DUP", 20)]
    [InlineData("RAID1", 20)]
    [InlineData("RAID10", 20)]
    [InlineData("RAID1C3", 30)]
    [InlineData("RAID1C4", 40)]
    public void The_profile_decides_how_many_copies_of_metadata_are_stored(string profile, long expected)
    {
        string output = $"Metadata,{profile}: Size:100, Used:10 (10.00%)\n";

        Assert.Equal(expected, SystemSpaceParsers.ParseBtrfsMetadataBytes(output));
    }

    [Fact]
    public void An_unknown_profile_uses_the_ratio_btrfs_states()
    {
        const string output = "    Metadata ratio:\t\t     1.50\n\nMetadata,RAID5: Size:100, Used:10 (10.00%)\n";

        Assert.Equal(15, SystemSpaceParsers.ParseBtrfsMetadataBytes(output));
    }

    [Fact]
    public void Mixed_profiles_are_added_each_with_its_own_copies()
    {
        const string output = "Metadata,single: Size:100, Used:10\nMetadata,DUP: Size:100, Used:10\nSystem,DUP: Size:8, Used:1\n";

        Assert.Equal(10 + 20 + 2, SystemSpaceParsers.ParseBtrfsMetadataBytes(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage\nnot btrfs output")]
    [InlineData("Data,single: Size:1000, Used:900 (90.00%)\n")]
    [InlineData("Metadata,DUP: Size:100, Used:notanumber (1.00%)\n")]
    [InlineData("Metadata,DUP: Size:100, Used:99999999999999999999999 (1.00%)\n")]
    [InlineData("Metadata,DUP: Size:100, Used:9223372036854775807 (1.00%)\n")]
    public void Output_without_a_usable_metadata_line_measures_nothing(string output)
    {
        Assert.Null(SystemSpaceParsers.ParseBtrfsMetadataBytes(output));
    }

    [Fact]
    public void The_per_device_section_printed_to_root_is_not_mistaken_for_chunk_lines()
    {
        const string output = "Metadata,DUP: Size:100, Used:10 (10.00%)\n\n/dev/sda1, ID: 1\n   Device size: 500\n   Metadata,DUP: 200\n   System,DUP: 64\n";

        Assert.Equal(20, SystemSpaceParsers.ParseBtrfsMetadataBytes(output));
    }

    // ---- statvfs arithmetic ----

    [Fact]
    public void Reserved_bytes_are_free_minus_available_blocks_in_fragment_units()
    {
        Assert.Equal(HomeReserved, SystemSpaceParsers.ReservedBytes(HomeCounters));
        Assert.Equal(1_777_172_480L, RootReserved);
        Assert.Equal(RootReserved, SystemSpaceParsers.ReservedBytes(RootCounters));
    }

    [Fact]
    public void Available_above_free_is_an_impossible_reserve_of_zero()
    {
        var counters = new VfsCounters(4096, 4096, 1000, 10, 50);

        Assert.Equal(0, SystemSpaceParsers.ReservedBytes(counters));
    }

    [Fact]
    public void Equal_free_and_available_blocks_reserve_nothing()
    {
        Assert.Equal(0, SystemSpaceParsers.ReservedBytes(new VfsCounters(4096, 4096, 100, 49679308, 49679308)));
    }

    [Fact]
    public void The_fragment_size_is_the_unit_and_the_block_size_only_a_fallback()
    {
        Assert.Equal(10 * 1024, SystemSpaceParsers.ReservedBytes(new VfsCounters(4096, 1024, 100, 15, 5)));
        Assert.Equal(10 * 4096, SystemSpaceParsers.ReservedBytes(new VfsCounters(4096, 0, 100, 15, 5)));
        Assert.Null(SystemSpaceParsers.ReservedBytes(new VfsCounters(0, 0, 100, 15, 5)));
    }

    [Fact]
    public void A_reserve_larger_than_the_filesystem_is_not_believed()
    {
        Assert.Null(SystemSpaceParsers.ReservedBytes(new VfsCounters(4096, 4096, 100, 5000, 0)));
    }

    [Fact]
    public void A_byte_count_beyond_the_range_of_long_is_not_measured()
    {
        var counters = new VfsCounters(1 << 30, 1 << 30, ulong.MaxValue, ulong.MaxValue, 0);

        Assert.Null(SystemSpaceParsers.ReservedBytes(counters));
    }

    [Fact]
    public void Device_overhead_is_what_the_device_holds_beyond_the_reported_filesystem_size()
    {
        Assert.Equal(HomeOverhead, SystemSpaceParsers.DeviceOverheadBytes("1416652175\n", HomeCounters));
        Assert.Equal(12_534_967_808L, HomeOverhead);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("99999999999999999999")]
    [InlineData("9223372036854775807")]
    public void An_unreadable_device_size_measures_no_overhead(string sectors)
    {
        Assert.Null(SystemSpaceParsers.DeviceOverheadBytes(sectors, HomeCounters));
    }

    [Fact]
    public void A_filesystem_reported_larger_than_its_device_is_not_believed()
    {
        Assert.Null(SystemSpaceParsers.DeviceOverheadBytes("1000", HomeCounters));
        Assert.Null(SystemSpaceParsers.DeviceOverheadBytes(null, HomeCounters));
    }

    [Fact]
    public void A_filesystem_that_fills_its_device_has_no_overhead_to_report()
    {
        var counters = new VfsCounters(512, 512, 100, 0, 0);

        Assert.Equal(0, SystemSpaceParsers.DeviceOverheadBytes("100", counters));
    }

    // ---- snapper config and qgroups ----

    [Fact]
    public void Snapper_config_yields_subvolume_and_qgroup()
    {
        Assert.Equal(new SnapperConfig("/", "1/0"), SystemSpaceParsers.ParseSnapperConfig(SnapperRootConfig));
    }

    [Fact]
    public void Snapper_config_accepts_single_quotes_and_bare_values_and_ignores_comments()
    {
        const string config = "#QGROUP=\"9/9\"\n  SUBVOLUME='/home'\nQGROUP=2/5 # trailing\n";

        Assert.Equal(new SnapperConfig("/home", "2/5"), SystemSpaceParsers.ParseSnapperConfig(config));
    }

    [Fact]
    public void Snapper_config_without_quotas_has_an_empty_qgroup()
    {
        Assert.Equal(new SnapperConfig("/", ""), SystemSpaceParsers.ParseSnapperConfig("SUBVOLUME=\"/\"\nQGROUP=\"\"\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a config")]
    [InlineData("SUBVOLUME=\"\"\nQGROUP=\"1/0\"")]
    [InlineData("# SUBVOLUME=\"/\"")]
    public void A_file_without_a_subvolume_is_not_a_snapper_config(string text)
    {
        Assert.Null(SystemSpaceParsers.ParseSnapperConfig(text));
    }

    [Theory]
    [InlineData("1/0", "1_0")]
    [InlineData("0/257", "0_257")]
    [InlineData("255/265", "255_265")]
    [InlineData("", null)]
    [InlineData("1", null)]
    [InlineData("1/", null)]
    [InlineData("../..", null)]
    [InlineData("1/0/../..", null)]
    [InlineData("1/0\n", null)]
    [InlineData("a/b", null)]
    public void A_qgroup_becomes_a_sysfs_folder_name_only_when_it_is_level_slash_id(string qgroup, string? expected)
    {
        Assert.Equal(expected, SystemSpaceParsers.QgroupDirectory(qgroup));
    }

    [Theory]
    [InlineData("13985783808\n", 13985783808L)]
    [InlineData("0", 0L)]
    [InlineData("", null)]
    [InlineData("-1", null)]
    [InlineData("many", null)]
    [InlineData(null, null)]
    public void A_sysfs_counter_is_a_non_negative_integer(string? text, long? expected)
    {
        Assert.Equal(expected, SystemSpaceParsers.ParseCounter(text));
    }

    // ---- the whole measurement over a fabricated machine ----

    [Fact]
    public async Task Btrfs_root_reports_snapshot_qgroup_and_metadata_with_reserved_blocks()
    {
        var service = new LinuxSystemSpaceService(Machine().Sources);

        UnlockResult result = await service.MeasureAsync("/", CancellationToken.None);

        Assert.Equal(UnlockOutcome.Measured, result.Outcome);
        Assert.Equal(new HiddenSpaceReport(13_985_783_808L, CapturedMetadataBytes + RootReserved), result.Report);
    }

    [Fact]
    public async Task Another_subvolume_of_the_same_btrfs_filesystem_reports_the_same_snapshots()
    {
        var service = new LinuxSystemSpaceService(Machine().Sources);

        UnlockResult result = await service.MeasureAsync("/var/lib/", CancellationToken.None);

        Assert.Equal(13_985_783_808L, result.Report?.RestorePoints);
    }

    [Fact]
    public async Task Ext4_reports_reserved_blocks_and_journal_and_inode_tables_but_no_snapshots()
    {
        var service = new LinuxSystemSpaceService(Machine().Sources);

        UnlockResult result = await service.MeasureAsync("/home", CancellationToken.None);

        Assert.Equal(UnlockOutcome.Measured, result.Outcome);
        Assert.Equal(new HiddenSpaceReport(null, HomeReserved + HomeOverhead), result.Report);
    }

    [Fact]
    public async Task A_btrfs_filesystem_without_a_snapper_config_has_no_restore_points_but_keeps_metadata()
    {
        FakeMachine machine = Machine();
        machine.Directories[Path.Combine("/etc/snapper", "configs")] = [];
        var service = new LinuxSystemSpaceService(machine.Sources);

        UnlockResult result = await service.MeasureAsync("/", CancellationToken.None);

        Assert.Equal(new HiddenSpaceReport(null, CapturedMetadataBytes + RootReserved), result.Report);
    }

    [Fact]
    public async Task A_snapper_config_of_another_filesystem_does_not_count()
    {
        FakeMachine machine = Machine();
        machine.Files[Path.Combine("/etc/snapper/configs", "root")] = SnapperRootConfig.Replace("\"/\"", "\"/mnt/exram\"");
        var service = new LinuxSystemSpaceService(machine.Sources);

        UnlockResult result = await service.MeasureAsync("/", CancellationToken.None);

        Assert.Null(result.Report?.RestorePoints);
    }

    [Fact]
    public async Task Two_configs_sharing_a_qgroup_count_it_once_and_different_qgroups_add_up()
    {
        FakeMachine machine = Machine();
        machine.Directories["/etc/snapper/configs"] = ["root", "home", "var"];
        machine.Files[Path.Combine("/etc/snapper/configs", "home")] = SnapperRootConfig.Replace("\"/\"", "\"/var\"");
        machine.Files[Path.Combine("/etc/snapper/configs", "var")] = SnapperRootConfig.Replace("\"/\"", "\"/var\"").Replace("1/0", "1/1");
        machine.Files[Path.Combine("/sys/fs/btrfs", BtrfsUuid, "qgroups", "1_1", "exclusive")] = "1000\n";
        var service = new LinuxSystemSpaceService(machine.Sources);

        UnlockResult result = await service.MeasureAsync("/", CancellationToken.None);

        Assert.Equal(13_985_783_808L + 1000, result.Report?.RestorePoints);
    }

    [Fact]
    public async Task Quota_numbers_the_kernel_marks_inconsistent_are_not_reported()
    {
        FakeMachine machine = Machine();
        machine.Files[Path.Combine("/sys/fs/btrfs", BtrfsUuid, "qgroups", "inconsistent")] = "1\n";
        var service = new LinuxSystemSpaceService(machine.Sources);

        UnlockResult result = await service.MeasureAsync("/", CancellationToken.None);

        Assert.Equal(UnlockOutcome.Measured, result.Outcome);
        Assert.Null(result.Report?.RestorePoints);
    }

    [Fact]
    public async Task A_qgroup_without_a_readable_exclusive_counter_is_unmeasured_not_zero()
    {
        FakeMachine machine = Machine();
        machine.Files.Remove(Path.Combine("/sys/fs/btrfs", BtrfsUuid, "qgroups", "1_0", "exclusive"));
        var service = new LinuxSystemSpaceService(machine.Sources);

        UnlockResult result = await service.MeasureAsync("/", CancellationToken.None);

        Assert.Null(result.Report?.RestorePoints);
    }

    [Fact]
    public async Task A_missing_btrfs_command_leaves_the_reserved_blocks_and_the_snapshots()
    {
        FakeMachine machine = Machine();
        machine.BtrfsUsage = null;
        var service = new LinuxSystemSpaceService(machine.Sources);

        UnlockResult result = await service.MeasureAsync("/", CancellationToken.None);

        Assert.Equal(new HiddenSpaceReport(13_985_783_808L, RootReserved), result.Report);
    }

    [Fact]
    public async Task Garbage_from_the_btrfs_command_is_a_missing_source()
    {
        FakeMachine machine = Machine();
        machine.BtrfsUsage = "ERROR: something unexpected\n";
        var service = new LinuxSystemSpaceService(machine.Sources);

        UnlockResult result = await service.MeasureAsync("/", CancellationToken.None);

        Assert.Equal(new HiddenSpaceReport(13_985_783_808L, RootReserved), result.Report);
    }

    [Fact]
    public async Task Without_mountinfo_the_reserved_blocks_are_all_that_is_known()
    {
        FakeMachine machine = Machine();
        machine.Files.Remove("/proc/self/mountinfo");
        var service = new LinuxSystemSpaceService(machine.Sources);

        UnlockResult result = await service.MeasureAsync("/home", CancellationToken.None);

        Assert.Equal(new HiddenSpaceReport(null, HomeReserved), result.Report);
    }

    [Fact]
    public async Task A_filesystem_without_a_block_device_reports_only_its_reserve()
    {
        FakeMachine machine = Machine();
        machine.Counters["/tmp"] = new VfsCounters(4096, 4096, 4085194, 4085000, 4085000);
        var service = new LinuxSystemSpaceService(machine.Sources);

        UnlockResult result = await service.MeasureAsync("/tmp", CancellationToken.None);

        Assert.Equal(UnlockOutcome.Measured, result.Outcome);
        Assert.Equal(new HiddenSpaceReport(null, 0), result.Report);
    }

    [Fact]
    public async Task Nothing_measurable_is_a_failure_with_no_report()
    {
        var machine = new FakeMachine();
        var service = new LinuxSystemSpaceService(machine.Sources);

        UnlockResult result = await service.MeasureAsync("/", CancellationToken.None);

        Assert.Equal(new UnlockResult(UnlockOutcome.Failed), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("C:")]
    public async Task A_drive_root_that_is_not_an_absolute_path_fails_without_touching_the_machine(string driveRoot)
    {
        FakeMachine machine = Machine();
        var service = new LinuxSystemSpaceService(machine.Sources);

        UnlockResult result = await service.MeasureAsync(driveRoot, CancellationToken.None);

        Assert.Equal(new UnlockResult(UnlockOutcome.Failed), result);
        Assert.Empty(machine.Reads);
    }

    [Fact]
    public async Task Declined_is_never_an_outcome()
    {
        var service = new LinuxSystemSpaceService(Machine().Sources);

        foreach (string root in new[] { "/", "/home", "/tmp", "/nonexistent", "" })
        {
            Assert.NotEqual(UnlockOutcome.Declined, (await service.MeasureAsync(root, CancellationToken.None)).Outcome);
        }
    }

    [Fact]
    public async Task A_token_cancelled_before_the_start_cancels_the_task()
    {
        var machine = Machine();
        var service = new LinuxSystemSpaceService(machine.Sources);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.MeasureAsync("/", cancellation.Token));

        Assert.Empty(machine.Reads);
    }

    [Fact]
    public async Task Cancelling_while_a_source_is_being_read_stops_before_the_next_one()
    {
        FakeMachine machine = Machine();
        using var cancellation = new CancellationTokenSource();
        machine.OnRead = path =>
        {
            if (path == "/proc/self/mountinfo")
            {
                cancellation.Cancel();
            }
        };
        var service = new LinuxSystemSpaceService(machine.Sources);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.MeasureAsync("/", cancellation.Token));

        Assert.False(machine.BtrfsUsageRan);
        Assert.DoesNotContain(machine.Reads, path => path.Contains("qgroups"));
    }

    // ---- this machine ----

    [LinuxFact]
    public async Task The_real_root_is_measured_or_honestly_failed_without_a_prompt_and_never_declined()
    {
        var service = new LinuxSystemSpaceService();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        UnlockResult result = await service.MeasureAsync("/", CancellationToken.None);

        TestContext.Current.SendDiagnosticMessage($"MeasureAsync(\"/\") = {result.Outcome} {result.Report} in {clock.ElapsedMilliseconds} ms");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "the measurement must not hang on a helper program");
        Assert.NotEqual(UnlockOutcome.Declined, result.Outcome);
        Assert.Equal(result.Outcome == UnlockOutcome.Measured, result.Report is not null);
        Assert.True(result.Report is null or { RestorePoints: null or >= 0 });
        Assert.True(result.Report is null or { NtfsMetadata: null or >= 0 });
    }

    [LinuxFact]
    public async Task A_path_that_does_not_exist_is_a_failure()
    {
        var service = new LinuxSystemSpaceService();

        UnlockResult result = await service.MeasureAsync("/nonexistent-pennyworth-mount", CancellationToken.None);

        Assert.Equal(new UnlockResult(UnlockOutcome.Failed), result);
    }

    [LinuxFact]
    public void Statvfs_of_the_real_root_reports_a_filesystem()
    {
        VfsCounters? counters = SystemSpaceStatVfs.Read("/");

        Assert.NotNull(counters);
        Assert.True(counters.Value.Blocks > 0);
        Assert.True(counters.Value.FragmentSize > 0 || counters.Value.BlockSize > 0);
        Assert.True(counters.Value.FreeBlocks <= counters.Value.Blocks);
        Assert.Null(SystemSpaceStatVfs.Read("/nonexistent-pennyworth-mount"));
    }

    [LinuxFact]
    public void The_real_mountinfo_parses_and_holds_the_root()
    {
        IReadOnlyList<MountEntry> mounts = MountTable.Parse(File.ReadAllText("/proc/self/mountinfo"));

        Assert.NotNull(SystemSpaceParsers.FindMount(mounts, "/"));
    }

    private static FakeMachine Machine()
    {
        var machine = new FakeMachine { BtrfsUsage = BtrfsUsage };
        machine.Files["/proc/self/mountinfo"] = MountInfo;
        machine.Counters["/"] = RootCounters;
        machine.Counters["/var/lib"] = RootCounters;
        machine.Counters["/home"] = HomeCounters;
        machine.Files[Path.Combine("/sys/dev/block", "259:1", "size")] = "1416652175\n";
        machine.Directories["/etc/snapper/configs"] = ["root"];
        machine.Files[Path.Combine("/etc/snapper/configs", "root")] = SnapperRootConfig;
        machine.Directories["/sys/fs/btrfs"] = ["features", "a0d16af5-c1cc-46c8-94c3-782d075a023e", BtrfsUuid];
        machine.Directories[Path.Combine("/sys/fs/btrfs", "a0d16af5-c1cc-46c8-94c3-782d075a023e", "devices")] = ["sda1"];
        machine.Directories[Path.Combine("/sys/fs/btrfs", BtrfsUuid, "devices")] = ["nvme0n1p2"];
        string qgroups = Path.Combine("/sys/fs/btrfs", BtrfsUuid, "qgroups");
        machine.Files[Path.Combine(qgroups, "inconsistent")] = "0\n";
        machine.Files[Path.Combine(qgroups, "1_0", "exclusive")] = "13985783808\n";
        machine.Files[Path.Combine(qgroups, "0_762", "exclusive")] = "148131840\n";
        return machine;
    }

    /// <summary>Answers from dictionaries and records what was asked; anything not set up is unreadable, as on a locked-down machine.</summary>
    private sealed class FakeMachine
    {
        public Dictionary<string, string> Files { get; } = [];

        public Dictionary<string, string[]> Directories { get; } = [];

        public Dictionary<string, VfsCounters> Counters { get; } = [];

        public string? BtrfsUsage { get; set; }

        public bool BtrfsUsageRan { get; private set; }

        public List<string> Reads { get; } = [];

        public Action<string>? OnRead { get; set; }

        public SystemSpaceSources Sources => new(
            path =>
            {
                Reads.Add(path);
                OnRead?.Invoke(path);
                return Files.GetValueOrDefault(path);
            },
            path => Directories.GetValueOrDefault(path) ?? [],
            source => Path.GetFileName(source),
            path =>
            {
                Reads.Add(path);
                return Counters.TryGetValue(path, out VfsCounters value) ? value : null;
            },
            _ =>
            {
                BtrfsUsageRan = true;
                return BtrfsUsage;
            });
    }
}
