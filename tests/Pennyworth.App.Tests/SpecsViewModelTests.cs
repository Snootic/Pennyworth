using Pennyworth.Assistant;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.Monitoring.Hardware;
using static Pennyworth.App.Tests.SampleTrees;

namespace Pennyworth.App.Tests;

public class SpecsViewModelTests
{
    internal static PcSpecs SamplePc(BatterySpecs? battery = null) => new(
        new SystemSpecs("DESKTOP-1", "ana", "PICHAU", "PCH-B550M-CR"),
        new OsSpecs("Windows 11 Pro", "25H2", "26200.9711", "X64", new DateTimeOffset(2025, 9, 13, 10, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(26.5)),
        new CpuSpecs("AMD Ryzen 5 5600 6-Core Processor", "AuthenticAMD", 6, 12, 1, 3493, 384 * 1024, 3 * 1024 * 1024, 32 * 1024 * 1024, "X64", "AM4"),
        new MemorySpecs(16 * Gb, 4,
        [
            new MemoryModule("DIMM 0", 8 * Gb, "DDR4", 2667, 2400, null, null, "DIMM"),
            new MemoryModule("DIMM 1", 8 * Gb, "DDR4", 2400, 2400, "Kingston", "KF3200", "DIMM"),
        ]),
        [new GpuSpecs("NVIDIA GeForce RTX 5060", 8 * Gb, "32.0.15.9649", "5-5-2026")],
        [new DisplaySpecs("Monitor", "RTX", 1366, 768, 60, false), new DisplaySpecs("Monitor", "RTX", 2560, 1440, 165, true)],
        [new DiskSpecs(0, "ST1500DL003", 1397 * Gb, "SATA", false, ["D:"]), new DiskSpecs(1, "KINGSTON SA400", 223 * Gb, "SATA", true, [])],
        new BoardSpecs("PICHAU", "PCH-B550M-CR", "American Megatrends", "5.17", "07/23/2026"),
        [
            new NetworkSpecs("Tailscale", "Tailscale Tunnel", "VPN", 100_000_000_000, null, "100.64.0.1"),
            new NetworkSpecs("Ethernet 4", "Realtek PCIe GbE", "Ethernet", 1_000_000_000, "AA:BB", "192.168.0.2"),
        ],
        battery);

    [Fact]
    public async Task Loads_once_into_eight_sections()
    {
        var reader = new FakeReader(SamplePc());
        var specs = new SpecsViewModel(reader, new NullHost(), null);

        await specs.EnsureLoaded();
        await specs.EnsureLoaded();

        Assert.Equal(1, reader.Reads);
        Assert.True(specs.IsLoaded);
        Assert.Equal(["01", "02", "03", "04", "05", "06", "07", "08"], specs.Sections.Select(s => s.Index));
    }

    [Fact]
    public void Describes_each_part_in_plain_rows()
    {
        var sections = SpecsViewModel.Build(SamplePc());
        string Value(int section, string label) => sections[section].Rows.Single(r => r.Label == label).Value;

        Assert.Equal("PICHAU PCH-B550M-CR", sections[0].Headline);
        Assert.Equal("1 d 2 h", Value(0, "On for"));
        Assert.Equal("AMD Ryzen 5 5600 6-Core Processor", sections[1].Headline);
        Assert.Equal("6 cores · 12 threads", Value(1, "Cores"));
        Assert.Equal("3.49 GHz", Value(1, "Base speed"));
        Assert.Equal("L1 384 KB · L2 3.00 MB · L3 32.0 MB", Value(1, "Cache"));
        Assert.Equal("16 GB", sections[2].Headline);
        Assert.Equal("2 of 4 in use", Value(2, "Slots"));
        Assert.Equal("8.00 GB · DDR4 · 2667 MT/s (running at 2400 MT/s) · DIMM", Value(2, "DIMM 0"));
        Assert.Equal("2560 × 1440 · 165 Hz · main", Value(3, "Display 1")); // the main display comes first
        Assert.Equal("8 GB", Value(3, "Video memory"));
        Assert.Equal("KINGSTON SA400 · 223 GB · SSD · SATA · no drive letter", Value(4, "Disk 1"));
        Assert.Equal("Ethernet · 1 Gbps", sections[6].Headline);
        Assert.Equal("Desktop", sections[7].Headline);
    }

    [Fact]
    public void Graphics_lists_the_compositor_after_the_displays_and_Windows_has_none()
    {
        var withCompositor = SpecsViewModel.Build(SamplePc() with { Compositor = new CompositorSpecs("Wayland", "KWin", "6.8.1") })[3];
        var windows = SpecsViewModel.Build(SamplePc())[3];

        Assert.Equal(["Display 1", "Display 2"], withCompositor.Rows.Skip(withCompositor.Rows.Count - 3).Take(2).Select(r => r.Label));
        Assert.Equal(new SpecRow("Compositor", "KWin 6.8.1 (Wayland)"), withCompositor.Rows[^1]);
        Assert.DoesNotContain(windows.Rows, r => r.Label == "Compositor");
    }

    [Fact]
    public async Task Copy_all_includes_the_compositor_in_both_languages()
    {
        var clipboard = new FakeClipboard();
        var specs = new SpecsViewModel(new FakeReader(SamplePc() with { Compositor = new CompositorSpecs("Wayland", "Mutter", "47.1", "GNOME Shell") }), new NullHost(), clipboard);
        await specs.EnsureLoaded();

        await specs.CopyCommand.ExecuteAsync(null);

        Assert.Contains("  Compositor: Mutter 47.1 (GNOME Shell, Wayland)", clipboard.Text);
        Assert.Equal("Compositor", Pennyworth.App.Resources.Strings.ResourceManager.GetString("SpecCompositor", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")));
    }

    [Fact]
    public void Shows_the_battery_on_laptops()
    {
        var power = SpecsViewModel.Build(SamplePc(new BatterySpecs(81, IsCharging: true, OnAcPower: true)))[7];

        Assert.Equal("81%", power.Headline);
        Assert.Equal("charging", power.Rows[0].Value);
    }

    [Fact]
    public async Task Copies_everything_as_text()
    {
        var host = new NullHost();
        var clipboard = new FakeClipboard();
        var specs = new SpecsViewModel(new FakeReader(SamplePc()), host, clipboard);
        await specs.EnsureLoaded();

        await specs.CopyCommand.ExecuteAsync(null);

        Assert.StartsWith("Your PC, in detail", clipboard.Text);
        Assert.Contains("PROCESSOR: AMD Ryzen 5 5600 6-Core Processor", clipboard.Text);
        Assert.Contains("  Cores: 6 cores · 12 threads", clipboard.Text);
        Assert.Single(host.Toasts);
    }

    [Fact]
    public async Task The_sidebar_opens_and_loads_the_specs()
    {
        var viewModel = new MainWindowViewModel(TestApp.Services() with { Specs = new FakeReader(SamplePc()) });

        viewModel.GoSpecsCommand.Execute(null);
        await viewModel.Specs.CurrentLoad;

        Assert.True(viewModel.IsSpecsPage);
        Assert.Same(viewModel.Specs, viewModel.MainContent);
        Assert.True(viewModel.Specs.IsLoaded);
    }

    // ---- the opt-in read of the memory modules (Linux) ----

    private static PcSpecs ModulesUnknown() => SamplePc() with { Memory = new MemorySpecs(16 * Gb, null, []) };

    private static readonly MemoryModule[] TwoModules =
    [
        new("DIMM_A1", 8 * Gb, "DDR4", 3200, 3000, "Kingston", "KF3200C16D4/8GX", "DIMM"),
        new("DIMM_B1", 8 * Gb, "DDR4", 3200, 3200, null, null, "DIMM"),
    ];

    [Fact]
    public async Task Offers_the_button_only_where_the_modules_are_unknown_and_this_OS_can_ask()
    {
        var linux = new SpecsViewModel(new FakeReader(ModulesUnknown()), new NullHost(), null, new FakeModules(_ => Task.FromResult(MemoryModulesResult.Cancelled)));
        var known = new SpecsViewModel(new FakeReader(SamplePc()), new NullHost(), null, new FakeModules(_ => Task.FromResult(MemoryModulesResult.Cancelled)));
        var windows = new SpecsViewModel(new FakeReader(ModulesUnknown()), new NullHost(), null);
        await Task.WhenAll(linux.EnsureLoaded(), known.EnsureLoaded(), windows.EnsureLoaded());

        Assert.True(linux.Sections[2].OffersModules);
        Assert.True(linux.CanReadModules);
        Assert.True(linux.ReadModulesCommand.CanExecute(null));
        Assert.All(linux.Sections.Where(s => s.Index != "03"), s => Assert.False(s.OffersModules));
        Assert.False(known.Sections[2].OffersModules);
        Assert.False(known.CanReadModules);
        Assert.False(windows.Sections[2].OffersModules);
        Assert.False(windows.CanReadModules);
        Assert.False(windows.ReadModulesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Nothing_is_asked_until_the_button_is_pressed()
    {
        var modules = new FakeModules(_ => Task.FromResult(MemoryModulesResult.Cancelled));
        var specs = new SpecsViewModel(new FakeReader(ModulesUnknown()), new NullHost(), null, modules);

        await specs.EnsureLoaded();

        Assert.Equal(0, modules.Reads);
        Assert.Null(specs.ModulesMessage);
    }

    [Fact]
    public async Task Waits_for_the_password_then_shows_the_modules_like_Windows_does()
    {
        var answer = new TaskCompletionSource<MemoryModulesResult>();
        var specs = new SpecsViewModel(new FakeReader(ModulesUnknown()), new NullHost(), null, new FakeModules(_ => answer.Task));
        await specs.EnsureLoaded();

        Task reading = specs.ReadModulesCommand.ExecuteAsync(null);

        Assert.True(specs.IsReadingModules);
        Assert.Equal("Waiting for the password prompt…", specs.ModulesMessage);
        Assert.False(specs.ReadModulesCommand.CanExecute(null)); // no second prompt on top of the first

        answer.SetResult(MemoryModulesResult.Read(TwoModules, 4));
        await reading;

        Assert.False(specs.IsReadingModules);
        Assert.Null(specs.ModulesMessage);
        Assert.False(specs.CanReadModules);
        Assert.False(specs.Sections[2].OffersModules);
        Assert.Equal("2 of 4 in use", specs.Sections[2].Rows.Single(r => r.Label == "Slots").Value);
        Assert.Equal("8.00 GB · DDR4 · 3200 MT/s (running at 3000 MT/s) · DIMM · Kingston KF3200C16D4/8GX", specs.Sections[2].Rows.Single(r => r.Label == "DIMM_A1").Value);
        Assert.Equal(TwoModules, specs.Specs!.Memory.Modules);
    }

    [Fact]
    public async Task Copy_all_and_the_assistant_see_the_modules_once_read()
    {
        var clipboard = new FakeClipboard();
        var viewModel = new MainWindowViewModel(TestApp.Services() with
        {
            Specs = new FakeReader(ModulesUnknown()),
            Clipboard = clipboard,
            MemoryModules = new FakeModules(_ => Task.FromResult(MemoryModulesResult.Read(TwoModules, 4))),
        });
        viewModel.GoSpecsCommand.Execute(null);
        await viewModel.Specs.CurrentLoad;

        await viewModel.Specs.ReadModulesCommand.ExecuteAsync(null);
        await viewModel.Specs.CopyCommand.ExecuteAsync(null);
        PcSnapshot snapshot = await viewModel.SnapshotAsync();

        Assert.Contains("  DIMM_B1: 8.00 GB · DDR4 · 3200 MT/s · DIMM", clipboard.Text);
        Assert.Equal(TwoModules, snapshot.Specs!.Memory.Modules);
        Assert.Equal(4, snapshot.Specs.Memory.Slots);
    }

    [Theory]
    [InlineData(MemoryModulesOutcome.Cancelled, "No password was given, so nothing was read.")]
    [InlineData(MemoryModulesOutcome.Unavailable, "Couldn't read the memory modules on this PC.")]
    public async Task A_failed_read_says_so_in_one_line_and_keeps_the_button(MemoryModulesOutcome outcome, string message)
    {
        MemoryModulesResult result = outcome == MemoryModulesOutcome.Cancelled ? MemoryModulesResult.Cancelled : MemoryModulesResult.Unavailable;
        var specs = new SpecsViewModel(new FakeReader(ModulesUnknown()), new NullHost(), null, new FakeModules(_ => Task.FromResult(result)));
        await specs.EnsureLoaded();

        await specs.ReadModulesCommand.ExecuteAsync(null);

        Assert.Equal(message, specs.ModulesMessage);
        Assert.False(specs.IsReadingModules);
        Assert.True(specs.CanReadModules);
        Assert.True(specs.Sections[2].OffersModules);
        Assert.Empty(specs.Specs!.Memory.Modules);
    }

    [Fact]
    public async Task The_message_is_gone_when_the_person_tries_again()
    {
        var answers = new Queue<MemoryModulesResult>([MemoryModulesResult.Cancelled, MemoryModulesResult.Read(TwoModules, 4)]);
        var specs = new SpecsViewModel(new FakeReader(ModulesUnknown()), new NullHost(), null, new FakeModules(_ => Task.FromResult(answers.Dequeue())));
        await specs.EnsureLoaded();

        await specs.ReadModulesCommand.ExecuteAsync(null);
        Assert.NotNull(specs.ModulesMessage);
        await specs.ReadModulesCommand.ExecuteAsync(null);

        Assert.Null(specs.ModulesMessage);
        Assert.Equal(2, specs.Specs!.Memory.Modules.Count);
    }

    [Fact]
    public async Task A_reader_that_throws_is_unavailable_not_a_crash()
    {
        var specs = new SpecsViewModel(new FakeReader(ModulesUnknown()), new NullHost(), null, new FakeModules(_ => throw new IOException("no pipe")));
        await specs.EnsureLoaded();

        await specs.ReadModulesCommand.ExecuteAsync(null);

        Assert.Equal("Couldn't read the memory modules on this PC.", specs.ModulesMessage);
    }

    [Fact]
    public async Task Cancelling_withdraws_the_request_without_a_message()
    {
        CancellationToken seen = default;
        var specs = new SpecsViewModel(new FakeReader(ModulesUnknown()), new NullHost(), null, new FakeModules(async token =>
        {
            seen = token;
            await Task.Delay(Timeout.Infinite, token);
            return MemoryModulesResult.Cancelled;
        }));
        await specs.EnsureLoaded();
        Task reading = specs.ReadModulesCommand.ExecuteAsync(null);
        await WaitFor(() => seen.CanBeCanceled);

        specs.CancelModulesRead();
        await reading;

        Assert.True(seen.IsCancellationRequested);
        Assert.False(specs.IsReadingModules);
        Assert.Null(specs.ModulesMessage);
        Assert.True(specs.CanReadModules); // the button is back for another try
    }

    [Fact]
    public async Task Leaving_the_specs_screen_withdraws_the_request()
    {
        CancellationToken seen = default;
        var viewModel = new MainWindowViewModel(TestApp.Services() with
        {
            Specs = new FakeReader(ModulesUnknown()),
            MemoryModules = new FakeModules(async token =>
            {
                seen = token;
                await Task.Delay(Timeout.Infinite, token);
                return MemoryModulesResult.Cancelled;
            }),
        });
        viewModel.GoSpecsCommand.Execute(null);
        await viewModel.Specs.CurrentLoad;
        Task reading = viewModel.Specs.ReadModulesCommand.ExecuteAsync(null);
        await WaitFor(() => seen.CanBeCanceled);

        viewModel.GoHomeCommand.Execute(null);
        await reading;

        Assert.True(seen.IsCancellationRequested);
        Assert.False(viewModel.Specs.IsReadingModules);
    }

    [Fact]
    public void The_texts_exist_in_both_languages()
    {
        var portuguese = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        foreach (string key in new[] { "SpecsModulesInfo", "SpecsModulesRead", "SpecsModulesReading", "SpecsModulesCancelled", "SpecsModulesUnavailable" })
        {
            string? english = Pennyworth.App.Resources.Strings.ResourceManager.GetString(key, System.Globalization.CultureInfo.InvariantCulture);
            string? translated = Pennyworth.App.Resources.Strings.ResourceManager.GetString(key, portuguese);
            Assert.False(string.IsNullOrWhiteSpace(english), key);
            Assert.False(string.IsNullOrWhiteSpace(translated), key);
            Assert.NotEqual(english, translated);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private sealed class FakeModules(Func<CancellationToken, Task<MemoryModulesResult>> read) : IMemoryModuleReader
    {
        public int Reads { get; private set; }

        public Task<MemoryModulesResult> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return read(cancellationToken);
        }
    }

    private sealed class FakeReader(PcSpecs specs) : ISpecsReader
    {
        public int Reads { get; private set; }

        public PcSpecs Read()
        {
            Reads++;
            return specs;
        }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string Text { get; private set; } = "";

        public Task SetTextAsync(string text)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class NullHost : IDialogHost
    {
        public List<string> Toasts { get; } = [];

        public void ShowDialog(object dialog)
        {
        }

        public void CloseDialog()
        {
        }

        public void ShowToast(string message) => Toasts.Add(message);
    }
}
