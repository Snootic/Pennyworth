using Pennyworth.Platform.Linux;

namespace Pennyworth.Platform.Linux.Tests;

public class BtrfsSpaceTests
{
    /// <summary><c>btrfs subvolume list -s /</c> of a snapper layout: three snapshots.</summary>
    private const string SnapshotList = """
        ID 258 gen 120 cgen 120 parent 257 top level 257 otime 2025-02-01 09:00:00 path @/.snapshots/1/snapshot
        ID 259 gen 140 cgen 140 parent 257 top level 257 otime 2025-02-02 09:00:00 path @/.snapshots/2/snapshot
        ID 260 gen 160 cgen 160 parent 257 top level 257 otime 2025-02-03 09:00:00 path @/.snapshots/3/snapshot
        """;

    /// <summary><c>btrfs qgroup show -pcre --raw /</c>: snapper's <c>1/0</c> holds the three snapshot qgroups.</summary>
    private const string QgroupsWithParent = """
        Qgroupid    Referenced    Exclusive   Max referenced   Max exclusive   Parent  Child  Path
        --------    ----------    ---------   --------------   -------------   ------  -----  ----
        0/5              16384        16384             none            none      ---    ---  <toplevel>
        0/256        200000000     80000000             none            none      ---    ---  @
        0/257            16384        16384             none            none      ---    ---  @/.snapshots
        0/258        150000000     20000000             none            none      1/0    ---  @/.snapshots/1/snapshot
        0/259        160000000     15000000             none            none      1/0    ---  @/.snapshots/2/snapshot
        0/260        170000000     10000000             none            none      1/0    ---  @/.snapshots/3/snapshot
        1/0          300000000     60000000             none            none      ---    0/258,0/259,0/260  <3 member qgroups>
        """;

    /// <summary>The same table without snapper's parent group: only the snapshots' own qgroups.</summary>
    private const string QgroupsWithoutParent = """
        Qgroupid    Referenced    Exclusive   Max referenced   Max exclusive   Parent  Child  Path
        --------    ----------    ---------   --------------   -------------   ------  -----  ----
        0/5              16384        16384             none            none      ---    ---  <toplevel>
        0/256        200000000     80000000             none            none      ---    ---  @
        0/258        150000000     20000000             none            none      ---    ---  @/.snapshots/1/snapshot
        0/259        160000000     15000000             none            none      ---    ---  @/.snapshots/2/snapshot
        0/260        170000000     10000000             none            none      ---    ---  @/.snapshots/3/snapshot
        """;

    /// <summary><c>btrfs filesystem usage -b /</c>.</summary>
    private const string Usage = """
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

    /// <summary>Metadata and system chunks of <see cref="Usage"/>, both written twice (DUP).</summary>
    private const long UsageMetadataBytes = 2 * 4420976640L + 2 * 16384L;

    [Fact]
    public void TheSnapperParentGroupStandsForItsSnapshots()
    {
        Assert.Equal(60_000_000, BtrfsSpace.SnapshotBytes(SnapshotList, QgroupsWithParent));
    }

    [Fact]
    public void WithoutAParentGroupTheSnapshotsOwnExclusiveSizesAreAdded()
    {
        Assert.Equal(45_000_000, BtrfsSpace.SnapshotBytes(SnapshotList, QgroupsWithoutParent));
    }

    [Fact]
    public void ASnapshotOutsideTheParentCountsWithItsOwnSize()
    {
        string list = SnapshotList + "\nID 261 gen 170 cgen 170 parent 257 top level 257 otime 2025-02-04 09:00:00 path @/.snapshots/4/snapshot";
        string qgroups = QgroupsWithParent + "\n0/261        170000000      5000000             none            none      ---    ---  @/.snapshots/4/snapshot";

        Assert.Equal(65_000_000, BtrfsSpace.SnapshotBytes(list, qgroups));
    }

    [Fact]
    public void AGroupWithAMemberThatIsNotASnapshotIsNotAParent()
    {
        string qgroups = QgroupsWithParent.Replace("0/258,0/259,0/260", "0/256,0/259,0/260", StringComparison.Ordinal);

        Assert.Equal(45_000_000, BtrfsSpace.SnapshotBytes(SnapshotList, qgroups));
    }

    [Fact]
    public void TheGroupHoldingTheMostSnapshotsIsTheParent()
    {
        string qgroups = QgroupsWithParent + "\n1/1          170000000      9000000             none            none      ---    0/260  <1 member qgroups>";

        Assert.Equal(60_000_000, BtrfsSpace.SnapshotBytes(SnapshotList, qgroups));
    }

    [Fact]
    public void ALevelZeroQgroupIsNeverAParent()
    {
        string qgroups = QgroupsWithoutParent.Replace("0/259        160000000     15000000             none            none      ---    ---", "0/259        160000000     15000000             none            none      ---    0/258", StringComparison.Ordinal);

        Assert.Equal(45_000_000, BtrfsSpace.SnapshotBytes(SnapshotList, qgroups));
    }

    [Fact]
    public void NoSnapshotsOnAFileSystemWithQuotasIsZero()
    {
        Assert.Equal(0, BtrfsSpace.SnapshotBytes("", QgroupsWithoutParent));
        Assert.Equal(0, BtrfsSpace.SnapshotBytes("\n", QgroupsWithoutParent));
    }

    [Theory]
    [InlineData("ERROR: can't list qgroups: quotas not enabled\n")]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("Qgroupid    Referenced    Exclusive   Max referenced   Max exclusive   Parent  Child  Path\n--------    ----------    ---------   --------------   -------------   ------  -----  ----\n")]
    [InlineData("0/5 not numbers\n")]
    public void QuotasThatAreOffLeaveTheSnapshotBytesNull(string qgroupOutput)
    {
        Assert.Null(BtrfsSpace.SnapshotBytes(SnapshotList, qgroupOutput));
        Assert.Null(BtrfsSpace.SnapshotBytes("", qgroupOutput));
    }

    [Fact]
    public void ACommandThatDidNotRunLeavesTheSnapshotBytesNull()
    {
        Assert.Null(BtrfsSpace.SnapshotBytes(null, QgroupsWithParent));
        Assert.Null(BtrfsSpace.SnapshotBytes(SnapshotList, null));
        Assert.Null(BtrfsSpace.SnapshotBytes(null, null));
    }

    [Fact]
    public void ASnapshotWithoutAQgroupRowIsInconsistent()
    {
        string qgroups = QgroupsWithParent.Replace("0/260        170000000     10000000             none            none      1/0    ---  @/.snapshots/3/snapshot\n", "", StringComparison.Ordinal);

        Assert.NotEqual(QgroupsWithParent, qgroups);
        Assert.Null(BtrfsSpace.SnapshotBytes(SnapshotList, qgroups));
    }

    [Fact]
    public void AnExclusiveSizeAboveTheReferencedSizeIsInconsistent()
    {
        string qgroups = QgroupsWithParent.Replace("150000000     20000000", "10000000     20000000", StringComparison.Ordinal);

        Assert.Null(BtrfsSpace.SnapshotBytes(SnapshotList, qgroups));
    }

    [Fact]
    public void AParentSmallerThanItsMembersIsInconsistent()
    {
        string qgroups = QgroupsWithParent.Replace("300000000     60000000", "300000000     40000000", StringComparison.Ordinal);

        Assert.Null(BtrfsSpace.SnapshotBytes(SnapshotList, qgroups));
    }

    [Fact]
    public void ADuplicateQgroupRowIsInconsistent()
    {
        string qgroups = QgroupsWithParent + "\n0/259        160000000     15000000             none            none      1/0    ---  @/.snapshots/2/snapshot";

        Assert.Null(BtrfsSpace.SnapshotBytes(SnapshotList, qgroups));
    }

    [Fact]
    public void ALineThatIsNeitherHeaderNorRowIsInconsistent()
    {
        string qgroups = QgroupsWithParent + "\nWARNING: rescan is running, qgroup data may be inconsistent";

        Assert.Null(BtrfsSpace.SnapshotBytes(SnapshotList, qgroups));
    }

    [Fact]
    public void ASizeBeyondTheRangeOfALongIsInconsistentNotWrapped()
    {
        string qgroups = QgroupsWithoutParent.Replace("170000000     10000000", "99999999999999999999     99999999999999999999", StringComparison.Ordinal);

        Assert.Null(BtrfsSpace.SnapshotBytes(SnapshotList, qgroups));
    }

    [Fact]
    public void SnapshotSizesThatOverflowTheSumAreNull()
    {
        string qgroups = QgroupsWithoutParent
            .Replace("150000000     20000000", "9223372036854775807     9223372036854775807", StringComparison.Ordinal)
            .Replace("160000000     15000000", "9223372036854775807     9223372036854775807", StringComparison.Ordinal);

        Assert.Null(BtrfsSpace.SnapshotBytes(SnapshotList, qgroups));
    }

    [Fact]
    public void ASubvolumeListWithUnrecognisedContentIsNull()
    {
        Assert.Null(BtrfsSpace.SnapshotBytes("ERROR: can't perform the search\n", QgroupsWithParent));
    }

    [Fact]
    public void TheListMayCarryTrailingWhitespaceAndWindowsLineEnds()
    {
        Assert.Equal(60_000_000, BtrfsSpace.SnapshotBytes(SnapshotList.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n\r\n", QgroupsWithParent.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void BuildAddsTheReserveToTheMetadataChunks()
    {
        SystemSpaceResponse response = BtrfsSpace.Build("btrfs", 1_000, Usage, SnapshotList, QgroupsWithParent);

        Assert.Equal(new SystemSpaceResponse(HelperProtocol.Version, "btrfs", 60_000_000, UsageMetadataBytes + 1_000), response);
    }

    [Fact]
    public void BuildKeepsWhicheverMetadataPartIsThere()
    {
        Assert.Equal(1_000, BtrfsSpace.Build("btrfs", 1_000, null, null, null).MetadataBytes);
        Assert.Equal(UsageMetadataBytes, BtrfsSpace.Build("btrfs", null, Usage, null, null).MetadataBytes);
        Assert.Equal(UsageMetadataBytes, BtrfsSpace.Build("btrfs", null, Usage + "\nnoise", null, null).MetadataBytes);
        Assert.Null(BtrfsSpace.Build("btrfs", null, null, null, null).MetadataBytes);
        Assert.Equal(1_000, BtrfsSpace.Build("btrfs", 1_000, "ERROR: not a btrfs\n", null, null).MetadataBytes);
    }

    [Fact]
    public void BuildReportsTheSnapshotPartIndependentlyOfTheMetadataPart()
    {
        SystemSpaceResponse response = BtrfsSpace.Build("btrfs", null, null, SnapshotList, QgroupsWithoutParent);

        Assert.Equal(45_000_000, response.SnapshotBytes);
        Assert.Null(response.MetadataBytes);
    }

    [Fact]
    public void AddSaturatingStopsAtTheLargestValue()
    {
        Assert.Equal(long.MaxValue, BtrfsSpace.AddSaturating(long.MaxValue, 1));
        Assert.Equal(7, BtrfsSpace.AddSaturating(7, null));
        Assert.Equal(7, BtrfsSpace.AddSaturating(null, 7));
        Assert.Null(BtrfsSpace.AddSaturating(null, null));
    }

    private const string MountInfo = """
        45 1 0:41 / / rw,relatime shared:1 - btrfs /dev/nvme0n1p2 rw,subvolid=5,subvol=/
        54 45 259:1 / /home rw,relatime shared:47 - ext4 /dev/nvme0n1p1 rw
        """;

    private sealed class FakeMachine
    {
        public List<(string Tool, string[] Arguments)> Commands { get; } = [];

        public Dictionary<string, string> Files { get; } = new() { ["/proc/self/mountinfo"] = MountInfo };

        public Dictionary<string, string?> Output { get; } = new()
        {
            ["subvolume"] = SnapshotList,
            ["qgroup"] = QgroupsWithParent,
            ["filesystem"] = Usage,
        };

        public VfsCounters? Counters { get; set; } = new(4096, 4096, 1000, 100, 60);

        public SystemSpaceInputs Inputs => new(
            path => Files.GetValueOrDefault(path),
            _ => Counters,
            (tool, arguments, _) =>
            {
                Commands.Add((tool, [.. arguments]));
                return Output.GetValueOrDefault(arguments[0]);
            });
    }

    [Fact]
    public void BtrfsRunsTheThreeFixedCommandsWithTheMountPointLast()
    {
        var machine = new FakeMachine();

        SystemSpaceResponse response = SystemSpace.Run("/", machine.Inputs, CancellationToken.None);

        Assert.Equal(
            [
                "btrfs subvolume list -s /",
                "btrfs qgroup show -pcre --raw /",
                "btrfs filesystem usage -b /",
            ],
            machine.Commands.Select(c => c.Tool + " " + string.Join(' ', c.Arguments)));
        Assert.Equal("btrfs", response.FileSystem);
        Assert.Equal(60_000_000, response.SnapshotBytes);
        Assert.Equal(UsageMetadataBytes + 40 * 4096, response.MetadataBytes);
    }

    [Fact]
    public void BtrfsWithoutQuotasStillReportsMetadata()
    {
        var machine = new FakeMachine();
        machine.Output["qgroup"] = null;

        SystemSpaceResponse response = SystemSpace.Run("/", machine.Inputs, CancellationToken.None);

        Assert.Null(response.SnapshotBytes);
        Assert.Equal(UsageMetadataBytes + 40 * 4096, response.MetadataBytes);
    }

    [Fact]
    public void BtrfsWithoutAnyToolReportsOnlyTheReserve()
    {
        var machine = new FakeMachine();
        machine.Output.Clear();

        SystemSpaceResponse response = SystemSpace.Run("/", machine.Inputs, CancellationToken.None);

        Assert.Null(response.SnapshotBytes);
        Assert.Equal(40 * 4096, response.MetadataBytes);
    }

    [Fact]
    public void AnotherFileSystemRunsNoToolAndAddsTheDeviceOverheadToTheReserve()
    {
        var machine = new FakeMachine();
        machine.Files["/sys/dev/block/259:1/size"] = ((1000 * 4096 + 262_144) / 512).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";

        SystemSpaceResponse response = SystemSpace.Run("/home", machine.Inputs, CancellationToken.None);

        Assert.Empty(machine.Commands);
        Assert.Equal(new SystemSpaceResponse(HelperProtocol.Version, "ext4", null, 40 * 4096 + 262_144), response);
    }

    [Fact]
    public void AnotherFileSystemWithoutADeviceSizeReportsTheReserve()
    {
        var machine = new FakeMachine();

        Assert.Equal(40 * 4096, SystemSpace.Run("/home", machine.Inputs, CancellationToken.None).MetadataBytes);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("")]
    [InlineData("/home/sub")]
    [InlineData("/nowhere")]
    public void ANonMountPointIsRefusedWithoutRunningAnything(string mountPoint)
    {
        var machine = new FakeMachine();

        HelperException error = Assert.Throws<HelperException>(() => SystemSpace.Run(mountPoint, machine.Inputs, CancellationToken.None));

        Assert.Equal(HelperExit.Refused, error.ExitCode);
        Assert.Empty(machine.Commands);
    }

    [Fact]
    public void AnUnreadableFileSystemSizeIsAFailure()
    {
        var machine = new FakeMachine { Counters = null };

        HelperException error = Assert.Throws<HelperException>(() => SystemSpace.Run("/", machine.Inputs, CancellationToken.None));

        Assert.Equal(HelperExit.Failed, error.ExitCode);
    }

    [Fact]
    public void CancellationStopsBeforeTheNextCommand()
    {
        var machine = new FakeMachine();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => SystemSpace.Run("/", machine.Inputs, cts.Token));
        Assert.Empty(machine.Commands);
    }
}
