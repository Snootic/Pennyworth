using Pennyworth.Core.Privilege;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>
/// The opt-in, elevated read of the memory modules. The real tables are root-only, so these run on a
/// synthetic table built from the SMBIOS specification and on a fake runner that plays pkexec.
/// </summary>
public class MemoryModuleTests
{
    private const string Pkexec = Privileged.Pkexec;
    private const string Dmidecode = "/usr/sbin/dmidecode";

    private static ElevatedRun Exit(int code, byte[]? output = null, string error = "") => Privileged.Exit(code, output, error);

    private static ElevatedRun Text(string text) => Privileged.Text(text);

    /// <summary>The reader over the generic pkexec route (no helper installed), with only <paramref name="installed"/> found.</summary>
    private static PrivilegedMemoryReader Reader(FakeRunner runner, params string[] installed) =>
        new(Privileged.Ops(runner, Privileged.MissingLstat, installed));

    /// <summary>The reader over the helper (installed and trusted).</summary>
    private static PrivilegedMemoryReader HelperReader(FakeRunner runner, params string[] installed) =>
        new(Privileged.Ops(runner, Privileged.TrustedLstat, installed));

    private static ElevatedRun HelperTable(byte[] table) =>
        Privileged.Json(new FirmwareTablesResponse(HelperProtocol.Version, Convert.ToBase64String(table)), HelperJson.Default.FirmwareTablesResponse);

    // ---- the synthetic table, through the reader Windows uses ----

    [Fact]
    public void The_table_gives_the_fields_Windows_shows()
    {
        SmbiosInfo info = Smbios.ParseStructures(SmbiosTable.ThreeSlots());

        Assert.Equal(3, info.MemorySlots);
        Assert.Equal(2, info.Modules.Count); // the empty slot is skipped
        Assert.Equal(new MemoryModule("DIMM_A1", 8L << 30, "DDR4", 3200, 3000, "Kingston", "KF3200C16D4/8GX", "DIMM"), info.Modules[0]);
        Assert.Equal(new MemoryModule("DIMM_B1", 32L << 30, "DDR5", 6000, 4800, null, "CMK32GX5M1B6000", "SO-DIMM"), info.Modules[1]);
    }

    [Fact]
    public void The_headerless_table_and_the_Windows_blob_read_the_same_modules()
    {
        byte[] table = SmbiosTable.ThreeSlots();

        SmbiosInfo windows = Smbios.Parse([.. new byte[8], .. table]); // what GetSystemFirmwareTable returns

        Assert.Equal(Smbios.ParseStructures(table).Modules, windows.Modules);
    }

    [Fact]
    public void The_dmidecode_text_gives_the_same_modules_as_the_table()
    {
        (IReadOnlyList<MemoryModule> modules, int? slots) = DmidecodeMemory.Parse(SmbiosTable.ThreeSlotsDmidecode);
        SmbiosInfo table = Smbios.ParseStructures(SmbiosTable.ThreeSlots());

        Assert.Equal(table.Modules, modules);
        Assert.Equal(table.MemorySlots, slots);
    }

    [Fact]
    public void Older_dmidecode_text_reads_sizes_in_MB_and_speeds_in_MHz()
    {
        const string text = """
            Handle 0x0010, DMI type 17, 34 bytes
            Memory Device
            	Size: 4096 MB
            	Form Factor: SODIMM
            	Locator: ChannelA-DIMM0
            	Type: DDR3
            	Speed: 1600 MHz
            	Manufacturer: Samsung
            	Part Number: M471B5173EB0-YK0
            	Configured Clock Speed: 1333 MHz

            Handle 0x0011, DMI type 17, 34 bytes
            Memory Device
            	Size: 512 kB
            	Locator: Cache
            """;

        (IReadOnlyList<MemoryModule> modules, int? slots) = DmidecodeMemory.Parse(text);

        Assert.Equal(2, slots);
        Assert.Equal(new MemoryModule("ChannelA-DIMM0", 4L << 30, "DDR3", 1600, 1333, "Samsung", "M471B5173EB0-YK0", "SO-DIMM"), modules[0]);
        Assert.Equal(512L << 10, modules[1].Size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not dmidecode output at all")]
    [InlineData("Handle 0x0000, DMI type 0, 24 bytes\nBIOS Information\n\tVendor: AMI\n")]
    public void Text_without_memory_devices_gives_nothing(string text)
    {
        (IReadOnlyList<MemoryModule> modules, int? slots) = DmidecodeMemory.Parse(text);

        Assert.Empty(modules);
        Assert.Null(slots);
    }

    [Fact]
    public void No_prefix_of_the_table_breaks_the_parser()
    {
        byte[] table = SmbiosTable.ThreeSlots();

        for (int length = 0; length <= table.Length; length++)
        {
            Exception? failure = Record.Exception(() => Smbios.ParseStructures(table.AsSpan(0, length)));
            Assert.True(failure is null, $"{length} bytes: {failure}");
        }
    }

    // ---- the outcome of the elevated read ----

    [Fact]
    public async Task Reads_the_table_through_pkexec_cat_by_absolute_paths()
    {
        var runner = new FakeRunner(Exit(0, SmbiosTable.ThreeSlots()));

        MemoryModulesResult result = await Reader(runner, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Read, result.Outcome);
        Assert.Equal(["DIMM_A1", "DIMM_B1"], result.Modules.Select(m => m.Slot));
        Assert.Equal(3, result.Slots);
        Assert.Equal([$"{Pkexec} /usr/bin/cat /sys/firmware/dmi/tables/DMI"], runner.Lines); // one prompt, no PATH lookup, no sudo
    }

    [Fact]
    public async Task Reads_the_table_through_the_helper_when_it_is_trusted()
    {
        var runner = new FakeRunner(HelperTable(SmbiosTable.ThreeSlots()));

        MemoryModulesResult result = await HelperReader(runner, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Read, result.Outcome);
        Assert.Equal(["DIMM_A1", "DIMM_B1"], result.Modules.Select(m => m.Slot));
        Assert.Equal([$"{Pkexec} {HelperProtocol.InstalledPath} read-firmware-tables"], runner.Lines);
    }

    [Theory]
    [InlineData(126)] // the dialog was dismissed
    [InlineData(127)] // not authorised
    public async Task A_dismissed_or_refused_prompt_is_cancelled_and_is_not_asked_again(int exitCode)
    {
        var runner = new FakeRunner(Exit(exitCode));

        MemoryModulesResult result = await Reader(runner, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Cancelled, result.Outcome);
        Assert.Empty(result.Modules);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task A_dismissed_helper_prompt_is_cancelled_and_not_asked_again()
    {
        var runner = new FakeRunner(Exit(126));

        MemoryModulesResult result = await HelperReader(runner, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Cancelled, result.Outcome);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Without_an_authentication_agent_it_says_so_and_does_not_ask_again()
    {
        var runner = new FakeRunner(Exit(127, error: "Error getting authority: No authentication agent found.\n"));

        MemoryModulesResult result = await Reader(runner, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Unavailable, result.Outcome);
        Assert.Equal(PrivilegedUnavailableReason.NoAuthenticationAgent, result.Reason);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Without_pkexec_there_is_no_way_to_ask()
    {
        var runner = new FakeRunner();

        MemoryModulesResult result = await Reader(runner, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Unavailable, result.Outcome);
        Assert.Equal(PrivilegedUnavailableReason.NoPkexec, result.Reason);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task An_outdated_helper_is_unavailable_with_its_reason()
    {
        var runner = new FakeRunner(Privileged.Json(new FirmwareTablesResponse(HelperProtocol.Version + 1, ""), HelperJson.Default.FirmwareTablesResponse));

        MemoryModulesResult result = await HelperReader(runner, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Unavailable, result.Outcome);
        Assert.Equal(PrivilegedUnavailableReason.HelperOutdated, result.Reason);
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData("NotStarted")]
    [InlineData("TimedOut")] // nobody answered the prompt in time
    [InlineData("TooLarge")]
    public async Task A_run_that_never_finished_is_unavailable_and_is_not_repeated(string status)
    {
        var runner = new FakeRunner(ElevatedRun.Failed(Enum.Parse<ElevatedStatus>(status)));

        MemoryModulesResult result = await Reader(runner, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Unavailable, result.Outcome);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task A_pkexec_killed_by_a_signal_is_unavailable_and_does_not_prompt_again()
    {
        var runner = new FakeRunner(Exit(137));

        MemoryModulesResult result = await Reader(runner, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Unavailable, result.Outcome);
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData(1)] // the helper failed
    [InlineData(6)] // the table is not there
    public async Task A_failing_helper_is_unavailable_and_does_not_prompt_again(int exitCode)
    {
        var runner = new FakeRunner(Exit(exitCode, error: "pennyworth-helper: no table\n"));

        MemoryModulesResult result = await HelperReader(runner, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Reason);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Garbage_with_no_dmidecode_is_unavailable()
    {
        var runner = new FakeRunner(Exit(0, [1, 2, 3, 250, 250, 0, 0, 9]));

        MemoryModulesResult result = await Reader(runner, Pkexec, "/usr/bin/cat").ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Unavailable, result.Outcome);
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData(0)] // a table nobody can parse
    [InlineData(1)] // cat could not read it even as root
    public async Task Falls_back_to_dmidecode_when_the_table_gives_nothing(int catExit)
    {
        var runner = new FakeRunner(Exit(catExit, catExit == 0 ? [0xFF, 0xFF, 0xFF, 0xFF] : null), Text(SmbiosTable.ThreeSlotsDmidecode));

        MemoryModulesResult result = await Reader(runner, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Read, result.Outcome);
        Assert.Equal(2, result.Modules.Count);
        Assert.Equal(3, result.Slots);
        Assert.Equal([$"{Pkexec} /usr/bin/cat /sys/firmware/dmi/tables/DMI", $"{Pkexec} {Dmidecode} -t 17"], runner.Lines);
    }

    [Fact]
    public async Task The_helper_then_dmidecode_when_the_table_it_returns_gives_nothing()
    {
        var runner = new FakeRunner(HelperTable([0xFF, 0xFF, 0xFF, 0xFF]), Text(SmbiosTable.ThreeSlotsDmidecode));

        MemoryModulesResult result = await HelperReader(runner, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Read, result.Outcome);
        Assert.Equal([$"{Pkexec} {HelperProtocol.InstalledPath} read-firmware-tables", $"{Pkexec} {Dmidecode} -t 17"], runner.Lines);
    }

    [Fact]
    public async Task Uses_dmidecode_alone_when_there_is_no_cat()
    {
        var runner = new FakeRunner(Text(SmbiosTable.ThreeSlotsDmidecode));

        MemoryModulesResult result = await Reader(runner, Pkexec, Dmidecode).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryModulesOutcome.Read, result.Outcome);
        Assert.Equal([$"{Pkexec} {Dmidecode} -t 17"], runner.Lines);
    }

    [Fact]
    public async Task Dismissing_dmidecode_is_cancelled_and_dmidecode_failing_too_is_unavailable()
    {
        var dismissed = new FakeRunner(Exit(1), Exit(126));
        var failed = new FakeRunner(Exit(1), Exit(1));

        Assert.Equal(MemoryModulesOutcome.Cancelled, (await Reader(dismissed, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(MemoryModulesOutcome.Unavailable, (await Reader(failed, Pkexec, "/usr/bin/cat", Dmidecode).ReadAsync(TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task Cancelling_reaches_the_runner_and_surfaces_as_a_cancellation()
    {
        using var source = new CancellationTokenSource();
        CancellationToken seen = default;
        var reader = new PrivilegedMemoryReader(Privileged.Ops(
            (_, _, _, _, _, token) =>
            {
                seen = token;
                source.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(Exit(0));
            },
            Privileged.MissingLstat,
            [Pkexec, "/usr/bin/cat"]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(source.Token));
        Assert.Equal(source.Token, seen);
    }

    [Fact]
    public async Task Gives_the_runner_the_long_wait_a_person_needs()
    {
        var runner = new FakeRunner(Exit(126));
        var reader = new PrivilegedMemoryReader(Privileged.Ops(runner, Privileged.MissingLstat, [Pkexec, "/usr/bin/cat"], timeout: LinuxPrivilegedOperations.PromptTimeout));

        await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(120), runner.Calls.Single().Timeout);
    }
}
