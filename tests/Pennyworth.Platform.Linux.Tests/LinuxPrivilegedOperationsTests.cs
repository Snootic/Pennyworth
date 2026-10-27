using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Pennyworth.Cleanup;
using Pennyworth.Core.Privilege;
using Pennyworth.KnowledgeBase;
using Pennyworth.Scanning;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>
/// What the app asks pkexec for and how it reads the answer. Real pkexec is never run: a fake plays it and
/// records the command line, the standard input and the limits of every call.
/// </summary>
public class LinuxPrivilegedOperationsTests
{
    private const string Pkexec = Privileged.Pkexec;
    private const string HelperPath = HelperProtocol.InstalledPath;

    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private static ElevatedRun Exit(int code, byte[]? output = null, string error = "") => Privileged.Exit(code, output, error);

    private static LinuxPrivilegedOperations Helper(FakeRunner runner, params string[] installed) =>
        Privileged.Ops(runner, Privileged.TrustedLstat, installed.Length == 0 ? null : installed);

    private static LinuxPrivilegedOperations NoHelper(FakeRunner runner, params string[] installed) =>
        Privileged.Ops(runner, Privileged.MissingLstat, installed.Length == 0 ? null : installed);

    // ---- capability ----

    [Fact]
    public void Without_pkexec_nothing_can_be_asked()
    {
        LinuxPrivilegedOperations ops = Privileged.Ops(new FakeRunner(), Privileged.TrustedLstat, ["/usr/bin/cat"]);

        foreach (PrivilegedOperation operation in Enum.GetValues<PrivilegedOperation>())
        {
            Assert.Equal(PrivilegedCapability.Missing(PrivilegedUnavailableReason.NoPkexec), ops.Capability(operation));
        }
    }

    [Fact]
    public void The_alternative_pkexec_folder_is_found_too()
    {
        LinuxPrivilegedOperations ops = Privileged.Ops(new FakeRunner(), Privileged.TrustedLstat, ["/bin/pkexec"]);

        Assert.True(ops.Capability(PrivilegedOperation.FirmwareTables).Available);
    }

    [Fact]
    public void A_trusted_helper_makes_every_operation_available()
    {
        LinuxPrivilegedOperations ops = Helper(new FakeRunner());

        foreach (PrivilegedOperation operation in Enum.GetValues<PrivilegedOperation>())
        {
            Assert.Equal(PrivilegedCapability.Ready, ops.Capability(operation));
        }
    }

    [Theory]
    [InlineData("missing", PrivilegedUnavailableReason.HelperNotInstalled)]
    [InlineData("group-writable", PrivilegedUnavailableReason.HelperNotTrusted)]
    [InlineData("user-owned", PrivilegedUnavailableReason.HelperNotTrusted)]
    public void Without_a_trusted_helper_only_the_read_only_features_remain(string state, PrivilegedUnavailableReason reason)
    {
        LinuxPrivilegedOperations ops = Privileged.Ops(new FakeRunner(), Lstat(state));

        Assert.True(ops.Capability(PrivilegedOperation.FirmwareTables).Available);
        Assert.True(ops.Capability(PrivilegedOperation.SystemSpace).Available);
        Assert.Equal(PrivilegedCapability.Missing(reason), ops.Capability(PrivilegedOperation.Folders));
        Assert.Equal(PrivilegedCapability.Missing(reason), ops.Capability(PrivilegedOperation.SystemCleanup));
    }

    [Fact]
    public void The_helper_is_judged_again_on_every_query()
    {
        string state = "trusted";
        LinuxPrivilegedOperations ops = Privileged.Ops(new FakeRunner(), path => Lstat(state)(path));

        Assert.True(ops.Capability(PrivilegedOperation.SystemCleanup).Available);
        state = "group-writable";
        Assert.Equal(PrivilegedUnavailableReason.HelperNotTrusted, ops.Capability(PrivilegedOperation.SystemCleanup).Reason);
        state = "missing";
        Assert.Equal(PrivilegedUnavailableReason.HelperNotInstalled, ops.Capability(PrivilegedOperation.SystemCleanup).Reason);
        state = "trusted";
        Assert.True(ops.Capability(PrivilegedOperation.SystemCleanup).Available);
    }

    private static Func<string, EntryInfo> Lstat(string state) => state switch
    {
        "trusted" => Privileged.TrustedLstat,
        "missing" => Privileged.MissingLstat,
        "group-writable" => Privileged.GroupWritableLstat,
        "user-owned" => Privileged.UserOwnedLstat,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    [Fact]
    public async Task The_helper_is_checked_again_right_before_every_run()
    {
        string state = "trusted";
        var runner = new FakeRunner(FirmwareAnswer([1, 2, 3]), Exit(0, [9]));
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, path => Lstat(state)(path));

        await ops.ReadFirmwareTablesAsync(Token);
        state = "group-writable";
        await ops.ReadFirmwareTablesAsync(Token);

        Assert.Equal([$"{Pkexec} {HelperPath} read-firmware-tables", $"{Pkexec} /usr/bin/cat /sys/firmware/dmi/tables/DMI"], runner.Lines);
    }

    // ---- the exact command lines ----

    private static ElevatedRun FirmwareAnswer(byte[] table) =>
        Privileged.Json(new FirmwareTablesResponse(HelperProtocol.Version, Convert.ToBase64String(table)), HelperJson.Default.FirmwareTablesResponse);

    [Fact]
    public async Task The_firmware_tables_are_asked_of_the_helper_with_nothing_but_the_subcommand()
    {
        var runner = new FakeRunner(FirmwareAnswer([1, 2, 3, 4]));

        PrivilegedResult<byte[]> result = await Helper(runner).ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        Assert.Equal([1, 2, 3, 4], result.Value);
        RunCall call = Assert.Single(runner.Calls);
        Assert.Equal(Pkexec, call.File);
        Assert.Equal([HelperPath, "read-firmware-tables"], call.Arguments);
        Assert.Null(call.Input);
        Assert.Equal(HelperProtocol.MaxResponseBytes, call.MaxOutput);
        Assert.Equal(TimeSpan.FromSeconds(1), call.Timeout);
    }

    [Fact]
    public async Task The_firmware_tables_fall_back_to_cat_only_when_the_helper_is_not_usable()
    {
        foreach (string state in new[] { "missing", "group-writable", "user-owned" })
        {
            var runner = new FakeRunner(Exit(0, [5, 6]));

            PrivilegedResult<byte[]> result = await Privileged.Ops(runner, Lstat(state)).ReadFirmwareTablesAsync(Token);

            Assert.Equal([5, 6], result.Value);
            RunCall call = Assert.Single(runner.Calls);
            Assert.Equal(Pkexec, call.File);
            Assert.Equal(["/usr/bin/cat", "/sys/firmware/dmi/tables/DMI"], call.Arguments);
            Assert.Null(call.Input);
            Assert.Equal(ElevatedRunner.MaxOutput, call.MaxOutput);
        }
    }

    [Fact]
    public async Task Without_cat_the_fallback_table_is_empty_and_nothing_is_run()
    {
        var runner = new FakeRunner();

        PrivilegedResult<byte[]> result = await NoHelper(runner, Pkexec).ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        Assert.Empty(result.Value!);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task The_memory_text_is_dmidecode_t_17_by_absolute_path()
    {
        var runner = new FakeRunner(Privileged.Text("Handle 0x0030, DMI type 17"));

        PrivilegedResult<string> result = await Helper(runner).ReadMemoryDevicesTextAsync(Token);

        Assert.Equal("Handle 0x0030, DMI type 17", result.Value);
        Assert.Equal([$"{Pkexec} /usr/sbin/dmidecode -t 17"], runner.Lines); // never through the helper, which does not offer it
    }

    [Fact]
    public async Task Without_dmidecode_it_fails_without_running_anything()
    {
        var runner = new FakeRunner();

        PrivilegedResult<string> result = await NoHelper(runner, Pkexec, "/usr/bin/cat").ReadMemoryDevicesTextAsync(Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task System_space_is_asked_of_the_helper_with_the_mount_point_as_its_only_argument()
    {
        var runner = new FakeRunner(Privileged.Json(new SystemSpaceResponse(HelperProtocol.Version, "btrfs", 1234, 5678), HelperJson.Default.SystemSpaceResponse));

        PrivilegedResult<HiddenSpaceReport> result = await Helper(runner).MeasureSystemSpaceAsync("/home", Token);

        Assert.Equal(new HiddenSpaceReport(1234, 5678), result.Value);
        RunCall call = Assert.Single(runner.Calls);
        Assert.Equal(Pkexec, call.File);
        Assert.Equal([HelperPath, "measure-system-space", "/home"], call.Arguments);
        Assert.Null(call.Input);
    }

    [Fact]
    public async Task System_space_null_parts_stay_null()
    {
        var runner = new FakeRunner(Privileged.Json(new SystemSpaceResponse(HelperProtocol.Version, "ext4", null, 42), HelperJson.Default.SystemSpaceResponse));

        PrivilegedResult<HiddenSpaceReport> result = await Helper(runner).MeasureSystemSpaceAsync("/", Token);

        Assert.Equal(new HiddenSpaceReport(null, 42), result.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("/with\0nul")]
    public async Task A_mount_point_that_is_not_an_absolute_path_is_never_passed_on(string mountPoint)
    {
        var runner = new FakeRunner();

        PrivilegedResult<HiddenSpaceReport> result = await Helper(runner).MeasureSystemSpaceAsync(mountPoint, Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
        Assert.Empty(runner.Calls);
    }

    private const string BtrfsMountInfo = """
        43 1 0:39 / / rw,relatime shared:1 - btrfs /dev/nvme0n1p2 rw,subvolid=5,subvol=/
        54 43 259:1 / /home rw,relatime shared:47 - ext4 /dev/nvme0n1p1 rw
        """;

    private const string BtrfsUsage = """
        Overall:
            Metadata ratio:		              2.00

        Data,single: Size:153553469440, Used:147889410048 (96.31%)

        Metadata,DUP: Size:5368709120, Used:4420976640 (82.35%)

        System,DUP: Size:33554432, Used:16384 (0.05%)

        """;

    private static readonly VfsCounters Counters = new(4096, 4096, 66609152, 28299139, 27865259);

    private static readonly long Reserved = (28299139L - 27865259L) * 4096;

    [Fact]
    public async Task Without_the_helper_btrfs_space_is_measured_by_three_fixed_btrfs_commands()
    {
        var runner = new FakeRunner(Privileged.Text(BtrfsUsage), Exit(1, error: "ERROR: quotas not enabled"), Privileged.Text(""));
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, Privileged.MissingLstat, mountInfo: BtrfsMountInfo, counters: Counters);

        PrivilegedResult<HiddenSpaceReport> result = await ops.MeasureSystemSpaceAsync("/", Token);

        Assert.Equal(
            [
                $"{Pkexec} /usr/bin/btrfs filesystem usage -b /",
                $"{Pkexec} /usr/bin/btrfs qgroup show -pcre --raw /",
                $"{Pkexec} /usr/bin/btrfs subvolume list -s /",
            ],
            runner.Lines);
        Assert.All(runner.Calls, call => Assert.Null(call.Input));
        Assert.Equal(new HiddenSpaceReport(null, Reserved + ((4420976640L + 16384L) * 2)), result.Value); // quotas off: only the snapshots are unknown
    }

    [Fact]
    public async Task The_btrfs_report_is_the_one_the_helper_builds_from_the_same_text()
    {
        const string qgroup = "Qgroupid Referenced Exclusive Parent Child\n0/257 100 100 --- ---\n";
        const string list = "ID 257 gen 1 cgen 1 top level 5 otime 2024-01-01 00:00:00 path snap\n";
        var runner = new FakeRunner(Privileged.Text(BtrfsUsage), Privileged.Text(qgroup), Privileged.Text(list));
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, Privileged.UserOwnedLstat, mountInfo: BtrfsMountInfo, counters: Counters);

        PrivilegedResult<HiddenSpaceReport> result = await ops.MeasureSystemSpaceAsync("/", Token);

        SystemSpaceResponse expected = BtrfsSpace.Build("btrfs", Reserved, BtrfsUsage, list, qgroup);
        Assert.Equal(new HiddenSpaceReport(expected.SnapshotBytes, expected.MetadataBytes), result.Value);
    }

    [Fact]
    public async Task Without_the_helper_a_file_system_that_is_not_btrfs_needs_no_prompt()
    {
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, Privileged.MissingLstat, mountInfo: BtrfsMountInfo, counters: Counters);

        PrivilegedResult<HiddenSpaceReport> result = await ops.MeasureSystemSpaceAsync("/home/user", Token);

        Assert.Equal(new HiddenSpaceReport(null, Reserved), result.Value);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_dismissed_btrfs_prompt_ends_the_measurement_after_one_prompt()
    {
        var runner = new FakeRunner(Exit(126));
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, Privileged.MissingLstat, mountInfo: BtrfsMountInfo, counters: Counters);

        PrivilegedResult<HiddenSpaceReport> result = await ops.MeasureSystemSpaceAsync("/", Token);

        Assert.Equal(PrivilegedOutcome.Cancelled, result.Outcome);
        Assert.Single(runner.Calls);
    }

    // ---- the outcome matrix ----

    [Theory]
    [InlineData("NotStarted", -1, "", PrivilegedOutcome.NotAvailable, PrivilegedUnavailableReason.NoPkexec)]
    [InlineData("Exited", 126, "", PrivilegedOutcome.Cancelled, null)]
    [InlineData("Exited", 127, "", PrivilegedOutcome.Cancelled, null)]
    [InlineData("Exited", 127, "Error getting authority: Error initializing authority: No authentication agent found.", PrivilegedOutcome.NotAvailable, PrivilegedUnavailableReason.NoAuthenticationAgent)]
    [InlineData("Exited", 127, "NO AUTHENTICATION AGENT FOUND", PrivilegedOutcome.NotAvailable, PrivilegedUnavailableReason.NoAuthenticationAgent)]
    [InlineData("Exited", 127, "Error executing command as another user: Not authorized", PrivilegedOutcome.Cancelled, null)]
    [InlineData("Exited", 2, "pennyworth-helper: usage", PrivilegedOutcome.NotAvailable, PrivilegedUnavailableReason.HelperOutdated)]
    [InlineData("Exited", 1, "pennyworth-helper: boom", PrivilegedOutcome.Failed, null)]
    [InlineData("Exited", 3, "pennyworth-helper: refused", PrivilegedOutcome.Failed, null)]
    [InlineData("Exited", 4, "pennyworth-helper: too large", PrivilegedOutcome.Failed, null)]
    [InlineData("Exited", 5, "pennyworth-helper: timed out", PrivilegedOutcome.Failed, null)]
    [InlineData("Exited", 6, "pennyworth-helper: no table", PrivilegedOutcome.Failed, null)]
    [InlineData("Exited", 137, "", PrivilegedOutcome.Failed, null)]
    [InlineData("Exited", 143, "", PrivilegedOutcome.Failed, null)]
    [InlineData("TimedOut", -1, "", PrivilegedOutcome.Failed, null)]
    [InlineData("TooLarge", -1, "", PrivilegedOutcome.Failed, null)]
    public async Task The_helpers_and_pkexecs_answers_map_to_outcomes_and_never_to_a_second_prompt(
        string status, int exitCode, string error, PrivilegedOutcome outcome, PrivilegedUnavailableReason? reason)
    {
        ElevatedRun answer = new(Enum.Parse<ElevatedStatus>(status), exitCode, [], error);
        var runner = new FakeRunner(answer);

        PrivilegedResult<byte[]> result = await Helper(runner).ReadFirmwareTablesAsync(Token);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Single(runner.Calls);
        if (outcome == PrivilegedOutcome.Failed && error.Length > 0)
        {
            Assert.Equal(error, result.Detail);
        }
    }

    [Theory]
    [InlineData(126, PrivilegedOutcome.Cancelled)]
    [InlineData(127, PrivilegedOutcome.Cancelled)]
    [InlineData(137, PrivilegedOutcome.Failed)]
    public async Task The_same_pkexec_codes_settle_a_fallback_run(int exitCode, PrivilegedOutcome outcome)
    {
        var runner = new FakeRunner(Exit(exitCode));

        PrivilegedResult<string> result = await NoHelper(runner).ReadMemoryDevicesTextAsync(Token);

        Assert.Equal(outcome, result.Outcome);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task A_helper_that_answers_another_version_is_outdated_for_every_subcommand()
    {
        int wrong = HelperProtocol.Version + 1;
        var runner = new FakeRunner(
            Privileged.Json(new FirmwareTablesResponse(wrong, ""), HelperJson.Default.FirmwareTablesResponse),
            Privileged.Json(new SystemSpaceResponse(wrong, "btrfs", 1, 2), HelperJson.Default.SystemSpaceResponse),
            Privileged.Json(new FoldersResponse(wrong, []), HelperJson.Default.FoldersResponse),
            Privileged.Json(new CleanResponse(wrong, 1, 1, 0), HelperJson.Default.CleanResponse));
        LinuxPrivilegedOperations ops = Helper(runner);

        Assert.Equal(PrivilegedUnavailableReason.HelperOutdated, (await ops.ReadFirmwareTablesAsync(Token)).Reason);
        Assert.Equal(PrivilegedUnavailableReason.HelperOutdated, (await ops.MeasureSystemSpaceAsync("/", Token)).Reason);
        Assert.Equal(PrivilegedUnavailableReason.HelperOutdated, (await ops.MeasureFoldersAsync(["/root"], Token)).Reason);
        Assert.Equal(PrivilegedUnavailableReason.HelperOutdated, (await ops.CleanSystemAsync(Estimate("system-crash-reports", File("/var/crash/a", 1)), Token)).Reason);
    }

    [Fact]
    public async Task A_helper_without_a_version_is_outdated()
    {
        var runner = new FakeRunner(Privileged.Text("""{"table":"AQID"}"""));

        PrivilegedResult<byte[]> result = await Helper(runner).ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedUnavailableReason.HelperOutdated, result.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"version\":1,\"table\":\"@@@not base64@@@\"}")]
    public async Task An_unreadable_answer_is_a_failure(string stdout)
    {
        var runner = new FakeRunner(Privileged.Text(stdout));

        PrivilegedResult<byte[]> result = await Helper(runner).ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task A_long_error_line_is_cut_and_only_its_first_line_is_kept()
    {
        var runner = new FakeRunner(Exit(1, error: new string('x', 5000) + "\nsecond line"));

        PrivilegedResult<byte[]> result = await Helper(runner).ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
        Assert.Equal(new string('x', 300), result.Detail);
    }

    [Fact]
    public async Task Without_a_message_the_detail_names_the_exit_code()
    {
        var runner = new FakeRunner(Exit(6));

        PrivilegedResult<byte[]> result = await Helper(runner).ReadFirmwareTablesAsync(Token);

        Assert.Equal("Exited with code 6.", result.Detail);
    }

    // ---- folders ----

    [Fact]
    public async Task Folders_are_sent_as_json_on_standard_input_and_mapped_back()
    {
        var response = new FoldersResponse(HelperProtocol.Version, [new FolderSize("/root", 4096, 100, 3, true), new FolderSize("/var/lib/x", 10, 9, 1, false)]);
        var runner = new FakeRunner(Privileged.Json(response, HelperJson.Default.FoldersResponse));

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> result = await Helper(runner).MeasureFoldersAsync(["/root", "/var/lib/x"], Token);

        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        Assert.Equal([new MeasuredFolder("/root", 4096, 100, 3, true), new MeasuredFolder("/var/lib/x", 10, 9, 1, false)], result.Value);
        RunCall call = Assert.Single(runner.Calls);
        Assert.Equal(Pkexec, call.File);
        Assert.Equal([HelperPath, "measure-folders"], call.Arguments);
        FoldersRequest request = Privileged.Parse(call.Input, HelperJson.Default.FoldersRequest);
        Assert.Equal(HelperProtocol.Version, request.Version);
        Assert.Equal(["/root", "/var/lib/x"], request.Paths);
        Assert.Equal(HelperProtocol.MaxResponseBytes, call.MaxOutput);
        Assert.True(call.Timeout > HelperProtocol.MaxRuntime); // the helper stops itself before the app gives up
    }

    [Fact]
    public async Task Paths_that_are_not_absolute_or_repeat_are_not_sent_and_nothing_to_measure_asks_for_nothing()
    {
        var runner = new FakeRunner(Privileged.Json(new FoldersResponse(HelperProtocol.Version, []), HelperJson.Default.FoldersResponse));
        LinuxPrivilegedOperations ops = Helper(runner);

        await ops.MeasureFoldersAsync(["relative", "", "/a\0b", "/root", "/root"], Token);
        PrivilegedResult<IReadOnlyList<MeasuredFolder>> none = await ops.MeasureFoldersAsync(["relative"], Token);

        Assert.Equal(["/root"], Privileged.Parse(Assert.Single(runner.Calls).Input, HelperJson.Default.FoldersRequest).Paths);
        Assert.Equal(PrivilegedOutcome.Done, none.Outcome);
        Assert.Empty(none.Value!);
    }

    [Fact]
    public async Task More_folders_than_one_request_holds_go_in_further_requests()
    {
        string[] paths = [.. Enumerable.Range(0, HelperProtocol.MaxFolders + 1).Select(i => $"/data/{i}")];
        var runner = new FakeRunner(
            Privileged.Json(new FoldersResponse(HelperProtocol.Version, [new FolderSize("/data/0", 1, 1, 1, true)]), HelperJson.Default.FoldersResponse),
            Privileged.Json(new FoldersResponse(HelperProtocol.Version, [new FolderSize("/data/512", 2, 2, 1, true)]), HelperJson.Default.FoldersResponse));

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> result = await Helper(runner).MeasureFoldersAsync(paths, Token);

        Assert.Equal(["/data/0", "/data/512"], result.Value!.Select(f => f.Path));
        Assert.Equal(
            [HelperProtocol.MaxFolders, 1],
            runner.Calls.Select(call => Privileged.Parse(call.Input, HelperJson.Default.FoldersRequest).Paths.Count));
    }

    [Fact]
    public async Task A_dismissed_first_request_stops_the_rest()
    {
        string[] paths = [.. Enumerable.Range(0, HelperProtocol.MaxFolders + 1).Select(i => $"/data/{i}")];
        var runner = new FakeRunner(Exit(126));

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> result = await Helper(runner).MeasureFoldersAsync(paths, Token);

        Assert.Equal(PrivilegedOutcome.Cancelled, result.Outcome);
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData("missing", PrivilegedUnavailableReason.HelperNotInstalled)]
    [InlineData("group-writable", PrivilegedUnavailableReason.HelperNotTrusted)]
    public async Task Folders_and_cleanup_never_fall_back_to_anything_else(string state, PrivilegedUnavailableReason reason)
    {
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, Lstat(state));

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> folders = await ops.MeasureFoldersAsync(["/root"], Token);
        PrivilegedResult<CleanupResult> clean = await ops.CleanSystemAsync(Estimate("system-crash-reports", File("/var/crash/a", 1)), Token);

        Assert.Equal((PrivilegedOutcome.NotAvailable, reason), (folders.Outcome, folders.Reason));
        Assert.Equal((PrivilegedOutcome.NotAvailable, reason), (clean.Outcome, clean.Reason));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Folders_and_cleanup_without_pkexec_are_not_available()
    {
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = Privileged.Ops(runner, Privileged.TrustedLstat, ["/usr/bin/cat"]);

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> folders = await ops.MeasureFoldersAsync(["/root"], Token);
        PrivilegedResult<CleanupResult> clean = await ops.CleanSystemAsync(Estimate("system-crash-reports", File("/var/crash/a", 1)), Token);

        Assert.Equal(PrivilegedUnavailableReason.NoPkexec, folders.Reason);
        Assert.Equal(PrivilegedUnavailableReason.NoPkexec, clean.Reason);
        Assert.Empty(runner.Calls);
    }

    // ---- cleanup ----

    private static CleanupRule Rule(string id, bool requiresAdmin = true) =>
        new(id, SafetyLevel.Safe, CleanupKind.FolderContents, [], requiresAdmin, new LocalizedText("t", "t"), new LocalizedText("d", "d"), new LocalizedText("c", "c"));

    private static CleanupFile File(string path, long size, long ticks = 638_000_000_000_000_000) => new(path, size, new DateTime(ticks, DateTimeKind.Utc));

    private static CleanupEstimate Estimate(string ruleId, params CleanupFile[] files) =>
        new(Rule(ruleId), files.Sum(f => f.Size), files.Length, true, Files: files);

    private static ElevatedRun CleanAnswer(long freed, long deleted, long skipped) =>
        Privileged.Json(new CleanResponse(HelperProtocol.Version, freed, deleted, skipped), HelperJson.Default.CleanResponse);

    [Fact]
    public async Task Cleaning_sends_the_preview_as_json_with_the_rule_as_the_only_argument()
    {
        var runner = new FakeRunner(CleanAnswer(300, 2, 1));
        CleanupEstimate estimate = Estimate("system-package-cache", File("/var/cache/zypp/packages/repo/a.rpm", 100, 111), File("/var/cache/apt/archives/b.deb", 200, 222), File("/var/cache/apt/archives/partial/c", 5));

        PrivilegedResult<CleanupResult> result = await Helper(runner).CleanSystemAsync(estimate, Token);

        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        Assert.Equal(new CleanupResult(estimate.Rule, 300, 2, 1 + 1), result.Value); // the helper's skipped plus the kept apt folder never sent
        RunCall call = Assert.Single(runner.Calls);
        Assert.Equal(Pkexec, call.File);
        Assert.Equal([HelperPath, "clean-system", "system-package-cache"], call.Arguments);
        CleanRequest request = Privileged.Parse(call.Input, HelperJson.Default.CleanRequest);
        Assert.Equal(HelperProtocol.Version, request.Version);
        Assert.Equal(
            [
                new CleanFile("/var/cache/zypp/packages", "repo/a.rpm", 100, 111),
                new CleanFile("/var/cache/apt/archives", "b.deb", 200, 222),
            ],
            request.Files);
        Assert.True(call.Timeout > HelperProtocol.MaxRuntime);
    }

    [Fact]
    public async Task Only_a_rule_the_helper_knows_that_needs_admin_is_ever_cleaned()
    {
        var runner = new FakeRunner();
        LinuxPrivilegedOperations ops = Helper(runner);

        PrivilegedResult<CleanupResult> unknown = await ops.CleanSystemAsync(Estimate("browser-cache", File("/var/crash/a", 1)), Token);
        PrivilegedResult<CleanupResult> unprivileged = await ops.CleanSystemAsync(new CleanupEstimate(Rule("system-crash-reports", requiresAdmin: false), 1, 1, true, Files: [File("/var/crash/a", 1)]), Token);

        Assert.Equal(PrivilegedOutcome.Failed, unknown.Outcome);
        Assert.Equal(PrivilegedOutcome.Failed, unprivileged.Outcome);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Each_file_goes_with_the_allowlisted_root_that_is_its_exact_prefix()
    {
        string[] roots = ["/var/cache/zypp/packages", "/var/cache/dnf"];
        CleanupFile[] preview =
        [
            File("/var/cache/zypp/packages/x.rpm", 1),
            File("/var/cache/dnf/repo/y.rpm", 2),
            File("/var/cache/dnfx/z", 3), // a different folder that shares a prefix
            File("/var/cache/dnf", 4), // the root itself
            File("/var/cache/dnf/", 5), // nothing below it
            File("/etc/passwd", 6),
            File("/var/cache/zypp/packages/../../../etc/shadow", 7),
            File("/var/cache/dnf/a//b", 8),
            File("/var/cache/dnf/./c", 9),
            File("relative/path", 10),
        ];

        (List<CleanFile> files, _, long notSent) = LinuxPrivilegedOperations.Prepare(preview, roots);

        Assert.Equal([new CleanFile("/var/cache/zypp/packages", "x.rpm", 1, File("", 1).ChangedUtc.Ticks), new CleanFile("/var/cache/dnf", "repo/y.rpm", 2, File("", 1).ChangedUtc.Ticks)], files);
        Assert.Equal(8, notSent);
    }

    [Fact]
    public void Apts_own_working_files_are_never_sent()
    {
        string[] roots = ["/var/cache/apt/archives"];
        CleanupFile[] preview =
        [
            File("/var/cache/apt/archives/lock", 1),
            File("/var/cache/apt/archives/partial/x.deb", 2),
            File("/var/cache/apt/archives/partial", 3),
            File("/var/cache/apt/archives/locked.deb", 4),
            File("/var/cache/apt/archives/pkg/lock", 5),
        ];

        (List<CleanFile> files, _, long notSent) = LinuxPrivilegedOperations.Prepare(preview, roots);

        Assert.Equal(["locked.deb", "pkg/lock"], files.Select(f => f.Path));
        Assert.Equal(3, notSent);
    }

    [Fact]
    public void A_name_that_is_not_text_is_not_sent()
    {
        string[] roots = ["/var/crash"];
        CleanupFile[] preview =
        [
            File("/var/crash/caf\u00e9.crash", 1), // ordinary non-ASCII is fine
            File("/var/crash/bad\uFFFD.crash", 2), // what .NET shows for bytes that are not UTF-8
            File("/var/crash/lone\uD800.crash", 3), // a lone surrogate would be written as U+FFFD
            File("/var/crash/nul\0.crash", 4),
        ];

        (List<CleanFile> files, byte[] request, long notSent) = LinuxPrivilegedOperations.Prepare(preview, roots);

        Assert.Equal(["caf\u00e9.crash"], files.Select(f => f.Path));
        Assert.Equal(3, notSent);
        Assert.Equal(["caf\u00e9.crash"], Privileged.Parse(request, HelperJson.Default.CleanRequest).Files.Select(f => f.Path));
    }

    [Fact]
    public void No_more_than_the_most_files_a_request_may_list_are_sent()
    {
        string[] roots = ["/var/crash"];
        CleanupFile[] preview = [.. Enumerable.Range(0, HelperProtocol.MaxCleanFiles + 7).Select(i => File($"/var/crash/{i}", 1))];

        (List<CleanFile> files, byte[] request, long notSent) = LinuxPrivilegedOperations.Prepare(preview, roots);

        Assert.Equal(HelperProtocol.MaxCleanFiles, files.Count);
        Assert.Equal(7, notSent);
        Assert.Equal(HelperProtocol.MaxCleanFiles, Privileged.Parse(request, HelperJson.Default.CleanRequest).Files.Count);
        Assert.Equal("0", files[0].Path); // the first ones are kept, in order
    }

    [Fact]
    public void A_preview_too_big_for_the_request_is_cut_to_what_fits()
    {
        string[] roots = ["/var/crash"];
        string name = new('n', 3000);
        CleanupFile[] preview = [.. Enumerable.Range(0, 12_000).Select(i => File($"/var/crash/{i}{name}", 1))]; // about 36 MB of names

        (List<CleanFile> files, byte[] request, long notSent) = LinuxPrivilegedOperations.Prepare(preview, roots);

        Assert.InRange(request.Length, 1, HelperProtocol.MaxRequestBytes);
        Assert.InRange(files.Count, 1, 11_999);
        Assert.Equal(preview.Length - files.Count, notSent);
        Assert.Equal(files.Count, Privileged.Parse(request, HelperJson.Default.CleanRequest).Files.Count);
    }

    [Fact]
    public async Task A_preview_with_nothing_to_send_asks_for_no_password()
    {
        var runner = new FakeRunner();
        CleanupEstimate estimate = Estimate("system-crash-reports", File("/etc/passwd", 5), File("/var/crash/../x", 6));

        PrivilegedResult<CleanupResult> result = await Helper(runner).CleanSystemAsync(estimate, Token);

        Assert.Equal(new CleanupResult(estimate.Rule, 0, 0, 2), result.Value);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_dismissed_cleanup_prompt_is_cancelled_and_not_asked_again()
    {
        var runner = new FakeRunner(Exit(126));

        PrivilegedResult<CleanupResult> result = await Helper(runner).CleanSystemAsync(Estimate("system-crash-reports", File("/var/crash/a", 1)), Token);

        Assert.Equal(PrivilegedOutcome.Cancelled, result.Outcome);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task A_refusing_helper_is_a_failure_with_its_reason()
    {
        var runner = new FakeRunner(Exit(3, error: "pennyworth-helper: /var/crash is not owned by root"));

        PrivilegedResult<CleanupResult> result = await Helper(runner).CleanSystemAsync(Estimate("system-crash-reports", File("/var/crash/a", 1)), Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
        Assert.Equal("pennyworth-helper: /var/crash is not owned by root", result.Detail);
    }

    [Fact]
    public async Task The_admin_cleaner_forwards_to_the_operations()
    {
        var runner = new FakeRunner(CleanAnswer(1, 1, 0));
        var cleaner = new LinuxAdminCleaner(Helper(runner));
        CleanupEstimate estimate = Estimate("system-crash-reports", File("/var/crash/a", 1));

        PrivilegedResult<CleanupResult> result = await cleaner.CleanAsync(estimate, Token);

        Assert.Equal(PrivilegedCapability.Ready, cleaner.Capability);
        Assert.Equal(new CleanupResult(estimate.Rule, 1, 1, 0), result.Value);
    }

    [Fact]
    public void The_admin_cleaners_capability_is_the_cleanup_capability()
    {
        Assert.Equal(PrivilegedUnavailableReason.HelperNotInstalled, new LinuxAdminCleaner(Privileged.Ops(new FakeRunner(), Privileged.MissingLstat)).Capability.Reason);
        Assert.Equal(PrivilegedUnavailableReason.NoPkexec, new LinuxAdminCleaner(Privileged.Ops(new FakeRunner(), Privileged.TrustedLstat, [])).Capability.Reason);
    }

    // ---- the protocol ----

    [Fact]
    public async Task Every_subcommand_is_run_with_its_own_fixed_arguments_and_nothing_else()
    {
        var runner = new FakeRunner(
            FirmwareAnswer([1]),
            Privileged.Json(new SystemSpaceResponse(HelperProtocol.Version, "btrfs", null, null), HelperJson.Default.SystemSpaceResponse),
            Privileged.Json(new FoldersResponse(HelperProtocol.Version, []), HelperJson.Default.FoldersResponse),
            CleanAnswer(0, 0, 0));
        LinuxPrivilegedOperations ops = Helper(runner);

        await ops.ReadFirmwareTablesAsync(Token);
        await ops.MeasureSystemSpaceAsync("/mnt/data", Token);
        await ops.MeasureFoldersAsync(["/root"], Token);
        await ops.CleanSystemAsync(Estimate("system-package-cache", File("/var/cache/dnf/a", 1)), Token);

        Assert.All(runner.Calls, call =>
        {
            Assert.Equal(Pkexec, call.File);
            Assert.Equal(HelperPath, call.Arguments[0]);
        });
        Assert.Equal(HelperProtocol.Subcommands, runner.Calls.Select(call => call.Arguments[1]));
        Assert.Equal(
            [
                [HelperPath, "read-firmware-tables"],
                [HelperPath, "measure-system-space", "/mnt/data"],
                [HelperPath, "measure-folders"],
                [HelperPath, "clean-system", "system-package-cache"],
            ],
            runner.Calls.Select(call => call.Arguments));
        Assert.Equal([false, false, true, true], runner.Calls.Select(call => call.Input is not null));
    }

    [Fact]
    public void The_helper_path_is_the_one_the_polkit_actions_name()
    {
        Assert.Equal("/usr/libexec/pennyworth/pennyworth-helper", HelperProtocol.InstalledPath);
        Assert.Equal(["read-firmware-tables", "measure-system-space", "measure-folders", "clean-system"], HelperProtocol.Subcommands);
    }

    [Fact]
    public void Every_message_survives_the_round_trip_through_json()
    {
        Assert.Equal(new FirmwareTablesResponse(1, "AQID"), RoundTrip(new FirmwareTablesResponse(1, "AQID"), HelperJson.Default.FirmwareTablesResponse));
        Assert.Equal(new SystemSpaceResponse(1, "btrfs", 5, null), RoundTrip(new SystemSpaceResponse(1, "btrfs", 5, null), HelperJson.Default.SystemSpaceResponse));
        Assert.Equal(new SystemSpaceResponse(1, "ext4", null, 7), RoundTrip(new SystemSpaceResponse(1, "ext4", null, 7), HelperJson.Default.SystemSpaceResponse));

        var folders = new FoldersRequest(1, ["/root", "/var/lib/caf\u00e9"]);
        Assert.Equal(folders.Paths, RoundTrip(folders, HelperJson.Default.FoldersRequest).Paths);

        var sizes = new FoldersResponse(1, [new FolderSize("/root", 4096, 100, 3, true), new FolderSize("/x", 1, 1, 1, false)]);
        Assert.Equal(sizes.Folders, RoundTrip(sizes, HelperJson.Default.FoldersResponse).Folders);

        var request = new CleanRequest(1, [new CleanFile("/var/crash", "a/b.crash", 10, 638_000_000_000_000_000)]);
        Assert.Equal(request.Files, RoundTrip(request, HelperJson.Default.CleanRequest).Files);

        Assert.Equal(new CleanResponse(1, 10, 2, 3), RoundTrip(new CleanResponse(1, 10, 2, 3), HelperJson.Default.CleanResponse));
    }

    [Fact]
    public void The_wire_names_are_camelCase_and_the_ticks_are_a_plain_number()
    {
        string request = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(new CleanRequest(1, [new CleanFile("/var/crash", "a", 10, 638000000000000000)]), HelperJson.Default.CleanRequest));
        string space = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(new SystemSpaceResponse(1, "btrfs", null, 2), HelperJson.Default.SystemSpaceResponse));

        Assert.Equal("""{"version":1,"files":[{"root":"/var/crash","path":"a","size":10,"modifiedTicks":638000000000000000}]}""", request);
        Assert.Equal("""{"version":1,"fileSystem":"btrfs","snapshotBytes":null,"metadataBytes":2}""", space);
    }

    private static T RoundTrip<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, type), type)!;

    // ---- the process ----

    [LinuxFact]
    public async Task Cancelling_kills_the_process_and_returns_at_once()
    {
        using var source = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        LinuxPrivilegedOperations ops = Privileged.Ops(
            (_, _, input, max, timeout, token) => ElevatedRunner.RunAsync("/bin/sleep", ["30"], input, max, timeout, token),
            Privileged.TrustedLstat,
            timeout: TimeSpan.FromSeconds(60));

        Task<PrivilegedResult<byte[]>> pending = ops.ReadFirmwareTablesAsync(source.Token);
        await Task.Delay(200, Token);
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed}");
    }

    [LinuxFact]
    public async Task A_prompt_nobody_answers_ends_at_the_timeout_as_a_failure()
    {
        var clock = Stopwatch.StartNew();
        LinuxPrivilegedOperations ops = Privileged.Ops(
            (_, _, input, max, timeout, token) => ElevatedRunner.RunAsync("/bin/sleep", ["30"], input, max, timeout, token),
            Privileged.TrustedLstat,
            timeout: TimeSpan.FromMilliseconds(300));

        PrivilegedResult<byte[]> result = await ops.ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed}");
    }

    [LinuxFact]
    public async Task An_answer_over_the_limit_is_a_failure()
    {
        LinuxPrivilegedOperations ops = Privileged.Ops(
            (_, _, input, _, timeout, token) => ElevatedRunner.RunAsync("/bin/sh", ["-c", "head -c 5000 /dev/zero"], input, 1000, timeout, token),
            Privileged.TrustedLstat,
            timeout: TimeSpan.FromSeconds(30));

        PrivilegedResult<byte[]> result = await ops.ReadFirmwareTablesAsync(Token);

        Assert.Equal(PrivilegedOutcome.Failed, result.Outcome);
    }

    [LinuxFact]
    public async Task The_request_reaches_a_real_process_and_its_answer_is_read_back()
    {
        // cat plays a helper that answers with what it was sent: the request is a valid CleanResponse-shaped document.
        LinuxPrivilegedOperations ops = Privileged.Ops(
            (_, _, input, max, timeout, token) => ElevatedRunner.RunAsync(
                "/bin/sh",
                ["-c", "cat >/dev/null; printf '%s' '{\"version\":1,\"freedBytes\":9,\"deleted\":1,\"skipped\":0}'"],
                input,
                max,
                timeout,
                token),
            Privileged.TrustedLstat,
            timeout: TimeSpan.FromSeconds(30));
        CleanupEstimate estimate = Estimate("system-crash-reports", File("/var/crash/a.crash", 9));

        PrivilegedResult<CleanupResult> result = await ops.CleanSystemAsync(estimate, Token);

        Assert.Equal(new CleanupResult(estimate.Rule, 9, 1, 0), result.Value);
    }
}
