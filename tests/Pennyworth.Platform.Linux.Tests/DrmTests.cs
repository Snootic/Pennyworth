using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux.Tests;

public class DrmTests
{
    private const string Lspci = """
        00:00.0 "Host bridge" "Intel Corporation" "Xeon E7 v4/Xeon E5 v4/Xeon E3 v4/Xeon D DMI2" -r01 -p00 "Intel Corporation" "Device 0000"
        05:00.0 "VGA compatible controller" "Intel Corporation" "Battlemage G21 [Arc B580]" -p00 "Intel Corporation" "Device 1100"
        0a:00.0 "Ethernet controller" "Realtek Semiconductor Co., Ltd." "RTL8111/8168/8211/8411 PCI Express Gigabit Ethernet Controller" -r15 -p00 "Realtek Semiconductor Co., Ltd." "Device 0123"
        01:00.0 "VGA compatible controller" "NVIDIA Corporation" "GA106 [GeForce RTX 3060 Lite Hash Rate]" -ra1 -p00 "Micro-Star International Co., Ltd. [MSI]" "Device 3980"
        0b:00.0 "VGA compatible controller" "Advanced Micro Devices, Inc. [AMD/ATI]" "Navi 23 [Radeon RX 6600/6600 XT/6600M]" -rc1 -p00 "Sapphire Technology Limited" "Device 4318"
        """;

    /// <summary>The device-local heap of the B580 and the software renderer, from this machine's <c>vulkaninfo</c> (trimmed).</summary>
    private const string Vulkaninfo = """
        Device Properties and Extensions:
        =================================
        GPU0:
        VkPhysicalDeviceProperties:
        ---------------------------
        	apiVersion        = 1.4.354 (4210018)
        	driverVersion     = 26.2.5 (109051909)
        	vendorID          = 0x8086
        	deviceID          = 0xe20b
        	deviceType        = PHYSICAL_DEVICE_TYPE_DISCRETE_GPU
        	deviceName        = Intel(R) Arc(tm) B580 Graphics (BMG G21)
        VkPhysicalDeviceMemoryProperties:
        =================================
        	memoryHeaps: count = 2
        	memoryHeaps[0]:
        		size   = 12809404416 (0x2fb800000) (11.93 GiB)
        		budget = 9672065024 (0x240800000) (9.01 GiB)
        		usage  = 0 (0x00000000) (0.00 B)
        		flags: count = 1
        			MEMORY_HEAP_DEVICE_LOCAL_BIT
        	memoryHeaps[1]:
        		size   = 25099428864 (0x5d80ae400) (23.38 GiB)
        		budget = 17402167296 (0x40d400000) (16.21 GiB)
        		usage  = 0 (0x00000000) (0.00 B)
        		flags:
        			None
        GPU1:
        VkPhysicalDeviceProperties:
        ---------------------------
        	vendorID          = 0x10005
        	deviceID          = 0x0000
        	deviceType        = PHYSICAL_DEVICE_TYPE_CPU
        	deviceName        = llvmpipe (LLVM 23.1.3, 256 bits)
        VkPhysicalDeviceMemoryProperties:
        =================================
        	memoryHeaps: count = 1
        	memoryHeaps[0]:
        		size   = 33465905152 (0x7cab93000) (31.17 GiB)
        		flags: count = 1
        			MEMORY_HEAP_DEVICE_LOCAL_BIT
        """;

    private static readonly Func<string, IReadOnlyList<string>, string?> NoTools = (_, _) => null;

    private static Func<string, IReadOnlyList<string>, string?> Tools(string? lspci = Lspci, string? nvidia = null, string? vulkan = null) =>
        (file, _) => file switch { "lspci" => lspci, "nvidia-smi" => nvidia, "vulkaninfo" => vulkan, _ => null };

    private static SpecsTree Card(SpecsTree tree, int index, string vendor, string device, string slot, string driver, params (string Name, string Value)[] extra)
    {
        string folder = $"/sys/class/drm/card{index}/device";
        tree.File($"{folder}/vendor", vendor + "\n").File($"{folder}/device", device + "\n")
            .File($"{folder}/uevent", $"DRIVER={driver}\nPCI_SLOT_NAME={slot}\n")
            .Dir($"/sys/bus/pci/drivers/{driver}").Link($"{folder}/driver", $"../../../../bus/pci/drivers/{driver}");
        foreach ((string name, string value) in extra)
        {
            tree.File($"{folder}/{name}", value);
        }

        return tree;
    }

    [Fact]
    public void Parses_lspci_machine_output_keyed_by_the_slot_sysfs_uses()
    {
        IReadOnlyDictionary<string, PciDevice> devices = PciNames.Parse(Lspci);

        Assert.Equal(5, devices.Count);
        Assert.Equal("Battlemage G21 [Arc B580]", devices["0000:05:00.0"].Device);
        Assert.Equal("VGA compatible controller", devices["0000:05:00.0"].Class);
    }

    [Theory]
    [InlineData("0000:05:00.0", "Intel Arc B580")]
    [InlineData("0000:01:00.0", "NVIDIA GeForce RTX 3060 Lite Hash Rate")]
    [InlineData("0000:0b:00.0", "AMD Radeon RX 6600/6600 XT/6600M")]
    [InlineData("0000:00:00.0", "Intel Xeon E7 v4/Xeon E5 v4/Xeon E3 v4/Xeon D DMI2")]
    public void Names_a_graphics_card_the_way_people_say_it(string slot, string expected) =>
        Assert.Equal(expected, PciNames.Parse(Lspci)[slot].GraphicsName);

    [Fact]
    public void A_network_card_keeps_its_full_device_text_with_a_short_vendor()
    {
        Assert.Equal(
            "Realtek RTL8111/8168/8211/8411 PCI Express Gigabit Ethernet Controller",
            PciNames.Parse(Lspci)["0000:0a:00.0"].FullName);
    }

    [Fact]
    public void Quoted_fields_may_contain_escaped_quotes()
    {
        var devices = PciNames.Parse("""03:00.0 "Display controller" "Acme" "Model \"X\" [Pro]" -p00""");

        Assert.Equal("Model \"X\" [Pro]", devices["0000:03:00.0"].Device);
        Assert.Equal("Acme Pro", devices["0000:03:00.0"].GraphicsName);
    }

    [Theory]
    [InlineData("05:00.0", "0000:05:00.0")]
    [InlineData("00000000:01:00.0", "0000:01:00.0")] // nvidia-smi
    [InlineData("0001:03:00.0", "0001:03:00.0")]
    [InlineData("0000:0A:00.0", "0000:0a:00.0")]
    public void Normalizes_a_pci_slot_to_sysfs_spelling(string slot, string expected) =>
        Assert.Equal(expected, PciNames.NormalizeSlot(slot));

    [Fact]
    public void Reads_nvidia_smi_csv_and_converts_mebibytes()
    {
        IReadOnlyList<NvidiaGpu> gpus = GpuTools.ParseNvidiaSmi("00000000:01:00.0, 12288, 550.54.14\n00000000:02:00.0, [N/A], [N/A]\n\n");

        Assert.Equal(new NvidiaGpu("0000:01:00.0", 12288L * 1024 * 1024, "550.54.14"), gpus[0]);
        Assert.Equal(new NvidiaGpu("0000:02:00.0", null, null), gpus[1]);
        Assert.Equal(2, gpus.Count);
    }

    [Fact]
    public void Vulkan_gives_the_largest_device_local_heap_per_gpu_and_marks_the_discrete_one()
    {
        IReadOnlyList<VulkanGpu> gpus = GpuTools.ParseVulkanInfo(Vulkaninfo);

        Assert.Equal(2, gpus.Count);
        Assert.Equal(new VulkanGpu(0x8086, 0xE20B, true, 12809404416), gpus[0]); // The 23 GB heap is host memory, not device-local.
        Assert.Equal(new VulkanGpu(0x10005, 0, false, 33465905152), gpus[1]);
    }

    [Fact]
    public void An_intel_card_gets_its_name_from_lspci_and_its_memory_from_vulkan()
    {
        using var tree = new SpecsTree();
        Card(tree, 0, "0x8086", "0xe20b", "0000:05:00.0", "xe", ("boot_vga", "1\n"));
        tree.File("/proc/sys/kernel/osrelease", "7.2.11-1-default\n");

        DrmCard card = Assert.Single(DrmGpu.Read(new SpecsRoot(tree.Root), () => PciNames.Parse(Lspci), Tools(vulkan: Vulkaninfo)));

        Assert.Equal(0, card.Index);
        Assert.True(card.IsBootDisplay);
        Assert.Equal("Intel Arc B580", card.Gpu.Name);
        Assert.Equal(12809404416, card.Gpu.Memory);
        Assert.Equal("xe (kernel 7.2.11-1-default)", card.Gpu.DriverVersion);
    }

    [Fact]
    public void An_integrated_gpus_vulkan_heap_is_shared_system_memory_and_is_not_its_vram()
    {
        using var tree = new SpecsTree();
        Card(tree, 0, "0x8086", "0x9bc5", "0000:00:02.0", "i915");
        string integrated = Vulkaninfo.Replace("PHYSICAL_DEVICE_TYPE_DISCRETE_GPU", "PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU").Replace("0xe20b", "0x9bc5");

        DrmCard card = Assert.Single(DrmGpu.Read(new SpecsRoot(tree.Root), () => PciNames.Parse(null), Tools(vulkan: integrated)));

        Assert.Null(card.Gpu.Memory);
    }

    [Fact]
    public void Without_lspci_the_name_falls_back_to_the_vendor_and_the_ids_and_memory_is_unknown_not_guessed()
    {
        using var tree = new SpecsTree();
        Card(tree, 0, "0x8086", "0xe20b", "0000:05:00.0", "xe");

        DrmCard card = Assert.Single(DrmGpu.Read(new SpecsRoot(tree.Root), () => PciNames.Parse(null), NoTools));

        Assert.Equal("Intel graphics [8086:e20b]", card.Gpu.Name);
        Assert.Null(card.Gpu.Memory);
    }

    [Fact]
    public void An_amd_card_reports_its_vram_from_sysfs_and_runs_no_helper()
    {
        using var tree = new SpecsTree();
        Card(tree, 0, "0x1002", "0x73ff", "0000:0b:00.0", "amdgpu", ("mem_info_vram_total", "8573157376\n"));
        var asked = new List<string>();

        DrmCard card = Assert.Single(DrmGpu.Read(new SpecsRoot(tree.Root), () => PciNames.Parse(Lspci), (file, _) =>
        {
            asked.Add(file);
            return null;
        }));

        Assert.Equal(8573157376, card.Gpu.Memory);
        Assert.Empty(asked);
    }

    [Fact]
    public void An_nvidia_card_takes_memory_and_driver_from_nvidia_smi_matched_by_slot()
    {
        using var tree = new SpecsTree();
        Card(tree, 0, "0x10de", "0x2504", "0000:01:00.0", "nvidia");

        DrmCard card = Assert.Single(DrmGpu.Read(new SpecsRoot(tree.Root), () => PciNames.Parse(Lspci), Tools(nvidia: "00000000:01:00.0, 12288, 550.54.14\n")));

        Assert.Equal(12288L * 1024 * 1024, card.Gpu.Memory);
        Assert.Equal("nvidia 550.54.14", card.Gpu.DriverVersion);
        Assert.Equal("NVIDIA GeForce RTX 3060 Lite Hash Rate", card.Gpu.Name);
    }

    [Fact]
    public void A_driver_that_publishes_a_module_version_wins_over_the_kernel_release()
    {
        using var tree = new SpecsTree();
        Card(tree, 0, "0x10de", "0x2504", "0000:01:00.0", "nvidia");
        tree.File("/sys/module/nvidia/version", "560.35.03\n").File("/proc/sys/kernel/osrelease", "6.8.0\n");

        DrmCard card = Assert.Single(DrmGpu.Read(new SpecsRoot(tree.Root), () => PciNames.Parse(Lspci), NoTools));

        Assert.Equal("nvidia 560.35.03", card.Gpu.DriverVersion);
    }

    [Fact]
    public void The_boot_display_is_listed_first_and_a_card_seen_twice_only_once()
    {
        using var tree = new SpecsTree();
        Card(tree, 0, "0x10de", "0x2504", "0000:01:00.0", "nvidia");
        Card(tree, 1, "0x8086", "0x9bc5", "0000:00:02.0", "i915", ("boot_vga", "1\n"));
        Card(tree, 2, "0x8086", "0x9bc5", "0000:00:02.0", "i915"); // Same PCI slot: a second DRM node of the same device.
        tree.Dir("/sys/class/drm/renderD128").Dir("/sys/class/drm/card1-eDP-1");

        IReadOnlyList<DrmCard> cards = DrmGpu.Read(new SpecsRoot(tree.Root), () => PciNames.Parse(Lspci), NoTools);

        Assert.Equal([1, 0], cards.Select(c => c.Index));
    }

    [Fact]
    public void Builds_a_display_from_the_edid_and_the_first_mode_of_the_connector()
    {
        Edid edid = Edid.Parse(Convert.FromHexString(
            "00ffffffffffff004c2d7d70000000001622010380351e782adfd5a35b4da125" +
            "0d5054bfef8081c0810081809500a9c0b300714f0101023a801871382d40582c" +
            "450010292100001e000000fd00304b1e5412000a202020202020000000fc004c" +
            "4632345433350a2020202020000000ff00534e30303030303030300a2020015f"))!;

        DisplaySpecs? display = DrmDisplay.Build("HDMI-A-3", "Intel Arc B580", edid, "1920x1080\n1920x1080\n1680x1050\n", isPrimary: true);

        Assert.Equal(new DisplaySpecs("Samsung LF24T35", "Intel Arc B580", 1920, 1080, 60, true), display);
    }

    [Fact]
    public void A_mode_that_is_not_the_preferred_one_takes_the_best_refresh_the_edid_lists_for_it()
    {
        var edid = new Edid("DEL", 1, "U2720Q", 60, 34, new EdidTiming(3840, 2160, 60), [new EdidTiming(3840, 2160, 60), new EdidTiming(1920, 1080, 59.94), new EdidTiming(1920, 1080, 120)]);

        DisplaySpecs? display = DrmDisplay.Build("DP-1", "GPU", edid, "1920x1080", isPrimary: false);

        Assert.Equal(new DisplaySpecs("Dell U2720Q", "GPU", 1920, 1080, 120, false), display);
    }

    [Fact]
    public void Without_modes_the_edids_native_timing_is_used()
    {
        var edid = new Edid("LGD", 1, null, null, null, new EdidTiming(2560, 1600, 165.004), [new EdidTiming(2560, 1600, 165.004)]);

        DisplaySpecs? display = DrmDisplay.Build("eDP-1", "GPU", edid, null, isPrimary: true);

        Assert.Equal(new DisplaySpecs("LG Display display", "GPU", 2560, 1600, 165, true), display);
    }

    [Fact]
    public void A_connector_without_an_edid_is_named_after_the_connector_and_its_refresh_is_unknown()
    {
        DisplaySpecs? display = DrmDisplay.Build("VGA-1", "GPU", null, "1024x768i\n", isPrimary: true);

        Assert.Equal(new DisplaySpecs("VGA-1", "GPU", 1024, 768, 0, true), display);
    }

    [Fact]
    public void A_connected_connector_with_no_resolution_anywhere_is_not_a_display() =>
        Assert.Null(DrmDisplay.Build("DP-2", "GPU", null, "", isPrimary: false));

    [Fact]
    public void A_model_that_already_starts_with_the_maker_is_not_prefixed_twice()
    {
        var edid = new Edid("DEL", 1, "DELL U2720Q", null, null, null, [new EdidTiming(1920, 1080, 60)]);

        Assert.Equal("DELL U2720Q", DrmDisplay.Build("DP-1", "GPU", edid, "1920x1080", true)!.Name);
    }

    [Fact]
    public void Lists_only_connected_connectors_with_their_gpu_and_the_first_as_main()
    {
        using var tree = new SpecsTree();
        tree.File("/sys/class/drm/card0-DP-1/status", "disconnected\n")
            .File("/sys/class/drm/card0-HDMI-A-3/status", "connected\n")
            .File("/sys/class/drm/card0-HDMI-A-3/modes", "1920x1080\n1280x720\n")
            .File("/sys/class/drm/card0-HDMI-A-4/status", "connected\n")
            .File("/sys/class/drm/card0-HDMI-A-4/modes", "1366x768\n")
            .File("/sys/class/drm/version", "drm 1.1.0\n");
        var cards = new[] { new DrmCard(0, true, new GpuSpecs("Intel Arc B580", null, null, null)) };

        IReadOnlyList<DisplaySpecs> displays = DrmDisplay.Read(new SpecsRoot(tree.Root), cards);

        Assert.Equal(["HDMI-A-3", "HDMI-A-4"], displays.Select(d => d.Name));
        Assert.Equal([true, false], displays.Select(d => d.IsPrimary));
        Assert.All(displays, d => Assert.Equal("Intel Arc B580", d.Adapter));
    }
}
