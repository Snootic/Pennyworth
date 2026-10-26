using Pennyworth.Monitoring.Hardware;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public class LinuxSpecsTests
{
    /// <summary>This machine's <c>/etc/os-release</c> (openSUSE Tumbleweed).</summary>
    private const string TumbleweedOsRelease = """
        NAME="openSUSE Tumbleweed"
        # VERSION="20261024"
        ID="opensuse-tumbleweed"
        ID_LIKE="opensuse suse"
        VERSION_ID="20261024"
        PRETTY_NAME="openSUSE Tumbleweed"
        ANSI_COLOR="0;32"
        # CPE 2.3 format, boo#1217921
        CPE_NAME="cpe:2.3:o:opensuse:tumbleweed:20261024:*:*:*:*:*:*:*"
        #CPE 2.2 format
        #CPE_NAME="cpe:/o:opensuse:tumbleweed:20261024"
        BUG_REPORT_URL="https://bugzilla.opensuse.org"
        LOGO="distributor-logo-Tumbleweed"
        """;

    private static readonly Func<string, IReadOnlyList<string>, string?> NoTools = (_, _) => null;

    // ---- os-release, uptime, install date ----

    [Fact]
    public void Parses_this_machines_os_release_and_ignores_the_commented_out_lines()
    {
        var values = OsRelease.Parse(TumbleweedOsRelease);

        Assert.Equal("openSUSE Tumbleweed", values["PRETTY_NAME"]);
        Assert.Equal("20261024", values["VERSION_ID"]);
        Assert.Equal("opensuse suse", values["ID_LIKE"]);
        Assert.False(values.ContainsKey("VERSION")); // Only the commented-out "# VERSION=" exists.
        Assert.False(values.ContainsKey("#CPE_NAME"));
        Assert.Equal("cpe:2.3:o:opensuse:tumbleweed:20261024:*:*:*:*:*:*:*", values["CPE_NAME"]);
    }

    [Fact]
    public void Follows_shell_quoting_rules()
    {
        var values = OsRelease.Parse("A=\"say \\\"hi\\\" \\$HOME \\\\ \\n\"\nB='it\\'s'\nC=plain\\ value\nD=\"unterminated\nE=first\nE=second\r\nF=\n=nokey\nnoequals");

        Assert.Equal("say \"hi\" $HOME \\ \\n", values["A"]); // In double quotes \n is not an escape, \" \$ \\ are.
        Assert.Equal("it\\'s", values["B"]);                    // Single quotes are literal.
        Assert.Equal("plain value", values["C"]);
        Assert.Equal("\"unterminated", values["D"]);
        Assert.Equal("second", values["E"]);                    // The last assignment wins, and CRLF is tolerated.
        Assert.Equal("", values["F"]);
        Assert.False(values.ContainsKey(""));
    }

    [Theory]
    [InlineData("150572.40 4015209.13\n", 150572.40)]
    [InlineData("0.00 0.00", 0.0)]
    public void Reads_uptime_from_the_first_field(string text, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), OsRelease.ParseUptime(text));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5 3")]
    [InlineData(null)]
    public void Unreadable_uptime_is_unknown(string? text) => Assert.Null(OsRelease.ParseUptime(text));

    [Fact]
    public void Stat_birth_time_zero_means_the_filesystem_does_not_record_it_not_1970()
    {
        Assert.Null(OsRelease.ParseBirthTime("0\n"));
        Assert.Null(OsRelease.ParseBirthTime("-\n")); // Newer coreutils print "-".
        Assert.Null(OsRelease.ParseBirthTime(null));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1780179277), OsRelease.ParseBirthTime("1780179277\n"));
    }

    [Fact]
    public void Describes_the_os_from_os_release_and_the_kernel()
    {
        using var tree = new SpecsTree();
        tree.File("/etc/os-release", TumbleweedOsRelease).File("/proc/sys/kernel/osrelease", "7.2.11-1-default\n").File("/proc/uptime", "3600.5 100.0\n");

        OsSpecs os = OsRelease.Read(new SpecsRoot(tree.Root), NoTools);

        Assert.Equal("openSUSE Tumbleweed", os.Name);
        Assert.Equal("20261024", os.Version);
        Assert.Equal("7.2.11-1-default", os.Build);
        Assert.Equal(TimeSpan.FromSeconds(3600.5), os.Uptime);
        Assert.Null(os.InstalledOn);
    }

    [Fact]
    public void A_rolling_distribution_without_version_id_falls_back_to_build_id_and_usr_lib_os_release()
    {
        using var tree = new SpecsTree();
        tree.File("/usr/lib/os-release", "NAME=\"Arch Linux\"\nBUILD_ID=rolling\n");

        OsSpecs os = OsRelease.Read(new SpecsRoot(tree.Root), NoTools);

        Assert.Equal("Arch Linux", os.Name); // No PRETTY_NAME: NAME is next.
        Assert.Equal("rolling", os.Version);
        Assert.Equal("?", os.Build);
    }

    [Fact]
    public void Install_date_is_the_birth_time_of_the_root_and_falls_back_to_machine_id()
    {
        using var tree = new SpecsTree();
        tree.File("/etc/machine-id", "abc\n");
        var asked = new List<string>();
        string? Stat(string file, IReadOnlyList<string> arguments)
        {
            Assert.Equal("stat", file);
            Assert.Equal(["-c", "%W"], arguments.Take(2));
            asked.Add(arguments[2]);
            return arguments[2] == tree.Full("/") ? "0\n" : "1780179850\n";
        }

        OsSpecs os = OsRelease.Read(new SpecsRoot(tree.Root), Stat);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1780179850), os.InstalledOn);
        Assert.Equal([tree.Full("/"), tree.Full("/etc/machine-id")], asked);
    }

    [Fact]
    public void Install_date_uses_the_machine_id_modification_time_when_no_birth_time_exists()
    {
        using var tree = new SpecsTree();
        tree.File("/etc/machine-id", "abc\n");
        System.IO.File.SetLastWriteTimeUtc(tree.Full("/etc/machine-id"), new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc));

        OsSpecs os = OsRelease.Read(new SpecsRoot(tree.Root), (_, _) => "0\n");

        Assert.Equal(new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero), os.InstalledOn);
    }

    // ---- DMI ----

    [Theory]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("to be filled by o.e.m.")]
    [InlineData("Default string")]
    [InlineData("System manufacturer")]
    [InlineData("System Product Name")]
    [InlineData("Unknown")]
    [InlineData("Not Specified")]
    [InlineData("0000000000")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void Firmware_placeholders_are_not_answers(string? text) => Assert.Null(Dmi.Clean(text));

    [Fact]
    public void Real_values_are_trimmed_and_kept()
    {
        Assert.Equal("X99 PR9", Dmi.Clean("X99 PR9 \n"));
        Assert.Equal("American Megatrends Inc.", Dmi.Clean("American Megatrends Inc."));
        Assert.Equal("ASUS", Dmi.Clean("ASUS"));
        Assert.Equal("Unknown Vendor Ltd", Dmi.Clean("Unknown Vendor Ltd")); // Only the whole word is a placeholder.
    }

    [Fact]
    public void Reads_the_world_readable_dmi_attributes_of_this_machine()
    {
        using var tree = new SpecsTree();
        tree.File("/sys/class/dmi/id/sys_vendor", "MACHINIST\n").File("/sys/class/dmi/id/product_name", "X99 PR9\n")
            .File("/sys/class/dmi/id/board_vendor", "MACHINIST\n").File("/sys/class/dmi/id/board_name", "X99 PR9\n")
            .File("/sys/class/dmi/id/board_version", "Default string\n")
            .File("/sys/class/dmi/id/bios_vendor", "American Megatrends Inc.\n").File("/sys/class/dmi/id/bios_version", "5.11\n").File("/sys/class/dmi/id/bios_date", "10/27/2023\n")
            .File("/proc/sys/kernel/hostname", "opensuse\n");
        var fs = new SpecsRoot(tree.Root);

        Assert.Equal(new BoardSpecs("MACHINIST", "X99 PR9", "American Megatrends Inc.", "5.11", "10/27/2023"), Dmi.ReadBoard(fs));
        SystemSpecs system = Dmi.ReadSystem(fs);
        Assert.Equal(("opensuse", "MACHINIST", "X99 PR9"), (system.ComputerName, system.Manufacturer, system.Model));
    }

    [Fact]
    public void A_board_whose_firmware_filled_nothing_in_is_unknown_field_by_field()
    {
        using var tree = new SpecsTree();
        tree.File("/sys/class/dmi/id/board_vendor", "To Be Filled By O.E.M.\n").File("/sys/class/dmi/id/board_name", "B450M\n");

        Assert.Equal(new BoardSpecs(null, "B450M", null, null, null), Dmi.ReadBoard(new SpecsRoot(tree.Root)));
    }

    // ---- CPU ----

    private static string Cpu(int processor, string package, string core, string model = "Intel(R) Xeon(R) CPU E5-2690 v4 @ 2.60GHz") =>
        $"processor\t: {processor}\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t\t: 79\nmodel name\t: {model}\ncpu MHz\t\t: 1197.090\ncache size\t: 35840 KB\nphysical id\t: {package}\nsiblings\t: 28\ncore id\t\t: {core}\ncpu cores\t: 14\nflags\t\t: fpu vme de\n\n";

    [Fact]
    public void Counts_cores_as_distinct_core_ids_so_smt_siblings_share_one()
    {
        // Logical CPUs 0 and 14 are the two threads of core 0 on this machine; CPU 1 is core 1.
        var info = ProcCpu.Parse(Cpu(0, "0", "0") + Cpu(1, "0", "1") + Cpu(14, "0", "0"));

        Assert.Equal("Intel(R) Xeon(R) CPU E5-2690 v4 @ 2.60GHz", info.Name);
        Assert.Equal("GenuineIntel", info.Vendor);
        Assert.Equal(3, info.Threads);
        Assert.Equal(2, info.Cores);
        Assert.Equal(1, info.Sockets);
    }

    [Fact]
    public void Two_sockets_with_the_same_core_ids_are_not_the_same_cores()
    {
        var info = ProcCpu.Parse(Cpu(0, "0", "0") + Cpu(1, "1", "0"));

        Assert.Equal(2, info.Cores);
        Assert.Equal(2, info.Sockets);
    }

    [Fact]
    public void An_arm_cpuinfo_without_core_ids_has_unknown_cores_and_one_socket()
    {
        var info = ProcCpu.Parse("processor\t: 0\nBogoMIPS\t: 108.00\nFeatures\t: fp asimd\nCPU implementer\t: 0x41\n\nprocessor\t: 1\nBogoMIPS\t: 108.00\n\nHardware\t: BCM2711\nModel\t\t: Raspberry Pi 4 Model B Rev 1.5\n");

        Assert.Null(info.Cores);
        Assert.Equal(2, info.Threads);
        Assert.Equal(1, info.Sockets);
        Assert.Equal("BCM2711", info.Name);
        Assert.Null(info.Vendor);
    }

    [Fact]
    public void The_processor_name_is_squeezed_of_repeated_spaces()
    {
        var info = ProcCpu.Parse(Cpu(0, "0", "0", "AMD Ryzen 5 5600   6-Core  Processor      "));

        Assert.Equal("AMD Ryzen 5 5600 6-Core Processor", info.Name);
    }

    [Fact]
    public void Empty_cpuinfo_is_all_unknown()
    {
        var info = ProcCpu.Parse("");

        Assert.Null(info.Name);
        Assert.Equal(0, info.Threads);
        Assert.Null(info.Cores);
        Assert.Equal(1, info.Sockets);
    }

    [Theory]
    [InlineData("32K", 32L * 1024)]
    [InlineData("256K", 256L * 1024)]
    [InlineData("35840K", 35840L * 1024)]
    [InlineData("1M", 1024L * 1024)]
    [InlineData("2G", 2048L * 1024 * 1024)]
    [InlineData("512", 512)]
    [InlineData("", 0)]
    [InlineData("K", 0)]
    [InlineData("12X", 0)]
    [InlineData("-4K", 0)]
    [InlineData("99999999999999999999K", 0)]
    public void Parses_sysfs_cache_sizes(string text, long bytes) => Assert.Equal(bytes, ProcCpu.ParseCacheSize(text));

    [Fact]
    public void Cache_totals_count_each_physical_cache_once_however_many_cpus_see_it()
    {
        // This machine: 14 cores x 2 threads. Each core's L1d/L1i/L2 is seen by its two threads; the L3 by all 28.
        var seen = new List<ProcCpu.CacheInstance>();
        for (int cpu = 0; cpu < 28; cpu++)
        {
            string core = $"{cpu % 14},{cpu % 14 + 14}";
            seen.Add(new ProcCpu.CacheInstance(1, "Data", core, 32 * 1024));
            seen.Add(new ProcCpu.CacheInstance(1, "Instruction", core, 32 * 1024));
            seen.Add(new ProcCpu.CacheInstance(2, "Unified", core, 256 * 1024));
            seen.Add(new ProcCpu.CacheInstance(3, "Unified", "0-27", 35840L * 1024));
        }

        (long l1, long l2, long l3) = ProcCpu.SumCaches(seen);

        Assert.Equal(14 * 64L * 1024, l1);       // lscpu: L1d 448 KiB + L1i 448 KiB.
        Assert.Equal(14 * 256L * 1024, l2);      // lscpu: L2 3.5 MiB.
        Assert.Equal(35840L * 1024, l3);         // lscpu: L3 35 MiB, 1 instance.
    }

    [Fact]
    public void A_cache_level_above_three_is_ignored()
    {
        (long l1, long l2, long l3) = ProcCpu.SumCaches([new ProcCpu.CacheInstance(4, "Unified", "0", 1 << 30)]);

        Assert.Equal((0L, 0L, 0L), (l1, l2, l3));
    }

    [Theory]
    [InlineData("Intel(R) Xeon(R) CPU E5-2690 v4 @ 2.60GHz", 2600)]
    [InlineData("Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz", 3200)]
    [InlineData("Intel(R) Pentium(R) CPU G620 @ 2.60 GHz", 2600)]
    [InlineData("Genuine Intel(R) CPU @ 800MHz", 800)]
    [InlineData("AMD Ryzen 5 5600 6-Core Processor", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Reads_the_rated_speed_out_of_the_processor_name(string? name, int? mhz) => Assert.Equal(mhz, ProcCpu.NominalMhz(name));

    private static SpecsTree CpuTree(string name = "Intel(R) Xeon(R) CPU E5-2690 v4 @ 2.60GHz")
    {
        var tree = new SpecsTree();
        tree.File("/proc/cpuinfo", Cpu(0, "0", "0", name) + Cpu(1, "0", "0", name));
        for (int cpu = 0; cpu < 2; cpu++)
        {
            string folder = $"/sys/devices/system/cpu/cpu{cpu}/cache";
            tree.File($"{folder}/index0/level", "1\n").File($"{folder}/index0/type", "Data\n").File($"{folder}/index0/size", "32K\n").File($"{folder}/index0/shared_cpu_list", "0-1\n")
                .File($"{folder}/index1/level", "1\n").File($"{folder}/index1/type", "Instruction\n").File($"{folder}/index1/size", "32K\n").File($"{folder}/index1/shared_cpu_list", "0-1\n")
                .File($"{folder}/index2/level", "2\n").File($"{folder}/index2/type", "Unified\n").File($"{folder}/index2/size", "256K\n").File($"{folder}/index2/shared_cpu_list", "0-1\n");
        }

        tree.Dir("/sys/devices/system/cpu/cpufreq").Dir("/sys/devices/system/cpu/cpuidle");
        return tree;
    }

    [Fact]
    public void The_processor_section_combines_cpuinfo_with_sysfs_caches()
    {
        using SpecsTree tree = CpuTree();
        tree.File("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq", "3500000\n");

        CpuSpecs cpu = ProcCpu.Read(new SpecsRoot(tree.Root));

        Assert.Equal((1, 2, 1), (cpu.Cores, cpu.Threads, cpu.Sockets));
        Assert.Equal((64L * 1024, 256L * 1024, 0L), (cpu.L1, cpu.L2, cpu.L3));
        Assert.Equal(2600, cpu.BaseMhz); // The name's "@ 2.60GHz" beats the boost clock cpufreq reports.
        Assert.Null(cpu.Socket);         // The socket designation is in SMBIOS, which needs root.
    }

    [Fact]
    public void Base_speed_prefers_the_driver_base_frequency_then_the_name_then_the_boost_clock()
    {
        using SpecsTree tree = CpuTree("AMD Ryzen 5 5600 6-Core Processor");
        tree.File("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq", "4468000\n");
        Assert.Equal(4468, ProcCpu.Read(new SpecsRoot(tree.Root)).BaseMhz);

        tree.File("/sys/devices/system/cpu/cpu0/cpufreq/base_frequency", "3500000\n");
        Assert.Equal(3500, ProcCpu.Read(new SpecsRoot(tree.Root)).BaseMhz);

        using SpecsTree bare = CpuTree("AMD Ryzen 5 5600 6-Core Processor");
        Assert.Null(ProcCpu.Read(new SpecsRoot(bare.Root)).BaseMhz);
    }

    [Fact]
    public void A_tree_with_no_cpuinfo_still_reports_the_thread_count_from_the_runtime()
    {
        using var tree = new SpecsTree();

        CpuSpecs cpu = ProcCpu.Read(new SpecsRoot(tree.Root));

        Assert.Equal("?", cpu.Name);
        Assert.Equal(Environment.ProcessorCount, cpu.Threads);
        Assert.Equal(cpu.Threads, cpu.Cores);
    }

    // ---- memory ----

    [Theory]
    [InlineData("MemTotal:       32681548 kB\nMemFree:          917844 kB\n", 32681548L * 1024)]
    [InlineData("MemFree: 1 kB\nMemTotal: 1024 kB\n", 1024L * 1024)]
    [InlineData("MemTotal: 2048\n", 2048)]
    [InlineData("MemTotalX: 5 kB\n", 0)]
    [InlineData("MemTotal: lots kB\n", 0)]
    [InlineData("MemTotal: 99999999999999999 kB\n", 0)]
    [InlineData("", 0)]
    public void Reads_total_memory_from_meminfo(string text, long bytes) => Assert.Equal(bytes, MemorySection.ParseTotal(text));

    [Fact]
    public void Memory_slots_and_modules_stay_unknown_because_they_need_root()
    {
        using var tree = new SpecsTree();
        tree.File("/proc/meminfo", "MemTotal:       32681548 kB\n");

        MemorySpecs memory = MemorySection.Read(new SpecsRoot(tree.Root));

        Assert.Equal(32681548L * 1024, memory.Total);
        Assert.Null(memory.Slots);
        Assert.Empty(memory.Modules);
    }

    // ---- power ----

    private static PowerSupply.Supply Battery(string? status, int? capacity, string? scope = null, bool? present = null) =>
        new("Battery", scope, status, capacity, null, present);

    private static PowerSupply.Supply Mains(bool online, string type = "Mains") => new(type, null, null, null, online, null);

    [Fact]
    public void A_desktop_without_a_battery_has_no_battery()
    {
        Assert.Null(PowerSupply.Combine([Mains(true)]));
        Assert.Null(PowerSupply.Combine([]));
    }

    [Fact]
    public void A_laptop_on_battery()
    {
        Assert.Equal(new BatterySpecs(57, false, false), PowerSupply.Combine([Battery("Discharging", 57), Mains(false)]));
    }

    [Fact]
    public void A_laptop_charging_is_on_ac_and_one_that_is_full_on_the_charger_is_not_charging()
    {
        Assert.Equal(new BatterySpecs(80, true, true), PowerSupply.Combine([Battery("Charging", 80), Mains(true)]));
        Assert.Equal(new BatterySpecs(100, false, true), PowerSupply.Combine([Battery("Full", 100), Mains(true)]));
        Assert.Equal(new BatterySpecs(78, false, true), PowerSupply.Combine([Battery("Not charging", 78), Mains(true)])); // Charge limit reached.
    }

    [Fact]
    public void Without_a_mains_device_a_battery_that_charges_or_is_full_still_proves_the_charger()
    {
        Assert.Equal(new BatterySpecs(40, true, true), PowerSupply.Combine([Battery("Charging", 40)]));
        Assert.Equal(new BatterySpecs(100, false, true), PowerSupply.Combine([Battery("Full", 100)]));
        Assert.Equal(new BatterySpecs(40, false, false), PowerSupply.Combine([Battery("Discharging", 40)]));
    }

    [Fact]
    public void A_usb_c_supply_that_is_online_counts_as_the_charger()
    {
        Assert.Equal(new BatterySpecs(90, false, true), PowerSupply.Combine([Battery("Discharging", 90), Mains(true, "USB")]));
    }

    [Fact]
    public void A_wireless_mouses_battery_is_not_the_machines()
    {
        Assert.Null(PowerSupply.Combine([Battery("Discharging", 30, scope: "Device"), Mains(true)]));
        Assert.Equal(new BatterySpecs(60, false, false), PowerSupply.Combine([Battery("Discharging", 60, scope: "System"), Battery("Discharging", 30, scope: "Device"), Mains(false)]));
    }

    [Fact]
    public void Two_batteries_average_and_a_missing_one_is_ignored()
    {
        Assert.Equal(new BatterySpecs(51, false, false), PowerSupply.Combine([Battery("Discharging", 80), Battery("Discharging", 21), Mains(false)])); // 50.5 rounds up.
        Assert.Equal(new BatterySpecs(80, false, false), PowerSupply.Combine([Battery("Discharging", 80), Battery("Unknown", null, present: false), Mains(false)]));
    }

    [Fact]
    public void A_battery_that_reports_no_capacity_still_exists_with_an_unknown_level()
    {
        Assert.Equal(new BatterySpecs(null, true, true), PowerSupply.Combine([Battery("Charging", null), Mains(true)]));
    }

    [Fact]
    public void Reads_supplies_from_a_power_supply_tree()
    {
        using var tree = new SpecsTree();
        tree.File("/sys/class/power_supply/BAT0/type", "Battery\n").File("/sys/class/power_supply/BAT0/status", "Discharging\n")
            .File("/sys/class/power_supply/BAT0/capacity", "57\n").File("/sys/class/power_supply/BAT0/present", "1\n")
            .File("/sys/class/power_supply/AC/type", "Mains\n").File("/sys/class/power_supply/AC/online", "0\n")
            .File("/sys/class/power_supply/hidpp_battery_0/type", "Battery\n").File("/sys/class/power_supply/hidpp_battery_0/scope", "Device\n")
            .File("/sys/class/power_supply/hidpp_battery_0/capacity", "10\n").File("/sys/class/power_supply/hidpp_battery_0/status", "Discharging\n");

        Assert.Equal(new BatterySpecs(57, false, false), PowerSupply.Read(new SpecsRoot(tree.Root)));
    }

    // ---- networks ----

    [Theory]
    [InlineData("enp10s0", true, false, 1L, "Ethernet")]
    [InlineData("wlp3s0", true, true, 1L, "Wi-Fi")]
    [InlineData("eth0", true, false, 1L, "Ethernet")]
    [InlineData("enx001122334455", true, false, 1L, "Ethernet")]
    [InlineData("lo", true, false, 772L, null)]
    [InlineData("lo", true, false, 1L, null)]
    [InlineData("docker0", false, false, 1L, null)]
    [InlineData("veth3f2a1b", false, false, 1L, null)]
    [InlineData("virbr0", false, false, 1L, null)]
    [InlineData("br-8a7d3c", false, false, 1L, null)]
    [InlineData("tun0", false, false, 65534L, null)]
    [InlineData("tap0", false, false, 1L, null)]
    [InlineData("wg0", false, false, 65534L, null)]
    [InlineData("podman0", false, false, 1L, null)]
    [InlineData("lxcbr0", false, false, 1L, null)]
    [InlineData("tailscale0", false, false, 65534L, null)]
    [InlineData("br0", false, false, 1L, null)]
    [InlineData("docker0", true, false, 1L, null)]    // Even a "device" would not make a docker bridge real.
    [InlineData("eth1", true, false, 24L, null)]      // Not an Ethernet-framed link (e.g. an IP-over-IB or a modem's raw-IP interface).
    [InlineData("eth2", true, false, null, null)]
    public void Keeps_only_physical_ethernet_framed_adapters(string name, bool hasDevice, bool wireless, long? type, string? kind) =>
        Assert.Equal(kind, NetAdapters.Classify(name, hasDevice, wireless, type));

    [Theory]
    [InlineData(1000L, 1_000_000_000L)]
    [InlineData(100L, 100_000_000L)]
    [InlineData(-1L, 0L)]   // Wi-Fi and links that are down.
    [InlineData(0L, 0L)]
    [InlineData(null, 0L)]
    public void Link_speed_is_megabits_or_unknown(long? megabits, long bps) => Assert.Equal(bps, NetAdapters.SpeedBps(megabits));

    [Theory]
    [InlineData("00:e0:24:2f:2a:78", "00:E0:24:2F:2A:78")]
    [InlineData("00:00:00:00:00:00", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Formats_the_mac_like_the_windows_reader_and_drops_the_all_zero_address(string? address, string? expected) =>
        Assert.Equal(expected, NetAdapters.FormatMac(address));

    private static void Interface(SpecsTree tree, string name, string type, string operstate, string? address, bool device = false, string speed = "1000\n", bool wireless = false)
    {
        string folder = $"/sys/class/net/{name}";
        tree.File($"{folder}/type", type + "\n").File($"{folder}/operstate", operstate + "\n");
        if (address is not null)
        {
            tree.File($"{folder}/address", address + "\n");
        }

        if (device)
        {
            // In sysfs /sys/class/net/<if> is itself a link into the device tree; here it is a folder, so the link climbs to /sys.
            tree.Dir("/sys/devices/pci0000:00/0000:00:1c.2/0000:0a:00.0").Link($"{folder}/device", "../../../devices/pci0000:00/0000:00:1c.2/0000:0a:00.0");
        }

        if (wireless)
        {
            tree.Dir($"{folder}/wireless");
        }

        tree.File($"{folder}/speed", speed);
    }

    private static SpecsTree Network()
    {
        var tree = new SpecsTree();
        Interface(tree, "enp10s0", "1", "up", "00:e0:24:2f:2a:78", device: true);
        Interface(tree, "lo", "772", "unknown", "00:00:00:00:00:00");
        Interface(tree, "docker0", "1", "down", "1a:85:fd:73:51:03", speed: "-1\n");
        Interface(tree, "veth9c1d2e", "1", "up", "ba:3b:aa:11:22:33");
        Interface(tree, "tailscale0", "65534", "unknown", null);
        Interface(tree, "wlp4s0", "1", "up", "a4:6b:b6:00:11:22", device: true, speed: "-1\n", wireless: true);
        Interface(tree, "enp11s0", "1", "down", "00:e0:24:2f:2a:79", device: true);
        return tree;
    }

    [Fact]
    public void Lists_only_connected_hardware_adapters_with_an_ipv4_and_no_container_interface()
    {
        using SpecsTree tree = Network();
        var addresses = new Dictionary<string, string>
        {
            ["enp10s0"] = "192.168.3.15", ["lo"] = "127.0.0.1", ["docker0"] = "172.17.0.1", ["veth9c1d2e"] = "10.0.0.2",
            ["tailscale0"] = "100.67.155.99", ["wlp4s0"] = "192.168.3.20", ["enp11s0"] = "192.168.3.16",
        };
        var pci = PciNames.Parse("0a:00.0 \"Ethernet controller\" \"Realtek Semiconductor Co., Ltd.\" \"RTL8111/8168 Gigabit\" -r15");

        IReadOnlyList<NetworkSpecs> networks = NetAdapters.Read(new SpecsRoot(tree.Root), name => addresses.GetValueOrDefault(name), () => pci);

        Assert.Equal(["enp10s0", "wlp4s0"], networks.Select(n => n.Name));
        NetworkSpecs wired = networks[0];
        Assert.Equal("Ethernet", wired.Kind);
        Assert.Equal(1_000_000_000L, wired.SpeedBps);
        Assert.Equal("00:E0:24:2F:2A:78", wired.Mac);
        Assert.Equal("192.168.3.15", wired.IPv4);
        Assert.Equal("Realtek RTL8111/8168 Gigabit", wired.Description);
        NetworkSpecs wireless = networks[1];
        Assert.Equal(("Wi-Fi", 0L), (wireless.Kind, wireless.SpeedBps));
    }

    [Fact]
    public void An_adapter_without_an_ipv4_address_is_not_connected()
    {
        using SpecsTree tree = Network();

        Assert.Empty(NetAdapters.Read(new SpecsRoot(tree.Root), _ => null, () => PciNames.Parse(null)));
    }

    [Fact]
    public void Without_lspci_the_description_is_the_kernel_driver_then_the_usb_product_then_the_interface_name()
    {
        using var tree = new SpecsTree();
        Interface(tree, "enp10s0", "1", "up", "00:e0:24:2f:2a:78", device: true);
        tree.Dir("/sys/bus/pci/drivers/r8169").Link("/sys/devices/pci0000:00/0000:00:1c.2/0000:0a:00.0/driver", "../../../../bus/pci/drivers/r8169");
        // A card with no PCI name, no driver link and no USB parent: all that is left is the interface name.
        tree.File("/sys/class/net/eth9/type", "1\n").File("/sys/class/net/eth9/operstate", "up\n").Dir("/sys/devices/platform/eth9dev").Link("/sys/class/net/eth9/device", "../../../devices/platform/eth9dev");

        var networks = NetAdapters.Read(new SpecsRoot(tree.Root), _ => "10.0.0.5", () => PciNames.Parse(null));

        Assert.Equal("r8169", networks.Single(n => n.Name == "enp10s0").Description);
        Assert.Equal("eth9", networks.Single(n => n.Name == "eth9").Description);
        Assert.Null(networks.Single(n => n.Name == "eth9").Mac);
    }

    [Fact]
    public void A_usb_adapter_is_described_by_its_usb_product_string()
    {
        using var tree = new SpecsTree();
        tree.File("/sys/devices/usb1/1-2/product", "USB 10/100/1000 LAN\n").Dir("/sys/devices/usb1/1-2/1-2:1.0")
            .File("/sys/class/net/enx001122334455/type", "1\n").File("/sys/class/net/enx001122334455/operstate", "up\n")
            .Link("/sys/class/net/enx001122334455/device", "../../../devices/usb1/1-2/1-2:1.0");

        var network = Assert.Single(NetAdapters.Read(new SpecsRoot(tree.Root), _ => "10.0.0.5", () => PciNames.Parse(null)));

        Assert.Equal("USB 10/100/1000 LAN", network.Description);
    }

    // ---- the reader ----

    [Fact]
    public void A_machine_with_nothing_readable_gives_a_complete_all_unknown_report()
    {
        using var tree = new SpecsTree();

        PcSpecs specs = new LinuxSpecsReader(tree.Root, NoTools, _ => null).Read();

        Assert.Equal("Linux", specs.Os.Name);
        Assert.Equal("?", specs.Cpu.Name);
        Assert.Equal(0, specs.Memory.Total);
        Assert.Empty(specs.Gpus);
        Assert.Empty(specs.Displays);
        Assert.Empty(specs.Disks);
        Assert.Empty(specs.Networks);
        Assert.Null(specs.Battery);
        Assert.Equal(new BoardSpecs(null, null, null, null, null), specs.Board);
        Assert.Null(specs.System.Manufacturer);
    }

    [Fact]
    public void One_unreadable_source_empties_only_its_own_field()
    {
        using var tree = new SpecsTree();
        tree.File("/etc/os-release", TumbleweedOsRelease).File("/proc/meminfo", "MemTotal: 32681548 kB\n")
            .File("/sys/class/dmi/id/board_name", "X99 PR9\n").File("/proc/cpuinfo", Cpu(0, "0", "0"));
        var reader = new LinuxSpecsReader(tree.Root, NoTools, _ => null);
        PcSpecs complete = reader.Read();

        System.IO.File.Delete(tree.Full("/proc/cpuinfo")); // The CPU source goes away; nothing else changes.
        PcSpecs without = reader.Read();

        Assert.Equal("Intel(R) Xeon(R) CPU E5-2690 v4 @ 2.60GHz", complete.Cpu.Name);
        Assert.Equal("?", without.Cpu.Name);
        Assert.Equal(complete.Os.Name, without.Os.Name);
        Assert.Equal(complete.Board, without.Board);
        Assert.Equal(32681548L * 1024, without.Memory.Total);
        Assert.Equal("X99 PR9", without.Board.Product);
    }

    [Fact]
    public void A_helper_program_that_blows_up_costs_only_the_section_that_asked_for_it()
    {
        using var tree = new SpecsTree();
        tree.File("/proc/meminfo", "MemTotal: 1024 kB\n").File("/sys/class/drm/card0/device/vendor", "0x8086\n").File("/sys/class/drm/card0/device/device", "0xe20b\n");

        PcSpecs specs = new LinuxSpecsReader(tree.Root, (file, _) => file == "vulkaninfo" ? throw new InvalidOperationException("driver crashed") : null, _ => null).Read();

        Assert.Empty(specs.Gpus);
        Assert.Equal(1024 * 1024, specs.Memory.Total);
    }

    [Fact]
    public void Assembles_every_section_from_one_tree()
    {
        using SpecsTree tree = CpuTree();
        tree.File("/etc/os-release", TumbleweedOsRelease).File("/proc/meminfo", "MemTotal: 32681548 kB\n").File("/proc/sys/kernel/osrelease", "7.2.11-1-default\n")
            .File("/sys/class/dmi/id/sys_vendor", "MACHINIST\n").File("/sys/class/dmi/id/product_name", "X99 PR9\n")
            .File("/sys/block/sda/size", "976773168\n").Dir("/sys/block/sda/device").File("/sys/block/sda/device/model", "WDC WD5000AZLX-0\n")
            .File("/sys/class/power_supply/BAT0/type", "Battery\n").File("/sys/class/power_supply/BAT0/capacity", "88\n").File("/sys/class/power_supply/BAT0/status", "Discharging\n");
        Interface(tree, "enp10s0", "1", "up", "00:e0:24:2f:2a:78", device: true);

        PcSpecs specs = new LinuxSpecsReader(tree.Root, NoTools, name => name == "enp10s0" ? "192.168.3.15" : null).Read();

        Assert.Equal("MACHINIST X99 PR9", $"{specs.System.Manufacturer} {specs.System.Model}");
        Assert.Equal("openSUSE Tumbleweed", specs.Os.Name);
        Assert.Equal(2, specs.Cpu.Threads);
        Assert.Equal(32681548L * 1024, specs.Memory.Total);
        Assert.Equal("WDC WD5000AZLX-0", Assert.Single(specs.Disks).Model);
        Assert.Equal(88, specs.Battery?.Percent);
        Assert.Equal("192.168.3.15", Assert.Single(specs.Networks).IPv4);
    }

    // ---- this machine ----

    [LinuxFact]
    public void Reads_this_machine_without_throwing_and_with_every_field_populated_or_explicitly_unknown()
    {
        PcSpecs specs = new LinuxSpecsReader().Read();

        Assert.False(string.IsNullOrWhiteSpace(specs.System.ComputerName));
        Assert.False(string.IsNullOrWhiteSpace(specs.Os.Name));
        Assert.NotEqual("?", specs.Os.Build);
        Assert.True(specs.Os.Uptime > TimeSpan.Zero);
        Assert.False(string.IsNullOrWhiteSpace(specs.Cpu.Name));
        Assert.True(specs.Cpu.Threads > 0);
        Assert.InRange(specs.Cpu.Cores, 1, specs.Cpu.Threads);
        Assert.True(specs.Cpu.Sockets >= 1);
        Assert.True(specs.Memory.Total > 0);
        Assert.All(specs.Disks, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Model));
            Assert.True(d.Size > 0);
            Assert.False(string.IsNullOrWhiteSpace(d.Bus));
            Assert.DoesNotMatch(@"^(loop|ram|zram|dm-|md|sr)", d.Model);
        });
        Assert.Equal(specs.Disks.Count, specs.Disks.Select(d => d.Number).Distinct().Count());
        Assert.All(specs.Gpus, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
        Assert.All(specs.Displays, d => Assert.True(d.Width > 0 && d.Height > 0));
    }

    [LinuxFact]
    public void No_container_or_virtual_interface_reaches_the_list_on_this_machine()
    {
        PcSpecs specs = new LinuxSpecsReader().Read();

        Assert.All(specs.Networks, n =>
        {
            Assert.DoesNotMatch(@"^(lo$|docker|veth|virbr|br-|tun|tap|wg|podman|lxc)", n.Name);
            Assert.NotNull(n.IPv4);
            Assert.Contains(n.Kind, new[] { "Ethernet", "Wi-Fi" });
        });
    }
}
