using System.Globalization;
using Pennyworth.App.Resources;
using Pennyworth.App.ViewModels;
using Pennyworth.Cleanup;
using Pennyworth.Core.Privilege;
using Pennyworth.Core.Storage;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring.Hardware;
using Pennyworth.Platform;
using Pennyworth.Scanning;
using static Pennyworth.App.Tests.SampleTrees;

namespace Pennyworth.App.Tests;

/// <summary>
/// The view-model states of the four opt-in features that ask for an administrator's password: the second step of the
/// system space, locked folders, cleaning root-owned rules and the memory modules. Each runs against fakes; no prompt exists.
/// </summary>
public class PrivilegedUiTests
{
    /// <summary>A tree like <see cref="SampleTrees.Build"/> plus one folder nobody could list: 10 GB of it exists, the scan knows none.</summary>
    private static StorageTree TreeWithLockedFolder()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        int games = b.AddDirectory(StorageTreeBuilder.RootIndex, "Games", StorageCategory.Games);
        b.AddFile(games, "game.pak", 50 * Gb, 50 * Gb, StorageCategory.Games);
        b.AddFile(StorageTreeBuilder.RootIndex, "stray.bin", 5 * Gb, 5 * Gb, StorageCategory.Other);
        int secret = b.AddDirectory(StorageTreeBuilder.RootIndex, "Secret", StorageCategory.Other);
        b.MarkLocked(secret);
        return b.Build();
    }

    private static async Task<MainWindowViewModel> Scanned(IPrivilegedOperations? privileged, IHiddenSpaceService? hidden = null, Func<string, StorageTree>? tree = null)
    {
        var viewModel = new MainWindowViewModel(TestApp.Services(
            scanner: new FakeScanner(tree ?? (_ => TreeWithLockedFolder())),
            hidden: hidden ?? new FakeHiddenSpace(new UnlockResult(UnlockOutcome.Measured, new HiddenSpaceReport(3 * Gb, 1 * Gb))),
            privileged: privileged));
        viewModel.Initialize();
        await viewModel.CurrentScan;
        return viewModel;
    }

    private static async Task Unlock(MainWindowViewModel viewModel)
    {
        viewModel.RequestUnlock();
        var dialog = Assert.IsType<UnlockDialogViewModel>(viewModel.Dialog);
        dialog.UnlockCommand.Execute(null);
        await dialog.Run;
    }

    private static double Total(StorageSummaryViewModel summary) => summary.Segments.Sum(segment => segment.Fraction);

    // ----- the second step of the system space -----

    [Fact]
    public async Task Nothing_new_shows_where_the_platform_has_no_privileged_operations()
    {
        MainWindowViewModel viewModel = await Scanned(privileged: null);
        await Unlock(viewModel);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;

        Assert.False(summary.ShowAdminSpaceBanner);
        Assert.False(summary.ShowAdminSpaceReason);
        Assert.False(summary.ShowLockedBanner);
        Assert.False(summary.ShowLockedReason);
        viewModel.RequestAdminSpace();
        Assert.Null(viewModel.Dialog);
    }

    [Fact]
    public async Task The_second_step_is_offered_only_after_the_first_on_the_system_drive()
    {
        var ops = new FakePrivileged();
        MainWindowViewModel viewModel = await Scanned(ops);

        Assert.False(viewModel.SystemSummary!.ShowAdminSpaceBanner); // not unlocked yet
        viewModel.RequestAdminSpace();
        Assert.Null(viewModel.Dialog);

        await Unlock(viewModel);

        Assert.True(viewModel.SystemSummary.ShowAdminSpaceBanner);
        Assert.False(viewModel.SystemSummary.ShowAdminSpaceReason);
    }

    [Fact]
    public async Task When_it_cannot_be_asked_the_reason_replaces_the_button()
    {
        var ops = new FakePrivileged { SystemSpace = PrivilegedCapability.Missing(PrivilegedUnavailableReason.NoAuthenticationAgent) };
        MainWindowViewModel viewModel = await Scanned(ops);
        await Unlock(viewModel);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;

        Assert.False(summary.ShowAdminSpaceBanner);
        Assert.True(summary.ShowAdminSpaceReason);
        Assert.Contains(Strings.PrivReasonNoAuthenticationAgent, summary.AdminSpaceReasonText);
    }

    [Fact]
    public async Task What_the_administrator_measured_replaces_the_estimate_and_the_banner_goes()
    {
        var ops = new FakePrivileged { SpaceResult = PrivilegedResult<HiddenSpaceReport>.Done(new HiddenSpaceReport(RestorePoints: null, NtfsMetadata: 2 * Gb)) };
        MainWindowViewModel viewModel = await Scanned(ops);
        await Unlock(viewModel);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;
        long unaccountedBefore = summary.Unaccounted.Bytes;

        viewModel.RequestAdminSpace();
        var dialog = Assert.IsType<AdminSpaceDialogViewModel>(viewModel.Dialog);
        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Null(viewModel.Dialog);
        Assert.True(summary.IncludesAdminSpace);
        Assert.False(summary.ShowAdminSpaceBanner);
        Assert.Equal(TestApp.DriveC.RootPath, Assert.Single(ops.SpaceCalls));
        // The earlier 3 GB of snapshots stay (the administrator measured none), the metadata 1 GB is replaced by 2 GB: 1 GB more is accounted for.
        Assert.Equal(unaccountedBefore - (1 * Gb), summary.Unaccounted.Bytes);
        Assert.Equal(1.0, Total(summary), 3);
    }

    [Fact]
    public void A_report_the_administrator_could_not_fill_keeps_the_old_numbers()
    {
        var before = new HiddenSpaceReport(3 * Gb, 1 * Gb);

        Assert.Equal(new HiddenSpaceReport(3 * Gb, 2 * Gb), MainWindowViewModel.MergeAdminSpace(before, new HiddenSpaceReport(null, 2 * Gb)));
        Assert.Equal(new HiddenSpaceReport(5 * Gb, 1 * Gb), MainWindowViewModel.MergeAdminSpace(before, new HiddenSpaceReport(5 * Gb, null)));
        Assert.Equal(new HiddenSpaceReport(5 * Gb, 2 * Gb), MainWindowViewModel.MergeAdminSpace(null, new HiddenSpaceReport(5 * Gb, 2 * Gb)));
    }

    [Fact]
    public async Task A_dismissed_prompt_adds_nothing_and_can_be_tried_again()
    {
        var ops = new FakePrivileged { SpaceResult = PrivilegedResult<HiddenSpaceReport>.Cancelled };
        MainWindowViewModel viewModel = await Scanned(ops);
        await Unlock(viewModel);
        long before = viewModel.SystemSummary!.Unaccounted.Bytes;

        viewModel.RequestAdminSpace();
        var dialog = Assert.IsType<AdminSpaceDialogViewModel>(viewModel.Dialog);
        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Equal(PrivilegedDialogState.Cancelled, dialog.State);
        Assert.Equal(Strings.PrivCancelled, dialog.Message);
        Assert.Same(dialog, viewModel.Dialog);
        Assert.Equal(before, viewModel.SystemSummary.Unaccounted.Bytes);
        Assert.False(viewModel.SystemSummary.IncludesAdminSpace);
        dialog.TryAgainCommand.Execute(null);
        Assert.Equal(PrivilegedDialogState.Asking, dialog.State);
        Assert.True(dialog.StartCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(PrivilegedUnavailableReason.HelperNotTrusted)]
    [InlineData(PrivilegedUnavailableReason.NoPkexec)]
    public async Task An_unavailable_answer_shows_its_reason_and_adds_nothing(PrivilegedUnavailableReason reason)
    {
        var ops = new FakePrivileged { SpaceResult = PrivilegedResult<HiddenSpaceReport>.NotAvailable(reason) };
        MainWindowViewModel viewModel = await Scanned(ops);
        await Unlock(viewModel);

        viewModel.RequestAdminSpace();
        var dialog = Assert.IsType<AdminSpaceDialogViewModel>(viewModel.Dialog);
        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Equal(PrivilegedDialogState.Unavailable, dialog.State);
        Assert.Equal(PrivilegedText.Reason(reason), dialog.Message);
        Assert.False(viewModel.SystemSummary!.IncludesAdminSpace);
    }

    [Fact]
    public async Task A_failure_adds_nothing()
    {
        var ops = new FakePrivileged { SpaceResult = PrivilegedResult<HiddenSpaceReport>.Failed("boom") };
        MainWindowViewModel viewModel = await Scanned(ops);
        await Unlock(viewModel);

        viewModel.RequestAdminSpace();
        var dialog = Assert.IsType<AdminSpaceDialogViewModel>(viewModel.Dialog);
        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Equal(PrivilegedDialogState.Failed, dialog.State);
        Assert.Equal(Strings.PrivFailed, dialog.Message);
        Assert.False(viewModel.SystemSummary!.IncludesAdminSpace);
    }

    // ----- locked folders -----

    [Fact]
    public async Task The_locked_banner_counts_the_folders_and_needs_the_capability()
    {
        var ops = new FakePrivileged();
        MainWindowViewModel viewModel = await Scanned(ops);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;

        Assert.Equal(1, summary.LockedCount);
        Assert.True(summary.ShowLockedBanner);
        Assert.Equal(string.Format(Strings.LockedBannerOne, 1), summary.LockedBannerText);

        var missing = new FakePrivileged { FoldersCapability = PrivilegedCapability.Missing(PrivilegedUnavailableReason.HelperNotInstalled) };
        StorageSummaryViewModel blocked = (await Scanned(missing)).SystemSummary!;
        Assert.False(blocked.ShowLockedBanner);
        Assert.True(blocked.ShowLockedReason);
        Assert.Contains(Strings.PrivReasonHelperNotInstalled, blocked.LockedReasonText);
    }

    [Fact]
    public async Task No_locked_folder_means_no_banner()
    {
        MainWindowViewModel viewModel = await Scanned(new FakePrivileged(), tree: _ => SampleTrees.Build());

        Assert.Equal(0, viewModel.SystemSummary!.LockedCount);
        Assert.False(viewModel.SystemSummary.ShowLockedBanner);
        Assert.False(viewModel.SystemSummary.ShowLockedReason);
    }

    [Fact]
    public async Task Measuring_the_locked_folders_adds_their_size_and_the_bar_still_adds_up()
    {
        var ops = new FakePrivileged();
        MainWindowViewModel viewModel = await Scanned(ops);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;
        long unaccountedBefore = summary.Unaccounted.Bytes;
        long otherBefore = summary.Categories.Single(row => row.Category == StorageCategory.Other).Bytes;
        CategoryRowViewModel? selected = summary.SelectedCategory;
        ops.FoldersResult = paths => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([new MeasuredFolder(paths[0], 10 * Gb, 10 * Gb, 42, Complete: true)]);

        viewModel.RequestLockedMeasure(summary);
        var dialog = Assert.IsType<LockedMeasureDialogViewModel>(viewModel.Dialog);
        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Null(viewModel.Dialog);
        Assert.Equal(Path.Combine(@"C:\", "Secret"), Assert.Single(ops.FolderCalls.Single()));
        Assert.Equal(unaccountedBefore - (10 * Gb), summary.Unaccounted.Bytes);
        Assert.Equal(otherBefore + (10 * Gb), summary.Categories.Single(row => row.Category == StorageCategory.Other).Bytes);
        Assert.Equal(0, summary.LockedCount);
        Assert.False(summary.ShowLockedBanner);
        Assert.Same(selected, summary.SelectedCategory);
        Assert.Equal(1.0, Total(summary), 3);
        Assert.NotNull(viewModel.Toast);
    }

    [Fact]
    public async Task A_dismissed_prompt_leaves_the_folders_locked()
    {
        var ops = new FakePrivileged { FoldersResult = _ => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Cancelled };
        MainWindowViewModel viewModel = await Scanned(ops);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;
        long unaccountedBefore = summary.Unaccounted.Bytes;

        viewModel.RequestLockedMeasure(summary);
        var dialog = Assert.IsType<LockedMeasureDialogViewModel>(viewModel.Dialog);
        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Equal(PrivilegedDialogState.Cancelled, dialog.State);
        Assert.Equal(1, summary.LockedCount);
        Assert.Equal(unaccountedBefore, summary.Unaccounted.Bytes);
    }

    [Fact]
    public async Task A_folder_the_helper_did_not_answer_for_stays_locked()
    {
        var ops = new FakePrivileged { FoldersResult = _ => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([new MeasuredFolder("/not/asked", 5 * Gb, 5 * Gb, 1, true)]) };
        MainWindowViewModel viewModel = await Scanned(ops);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;

        viewModel.RequestLockedMeasure(summary);
        var dialog = Assert.IsType<LockedMeasureDialogViewModel>(viewModel.Dialog);
        dialog.StartCommand.Execute(null);
        await dialog.Run;

        Assert.Equal(PrivilegedDialogState.Failed, dialog.State);
        Assert.Equal(1, summary.LockedCount);
    }

    [Fact]
    public async Task A_measured_folder_shows_its_size_in_the_browser()
    {
        var ops = new FakePrivileged();
        MainWindowViewModel viewModel = await Scanned(ops);
        StorageSummaryViewModel summary = viewModel.SystemSummary!;
        ops.FoldersResult = paths => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([new MeasuredFolder(paths[0], 10 * Gb, 10 * Gb, 42, Complete: false)]);

        viewModel.RequestLockedMeasure(summary);
        var dialog = Assert.IsType<LockedMeasureDialogViewModel>(viewModel.Dialog);
        dialog.StartCommand.Execute(null);
        await dialog.Run;

        StorageNode secret = summary.Tree!.Root.GetChildren().Single(child => child.Name == "Secret");
        FolderItemViewModel row = FolderItemViewModel.ForNode(secret, largest: 50 * Gb);
        Assert.True(row.IsLocked);
        Assert.True(row.IsMeasured);
        Assert.False(row.ShowLockedBadge);
        Assert.NotEqual("—", row.SizeText);
        Assert.Contains(Strings.LockedMeasuredIncomplete, viewModel.Toast);
    }

    // ----- root-owned cleanup rules -----

    private static readonly CleanupRule HomeRule = FakeCleanup.Rule("browser-cache", SafetyLevel.Safe);
    private static readonly CleanupRule PackagesRule = FakeCleanup.Rule("system-package-cache", SafetyLevel.UsuallySafe, admin: true);
    private static readonly CleanupRule CrashRule = FakeCleanup.Rule("system-crash-reports", SafetyLevel.UsuallySafe, admin: true);

    private static async Task<(CleanupViewModel Cleanup, AdminFakeCleanup Service)> Measured(AdminFakeCleanup service)
    {
        var host = new FakeDialogHost();
        var cleanup = new CleanupViewModel(service, host, () => { });
        await cleanup.MeasureAsync();
        return (cleanup, service);
    }

    [Fact]
    public async Task A_rule_that_asks_for_a_password_is_selectable_but_never_pre_ticked()
    {
        var service = new AdminFakeCleanup([(HomeRule, 10 * Gb, null), (PackagesRule, 4 * Gb, PrivilegedCapability.Ready)]);

        var (cleanup, _) = await Measured(service);

        CleanupItemViewModel home = cleanup.Items.Single(item => item.Rule == HomeRule);
        CleanupItemViewModel packages = cleanup.Items.Single(item => item.Rule == PackagesRule);
        Assert.True(home.IsSelected);
        Assert.False(packages.IsSelected);
        Assert.True(packages.IsAvailable);
        Assert.True(packages.NeedsPassword);
        Assert.False(packages.NeedsFullScan);
        Assert.False(packages.HasAdminReason);
        cleanup.ToggleCommand.Execute(packages);
        Assert.True(packages.IsSelected);
        Assert.Equal(14 * Gb, cleanup.SelectedBytes);
    }

    [Fact]
    public async Task A_rule_that_cannot_ask_shows_why_and_cannot_be_selected()
    {
        var service = new AdminFakeCleanup([(PackagesRule, 4 * Gb, PrivilegedCapability.Missing(PrivilegedUnavailableReason.HelperNotInstalled))]);

        var (cleanup, _) = await Measured(service);

        CleanupItemViewModel packages = Assert.Single(cleanup.Items);
        Assert.False(packages.IsAvailable);
        Assert.True(packages.HasAdminReason);
        Assert.False(packages.NeedsFullScan);
        Assert.Equal(PrivilegedText.Reason(PrivilegedUnavailableReason.HelperNotInstalled), packages.AdminReasonText);
        cleanup.ToggleCommand.Execute(packages);
        Assert.False(packages.IsSelected);
    }

    [Fact]
    public async Task Without_a_platform_that_asks_the_old_wording_stays()
    {
        var service = new AdminFakeCleanup([(PackagesRule, 4 * Gb, null)]);

        var (cleanup, _) = await Measured(service);

        CleanupItemViewModel packages = Assert.Single(cleanup.Items);
        Assert.False(packages.IsAvailable);
        Assert.True(packages.NeedsFullScan);
        Assert.False(packages.HasAdminReason);
        Assert.False(packages.NeedsPassword);
    }

    [Fact]
    public async Task The_confirmation_names_the_rules_that_ask_for_the_password()
    {
        var service = new AdminFakeCleanup([(HomeRule, 10 * Gb, null), (PackagesRule, 4 * Gb, PrivilegedCapability.Ready)]);
        var (cleanup, _) = await Measured(service);
        cleanup.ToggleCommand.Execute(cleanup.Items.Single(item => item.Rule == PackagesRule));

        var dialog = new CleanConfirmDialogViewModel(cleanup, new FakeDialogHost());

        Assert.True(dialog.ShowPasswordNote);
        Assert.Contains("system-package-cache", dialog.PasswordNote);
        Assert.DoesNotContain("browser-cache", dialog.PasswordNote);

        var plain = await Measured(new AdminFakeCleanup([(HomeRule, 10 * Gb, null)]));
        Assert.False(new CleanConfirmDialogViewModel(plain.Cleanup, new FakeDialogHost()).ShowPasswordNote);
    }

    [Fact]
    public async Task Rules_that_ask_run_last_and_a_cancelled_prompt_is_not_asked_again()
    {
        var service = new AdminFakeCleanup([(PackagesRule, 4 * Gb, PrivilegedCapability.Ready), (CrashRule, 1 * Gb, PrivilegedCapability.Ready), (HomeRule, 10 * Gb, null)])
        {
            Results = { [PackagesRule.Id] = new CleanupResult(PackagesRule, 0, 0, 3, PrivilegedOutcome.Cancelled) },
        };
        var (cleanup, _) = await Measured(service);
        foreach (CleanupItemViewModel item in cleanup.Items)
        {
            item.IsSelected = true;
        }

        await cleanup.CleanSelectedAsync();

        Assert.Equal(["browser-cache", "system-package-cache"], service.Cleaned); // the crash rule was never attempted
        Assert.Equal(10 * Gb, cleanup.FreedBytes);
        CleanupItemViewModel packages = cleanup.Items.Single(item => item.Rule == PackagesRule);
        CleanupItemViewModel crash = cleanup.Items.Single(item => item.Rule == CrashRule);
        Assert.Equal(Strings.CleanCancelled, packages.OutcomeText);
        Assert.Equal(Strings.CleanSkippedCancelled, crash.OutcomeText);
        Assert.Equal(CleanupPhase.Done, cleanup.Phase);
    }

    [Fact]
    public async Task Only_what_was_done_goes_to_the_history()
    {
        string dir = Directory.CreateTempSubdirectory("pw-history").FullName;
        try
        {
            var history = new CleanupHistory(Path.Combine(dir, "history.json"));
            var service = new AdminFakeCleanup([(HomeRule, 10 * Gb, null), (PackagesRule, 4 * Gb, PrivilegedCapability.Ready)])
            {
                Results = { [PackagesRule.Id] = new CleanupResult(PackagesRule, 0, 0, 3, PrivilegedOutcome.NotAvailable, PrivilegedUnavailableReason.HelperNotTrusted) },
            };
            var cleanup = new CleanupViewModel(service, new FakeDialogHost(), () => { }, history);
            await cleanup.MeasureAsync();
            cleanup.Items.Single(item => item.Rule == PackagesRule).IsSelected = true;

            await cleanup.CleanSelectedAsync();

            string json = File.ReadAllText(Path.Combine(dir, "history.json"));
            Assert.Contains("browser-cache", json);
            Assert.DoesNotContain("system-package-cache", json);
            Assert.Equal(PrivilegedText.Reason(PrivilegedUnavailableReason.HelperNotTrusted), cleanup.Items.Single(item => item.Rule == PackagesRule).OutcomeText);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_finished_admin_clean_counts_what_was_freed()
    {
        var service = new AdminFakeCleanup([(PackagesRule, 4 * Gb, PrivilegedCapability.Ready)])
        {
            Results = { [PackagesRule.Id] = new CleanupResult(PackagesRule, 3 * Gb, 7, 1) },
        };
        var (cleanup, _) = await Measured(service);
        cleanup.Items.Single().IsSelected = true;

        await cleanup.CleanSelectedAsync();

        Assert.Equal(3 * Gb, cleanup.FreedBytes);
        Assert.Equal(1, cleanup.SkippedFiles);
        Assert.Null(cleanup.Items.Single().OutcomeText);
    }

    // ----- memory modules -----

    [Theory]
    [InlineData(PrivilegedUnavailableReason.NoPkexec)]
    [InlineData(PrivilegedUnavailableReason.NoAuthenticationAgent)]
    [InlineData(PrivilegedUnavailableReason.HelperOutdated)]
    public async Task The_memory_button_explains_why_it_could_not_ask(PrivilegedUnavailableReason reason)
    {
        var reader = new ModulesReader(MemoryModulesResult.NotAvailable(reason));
        var specs = new SpecsViewModel(new OnePc(SpecsViewModelTests.SamplePc() with { Memory = new MemorySpecs(16 * Gb, null, []) }), new FakeDialogHost(), null, reader);
        await specs.EnsureLoaded();

        specs.ReadModulesCommand.Execute(null);
        await WaitFor(() => !specs.IsReadingModules);

        Assert.Equal(PrivilegedText.Reason(reason), specs.ModulesMessage);
    }

    [Fact]
    public async Task A_plain_unavailable_memory_read_keeps_the_generic_sentence()
    {
        var reader = new ModulesReader(MemoryModulesResult.Unavailable);
        var specs = new SpecsViewModel(new OnePc(SpecsViewModelTests.SamplePc() with { Memory = new MemorySpecs(16 * Gb, null, []) }), new FakeDialogHost(), null, reader);
        await specs.EnsureLoaded();

        specs.ReadModulesCommand.Execute(null);
        await WaitFor(() => !specs.IsReadingModules);

        Assert.Equal(Strings.SpecsModulesUnavailable, specs.ModulesMessage);
    }

    // ----- wording -----

    [Fact]
    public void Every_reason_has_its_own_sentence_in_both_languages()
    {
        // Resolved through the resource manager with explicit cultures: switching the thread's culture would leak into tests running beside this one.
        string[] keys = ["PrivReasonNoPkexec", "PrivReasonNoAuthenticationAgent", "PrivReasonHelperNotInstalled", "PrivReasonHelperNotTrusted", "PrivReasonHelperOutdated", "PrivReasonUnknown"];
        foreach (string culture in new[] { "en", "pt-BR" })
        {
            string[] sentences = [.. keys.Select(key => Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo(culture)) ?? "")];
            Assert.All(sentences, sentence => Assert.False(string.IsNullOrWhiteSpace(sentence)));
            Assert.Equal(sentences.Length, sentences.Distinct().Count());
        }

        // And every reason picks a sentence of its own.
        var seen = Enum.GetValues<PrivilegedUnavailableReason>().Select(reason => PrivilegedText.Reason(reason)).ToList();
        seen.Add(PrivilegedText.Reason(null));
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    private static async Task WaitFor(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(done());
    }

    // ----- fakes -----

    private sealed class FakeDialogHost : IDialogHost
    {
        public void ShowDialog(object dialog)
        {
        }

        public void CloseDialog()
        {
        }

        public void ShowToast(string message)
        {
        }
    }

    private sealed class OnePc(PcSpecs specs) : ISpecsReader
    {
        public PcSpecs Read() => specs;
    }

    private sealed class ModulesReader(MemoryModulesResult result) : IMemoryModuleReader
    {
        public Task<MemoryModulesResult> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FakePrivileged : IPrivilegedOperations
    {
        public PrivilegedCapability SystemSpace { get; init; } = PrivilegedCapability.Ready;

        public PrivilegedCapability FoldersCapability { get; init; } = PrivilegedCapability.Ready;

        public PrivilegedResult<HiddenSpaceReport> SpaceResult { get; init; } = PrivilegedResult<HiddenSpaceReport>.Done(new HiddenSpaceReport(1 * Gb, 1 * Gb));

        public Func<IReadOnlyList<string>, PrivilegedResult<IReadOnlyList<MeasuredFolder>>> FoldersResult { get; set; } = _ => PrivilegedResult<IReadOnlyList<MeasuredFolder>>.Done([]);

        public List<string> SpaceCalls { get; } = [];

        public List<IReadOnlyList<string>> FolderCalls { get; } = [];

        public PrivilegedCapability Capability(PrivilegedOperation operation) => operation switch
        {
            PrivilegedOperation.SystemSpace => SystemSpace,
            PrivilegedOperation.Folders => FoldersCapability,
            _ => PrivilegedCapability.Ready,
        };

        public Task<PrivilegedResult<byte[]>> ReadFirmwareTablesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PrivilegedResult<string>> ReadMemoryDevicesTextAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PrivilegedResult<HiddenSpaceReport>> MeasureSystemSpaceAsync(string mountPoint, CancellationToken cancellationToken)
        {
            SpaceCalls.Add(mountPoint);
            return Task.FromResult(SpaceResult);
        }

        public Task<PrivilegedResult<IReadOnlyList<MeasuredFolder>>> MeasureFoldersAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            FolderCalls.Add(paths);
            return Task.FromResult(FoldersResult(paths));
        }

        public Task<PrivilegedResult<CleanupResult>> CleanSystemAsync(CleanupEstimate estimate, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A cleanup service whose admin rules report a capability and end the way the test says; plain rules always finish.</summary>
    private sealed class AdminFakeCleanup((CleanupRule Rule, long Bytes, PrivilegedCapability? Admin)[] items) : ICleanupService
    {
        public List<string> Cleaned { get; } = [];

        public Dictionary<string, CleanupResult> Results { get; } = [];

        public IReadOnlyList<CleanupRule> Rules { get; } = items.Select(i => i.Rule).ToArray();

        public Task<CleanupEstimate> MeasureAsync(CleanupRule rule, CancellationToken cancellationToken)
        {
            (CleanupRule _, long bytes, PrivilegedCapability? admin) = items.Single(i => i.Rule == rule);
            CleanupFile[] files = [new($"/fake/{rule.Id}/a.bin", bytes, DateTime.UnixEpoch)];
            return Task.FromResult(new CleanupEstimate(rule, bytes, 1, IsAvailable: !rule.RequiresAdmin || admin is { Available: true }, [new($"/fake/{rule.Id}", bytes, 1)], files, admin));
        }

        public Task<CleanupResult> CleanAsync(CleanupEstimate estimate, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Cleaned.Add(estimate.Rule.Id);
            progress?.Report(1);
            return Task.FromResult(Results.TryGetValue(estimate.Rule.Id, out CleanupResult? result) ? result : new CleanupResult(estimate.Rule, estimate.Bytes, estimate.Items, 0));
        }
    }
}
