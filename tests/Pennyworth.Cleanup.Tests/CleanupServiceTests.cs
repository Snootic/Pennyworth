using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Pennyworth.Core.Privilege;
using Pennyworth.KnowledgeBase;
using Pennyworth.Tests;

namespace Pennyworth.Cleanup.Tests;

public sealed class CleanupServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pw-clean-").FullName;
    private readonly List<string> _junctions = [];

    public void Dispose()
    {
        _junctions.ForEach(junction => Directory.Delete(junction));
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Measures_without_deleting()
    {
        Write(@"cache\a.bin", 1000);
        Write(@"cache\sub\b.bin", 500);
        var rule = Rule("%ROOT%\\cache");

        var estimate = await Service().MeasureAsync(rule, TestContext.Current.CancellationToken);

        Assert.Equal(1500, estimate.Bytes);
        Assert.Equal(2, estimate.Items);
        Assert.True(estimate.IsAvailable);
        Assert.True(File.Exists(Path.Combine(_root, @"cache\a.bin")));
    }

    [Fact]
    public async Task Deletes_the_contents_but_keeps_the_folder()
    {
        Write(@"cache\a.bin", 1000);
        Write(@"cache\sub\deep\b.bin", 500);
        var progress = new List<double>();

        var result = await MeasureAndClean(Rule("%ROOT%\\cache"), new SyncProgress(progress));

        Assert.Equal(1500, result.BytesFreed);
        Assert.Equal(2, result.FilesDeleted);
        Assert.Equal(0, result.Skipped);
        Assert.True(Directory.Exists(Path.Combine(_root, "cache")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "cache")));
        Assert.Equal(1, progress[^1]);
    }

    [Fact]
    public async Task Skips_files_in_use()
    {
        Write(@"cache\busy.bin", 100);
        Write(@"cache\free.bin", 100);

        CleanupResult result;
        using (File.Open(Path.Combine(_root, @"cache\busy.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await MeasureAndClean(Rule("%ROOT%\\cache"), null);
        }

        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(Path.Combine(_root, @"cache\busy.bin")));
    }

    [WindowsFact]
    public async Task Never_follows_junctions_out_of_the_folder()
    {
        Write(@"precious\photo.jpg", 100);
        Write(@"cache\a.bin", 100);
        CreateJunction(Path.Combine(_root, @"cache\link"), Path.Combine(_root, "precious"));

        await MeasureAndClean(Rule("%ROOT%\\cache"), null);

        Assert.True(File.Exists(Path.Combine(_root, @"precious\photo.jpg")));
    }

    [Fact]
    public async Task Lists_every_file_and_folder_it_would_delete()
    {
        Write(@"cache\a.bin", 1000);
        Write(@"cache\sub\b.bin", 500);
        Write(@"other\c.bin", 100);

        var estimate = await Service().MeasureAsync(Rule("%ROOT%\\cache", "%ROOT%\\other"), TestContext.Current.CancellationToken);

        Assert.Equal(
            [Path.Combine(_root, @"cache\a.bin"), Path.Combine(_root, @"cache\sub\b.bin"), Path.Combine(_root, @"other\c.bin")],
            estimate.Files!.Select(f => f.Path).Order());
        Assert.Equal([(1500L, 2L), (100L, 1L)], estimate.Folders!.Select(f => (f.Bytes, f.Files)));
    }

    [Fact]
    public async Task Deletes_only_what_was_listed()
    {
        Write(@"cache\listed.bin", 100);
        var estimate = await Service().MeasureAsync(Rule("%ROOT%\\cache"), TestContext.Current.CancellationToken);
        Write(@"cache\new-after-preview.bin", 100);
        Write(@"cache\later\also-new.bin", 100);

        var result = await Service().CleanAsync(estimate, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.FilesDeleted);
        Assert.False(File.Exists(Path.Combine(_root, @"cache\listed.bin")));
        Assert.True(File.Exists(Path.Combine(_root, @"cache\new-after-preview.bin")));
        Assert.True(File.Exists(Path.Combine(_root, @"cache\later\also-new.bin")));
    }

    [Fact]
    public async Task Leaves_files_that_changed_since_the_preview()
    {
        Write(@"cache\changed.bin", 100);
        var estimate = await Service().MeasureAsync(Rule("%ROOT%\\cache"), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(Path.Combine(_root, @"cache\changed.bin"), DateTime.UtcNow.AddMinutes(5));

        var result = await Service().CleanAsync(estimate, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(Path.Combine(_root, @"cache\changed.bin")));
    }

    [Fact]
    public async Task Removes_only_folders_it_emptied()
    {
        Write(@"cache\deep\inner\a.bin", 100);
        Directory.CreateDirectory(Path.Combine(_root, @"cache\was-empty"));

        await MeasureAndClean(Rule("%ROOT%\\cache"), null);

        Assert.False(Directory.Exists(Path.Combine(_root, @"cache\deep")));
        Assert.True(Directory.Exists(Path.Combine(_root, @"cache\was-empty")));
    }

    [Fact]
    public async Task Refuses_listed_files_outside_the_rule_folders()
    {
        Write(@"cache\a.bin", 10);
        Write(@"precious\photo.jpg", 10);
        var estimate = await Service().MeasureAsync(Rule("%ROOT%\\cache"), TestContext.Current.CancellationToken);
        var photo = new FileInfo(Path.Combine(_root, @"precious\photo.jpg"));
        var tampered = estimate with { Files = [.. estimate.Files!, new CleanupFile(photo.FullName, photo.Length, photo.LastWriteTimeUtc)] };

        var result = await Service().CleanAsync(tampered, null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(photo.FullName));
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(1, result.Skipped);
    }

    [SupportedOSPlatform("windows")]
    [WindowsFact]
    public void Reads_recycle_bin_records()
    {
        var deleted = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal((@"C:\Users\Bob\notes.txt", 4096L, deleted), RecycleBin.ParseInfo(InfoV2(4096, deleted, @"C:\Users\Bob\notes.txt")));
        Assert.Equal((@"D:\old.doc", 10L, deleted), RecycleBin.ParseInfo(InfoV1(10, deleted, @"D:\old.doc")));
        Assert.Null(RecycleBin.ParseInfo(new byte[10]));
        Assert.Null(RecycleBin.ParseInfo([.. BitConverter.GetBytes(7L), .. new byte[40]]));
    }

    [Fact]
    public async Task Expands_wildcard_folders()
    {
        Write(@"profiles\Default\Cache\a.bin", 10);
        Write(@"profiles\Profile 1\Cache\b.bin", 20);
        Write(@"profiles\Profile 1\Bookmarks", 30);

        var estimate = await Service().MeasureAsync(Rule("%ROOT%\\profiles\\*\\Cache"), TestContext.Current.CancellationToken);

        Assert.Equal(30, estimate.Bytes);
    }

    [Fact]
    public async Task Ignores_patterns_with_unknown_variables()
    {
        var estimate = await Service().MeasureAsync(Rule("%NOT_SET%\\cache"), TestContext.Current.CancellationToken);

        Assert.Equal(0, estimate.Bytes);
    }

    [Fact]
    public async Task Refuses_admin_rules()
    {
        var rule = Rule("%ROOT%\\cache") with { RequiresAdmin = true };
        Write(@"cache\a.bin", 10);

        var estimate = await Service().MeasureAsync(rule, TestContext.Current.CancellationToken);

        Assert.False(estimate.IsAvailable);
        Assert.Equal(10, estimate.Bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().CleanAsync(estimate, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Resolves_absolute_folders_that_have_no_drive_letter()
    {
        string folder = Path.Combine(_root, "cache");
        Directory.CreateDirectory(folder);

        IReadOnlyList<string> resolved = CleanupTargets.Resolve([folder, folder + Path.DirectorySeparatorChar], Environment.GetEnvironmentVariable);

        Assert.Equal([Path.GetFullPath(folder)], resolved);
    }

    [Fact]
    public void Expands_a_wildcard_under_an_absolute_folder_that_has_no_drive_letter()
    {
        Directory.CreateDirectory(Path.Combine(_root, "profiles", "Default", "Cache"));
        Directory.CreateDirectory(Path.Combine(_root, "profiles", "Profile 1", "Cache"));

        IReadOnlyList<string> resolved = CleanupTargets.Resolve(
            [Path.Combine(_root, "profiles", "*", "Cache")],
            Environment.GetEnvironmentVariable);

        Assert.Equal(2, resolved.Count);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"C:\Program Files\App\cache")]
    [InlineData(@"C:\Program Files (x86)\Steam\shadercache")]
    [InlineData(@"C:\Users\Bob")]
    public void Guard_refuses_protected_places(string folder)
    {
        Assert.False(SafetyGuard.MayCleanInside(folder, Env));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"C:\Program Files\App\cache")]
    [InlineData(@"C:\Users\Bob\AppData\Local\Temp")]
    public void Guard_refuses_everything_when_the_environment_cannot_be_read(string folder)
    {
        Assert.False(SafetyGuard.MayCleanInside(folder, _ => null));
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\cache")]
    [InlineData(@"C:\Program Files (x86)\Steam\shadercache")]
    public void Guard_refuses_program_files_when_the_program_files_variable_is_missing(string folder)
    {
        // The reported gap: with only the profile and the Windows directory known, an unresolved
        // Program Files used to stop protecting itself.
        static string? WithoutProgramFiles(string name) => name switch
        {
            "SystemRoot" => @"C:\Windows",
            "USERPROFILE" => @"C:\Users\Bob",
            _ => null,
        };

        Assert.False(SafetyGuard.MayCleanInside(folder, WithoutProgramFiles));
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\cache")]
    [InlineData(@"C:\Program Files (x86)\Steam\shadercache")]
    public void Guard_refuses_program_files_when_the_x86_and_6432_variables_are_missing(string folder)
    {
        // Same environment, but Program Files is known: the x86 folder must still be derived from
        // the Windows path instead of being dropped with the missing variable.
        static string? OnlyRequired(string name) => name switch
        {
            "SystemRoot" => @"C:\Windows",
            "USERPROFILE" => @"C:\Users\Bob",
            "ProgramFiles" => @"C:\Program Files",
            _ => null,
        };

        Assert.False(SafetyGuard.MayCleanInside(folder, OnlyRequired));
    }

    [Fact]
    public void Guard_still_allows_caches_when_only_the_optional_variables_are_missing()
    {
        static string? OnlyRequired(string name) => name switch
        {
            "SystemRoot" => @"C:\Windows",
            "USERPROFILE" => @"C:\Users\Bob",
            "ProgramFiles" => @"C:\Program Files",
            _ => null,
        };

        Assert.True(SafetyGuard.MayCleanInside(@"C:\Users\Bob\AppData\Local\Temp", OnlyRequired));
    }

    [Fact]
    public void Guard_allows_caches_in_the_user_profile()
    {
        Assert.True(SafetyGuard.MayCleanInside(@"C:\Users\Bob\AppData\Local\Temp", Env));
    }

    [Fact]
    public async Task History_keeps_every_run()
    {
        var history = new CleanupHistory(Path.Combine(_root, @"logs\history.jsonl"));
        var rule = Rule("%ROOT%\\cache");
        var when = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        history.Append([new CleanupResult(rule, 100, 2, 1)], when);
        history.Append([new CleanupResult(rule, 50, 1, 0)], when.AddHours(1));
        await File.AppendAllTextAsync(Path.Combine(_root, @"logs\history.jsonl"), "garbage\n", TestContext.Current.CancellationToken);

        var entries = history.Read();

        Assert.Equal([100L, 50L], entries.Select(e => e.BytesFreed));
        Assert.Equal("test", entries[0].Items[0].RuleId);
        Assert.Equal(when, entries[0].When);
    }

    [Fact]
    public async Task Admin_rule_is_available_when_the_admin_cleaner_can_be_asked()
    {
        string folder = WriteAdminFiles(("a.bin", 10), ("sub/b.bin", 20));
        var cleaner = new FakeAdminCleaner(PrivilegedCapability.Ready);

        var estimate = await AdminService(cleaner).MeasureAsync(AdminRule(folder), TestContext.Current.CancellationToken);

        Assert.True(estimate.IsAvailable);
        Assert.Equal(PrivilegedCapability.Ready, estimate.Admin);
        Assert.Equal(30, estimate.Bytes);
        Assert.Equal(2, estimate.Items);
        Assert.Empty(cleaner.Calls);
        Assert.True(File.Exists(Path.Combine(folder, "a.bin")));
    }

    [Fact]
    public async Task Admin_rule_says_why_the_admin_cleaner_cannot_be_asked()
    {
        string folder = WriteAdminFiles(("a.bin", 10));
        var cleaner = new FakeAdminCleaner(PrivilegedCapability.Missing(PrivilegedUnavailableReason.NoAuthenticationAgent));

        var estimate = await AdminService(cleaner).MeasureAsync(AdminRule(folder), TestContext.Current.CancellationToken);

        Assert.False(estimate.IsAvailable);
        Assert.Equal(PrivilegedUnavailableReason.NoAuthenticationAgent, estimate.Admin?.Reason);
        Assert.Equal(10, estimate.Bytes);
    }

    [Fact]
    public async Task Admin_rule_is_not_offered_when_the_safety_rule_refuses_all_its_folders()
    {
        string folder = WriteAdminFiles(("a.bin", 10));
        var cleaner = new FakeAdminCleaner(PrivilegedCapability.Ready);

        var estimate = await AdminService(cleaner, mayCleanInside: _ => false).MeasureAsync(AdminRule(folder), TestContext.Current.CancellationToken);

        Assert.False(estimate.IsAvailable);
        Assert.Equal(0, estimate.Items);
        Assert.Equal(PrivilegedCapability.Ready, estimate.Admin);
    }

    [Fact]
    public async Task Admin_rule_without_an_admin_cleaner_stays_unavailable_and_is_refused()
    {
        string folder = WriteAdminFiles(("a.bin", 10));
        var service = AdminService(cleaner: null);

        var estimate = await service.MeasureAsync(AdminRule(folder), TestContext.Current.CancellationToken);

        Assert.False(estimate.IsAvailable);
        Assert.Null(estimate.Admin);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanAsync(estimate, null, TestContext.Current.CancellationToken));
        Assert.Equal("Cleanup rule 'admin-test' needs admin rights.", refused.Message);
        Assert.True(File.Exists(Path.Combine(folder, "a.bin")));
    }

    [Fact]
    public async Task Admin_rule_hands_the_previewed_files_to_the_admin_cleaner_and_returns_its_result()
    {
        string folder = WriteAdminFiles(("a.bin", 10), ("sub/b.bin", 20));
        CleanupResult? given = null;
        var cleaner = new FakeAdminCleaner(PrivilegedCapability.Ready, estimate =>
        {
            given = new CleanupResult(estimate.Rule, 25, 1, 1);
            return PrivilegedResult<CleanupResult>.Done(given);
        });
        var service = AdminService(cleaner);
        var progress = new List<double>();

        var estimate = await service.MeasureAsync(AdminRule(folder), TestContext.Current.CancellationToken);
        var result = await service.CleanAsync(estimate, new SyncProgress(progress), TestContext.Current.CancellationToken);

        Assert.Same(given, result);
        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        var forwarded = Assert.Single(cleaner.Calls);
        Assert.Equal(estimate.Files!.Select(f => f.Path).Order(), forwarded.Files!.Select(f => f.Path).Order());
        Assert.Equal(30, forwarded.Bytes);
        Assert.Equal(2, forwarded.Items);
        var forwardedFolder = Assert.Single(forwarded.Folders!);
        Assert.Equal(Path.GetFullPath(folder), forwardedFolder.Path);
        Assert.Equal(2, forwardedFolder.Files);
        Assert.Equal(1, progress[^1]);
        // Nothing is removed here, not even emptied folders: the cleaner owns all of that.
        Assert.True(Directory.Exists(Path.Combine(folder, "sub")));
        Assert.True(File.Exists(Path.Combine(folder, "sub", "b.bin")));
    }

    [Theory]
    [InlineData(PrivilegedOutcome.Cancelled, null)]
    [InlineData(PrivilegedOutcome.NotAvailable, PrivilegedUnavailableReason.HelperNotTrusted)]
    [InlineData(PrivilegedOutcome.Failed, null)]
    public async Task Admin_rule_that_did_not_finish_reports_the_outcome_and_deletes_nothing(PrivilegedOutcome outcome, PrivilegedUnavailableReason? reason)
    {
        string folder = WriteAdminFiles(("a.bin", 10), ("b.bin", 20));
        var cleaner = new FakeAdminCleaner(PrivilegedCapability.Ready, _ => new PrivilegedResult<CleanupResult>(outcome, Reason: reason));
        var service = AdminService(cleaner);
        var progress = new List<double>();

        var estimate = await service.MeasureAsync(AdminRule(folder), TestContext.Current.CancellationToken);
        var result = await service.CleanAsync(estimate, new SyncProgress(progress), TestContext.Current.CancellationToken);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(0, result.BytesFreed);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(1, progress[^1]);
        Assert.True(File.Exists(Path.Combine(folder, "a.bin")));
        Assert.True(File.Exists(Path.Combine(folder, "b.bin")));
    }

    [Fact]
    public async Task Admin_rule_checks_the_capability_again_before_asking()
    {
        string folder = WriteAdminFiles(("a.bin", 10));
        var cleaner = new FakeAdminCleaner(PrivilegedCapability.Ready);
        var service = AdminService(cleaner);
        var estimate = await service.MeasureAsync(AdminRule(folder), TestContext.Current.CancellationToken);

        cleaner.Capability = PrivilegedCapability.Missing(PrivilegedUnavailableReason.NoPkexec);
        var result = await service.CleanAsync(estimate, null, TestContext.Current.CancellationToken);

        Assert.Equal(PrivilegedOutcome.NotAvailable, result.Outcome);
        Assert.Equal(PrivilegedUnavailableReason.NoPkexec, result.Reason);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(cleaner.Calls);
    }

    [Fact]
    public async Task Admin_rule_never_forwards_files_outside_the_rule_folders()
    {
        string folder = WriteAdminFiles(("a.bin", 10));
        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "precious.bin"), new byte[5]);
        var cleaner = new FakeAdminCleaner(PrivilegedCapability.Ready, estimate =>
            PrivilegedResult<CleanupResult>.Done(new CleanupResult(estimate.Rule, estimate.Bytes, estimate.Items, 0)));
        var service = AdminService(cleaner);
        var measured = await service.MeasureAsync(AdminRule(folder), TestContext.Current.CancellationToken);
        var date = DateTime.UtcNow;
        var estimate = measured with
        {
            Files =
            [
                .. measured.Files!,
                new CleanupFile(Path.Combine(other, "precious.bin"), 5, date),
                new CleanupFile(Path.Combine(folder, "..", "other", "precious.bin"), 5, date),
                new CleanupFile(folder, 1, date),
                new CleanupFile(folder + "-sibling" + Path.DirectorySeparatorChar + "x.bin", 1, date),
            ],
        };

        var result = await service.CleanAsync(estimate, null, TestContext.Current.CancellationToken);

        var forwarded = Assert.Single(cleaner.Calls);
        Assert.Equal([Path.Combine(folder, "a.bin")], forwarded.Files!.Select(f => f.Path));
        Assert.Equal(10, forwarded.Bytes);
        Assert.Equal(1, forwarded.Items);
        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        Assert.Equal(4, result.Skipped);
        Assert.True(File.Exists(Path.Combine(other, "precious.bin")));
    }

    [Fact]
    public async Task Admin_rule_with_only_foreign_files_does_not_ask_at_all()
    {
        string folder = WriteAdminFiles(("a.bin", 10));
        var cleaner = new FakeAdminCleaner(PrivilegedCapability.Ready);
        var service = AdminService(cleaner);
        var measured = await service.MeasureAsync(AdminRule(folder), TestContext.Current.CancellationToken);
        var estimate = measured with { Files = [new CleanupFile(Path.Combine(_root, "elsewhere.bin"), 5, DateTime.UtcNow)] };

        var result = await service.CleanAsync(estimate, null, TestContext.Current.CancellationToken);

        Assert.Empty(cleaner.Calls);
        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task Rules_that_need_no_admin_ignore_the_admin_cleaner()
    {
        string folder = Path.Combine(_root, "plain");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[10]);
        File.WriteAllBytes(Path.Combine(folder, "sub", "b.bin"), new byte[20]);
        var cleaner = new FakeAdminCleaner(PrivilegedCapability.Ready);
        var service = AdminService(cleaner);
        var rule = AdminRule(folder) with { RequiresAdmin = false };

        var estimate = await service.MeasureAsync(rule, TestContext.Current.CancellationToken);
        var result = await service.CleanAsync(estimate, null, TestContext.Current.CancellationToken);

        Assert.True(estimate.IsAvailable);
        Assert.Null(estimate.Admin);
        Assert.Equal(PrivilegedOutcome.Done, result.Outcome);
        Assert.Equal(30, result.BytesFreed);
        Assert.Equal(2, result.FilesDeleted);
        Assert.False(Directory.Exists(Path.Combine(folder, "sub")));
        Assert.Empty(cleaner.Calls);
    }

    private static string? Env(string name) => name switch
    {
        "SystemRoot" => @"C:\Windows",
        "ProgramFiles" => @"C:\Program Files",
        "ProgramFiles(x86)" => @"C:\Program Files (x86)",
        "USERPROFILE" => @"C:\Users\Bob",
        _ => null,
    };

    private async Task<CleanupResult> MeasureAndClean(CleanupRule rule, IProgress<double>? progress)
    {
        var estimate = await Service().MeasureAsync(rule, TestContext.Current.CancellationToken);
        return await Service().CleanAsync(estimate, progress, TestContext.Current.CancellationToken);
    }

    private static byte[] InfoV2(long size, DateTime deleted, string path) =>
    [
        .. BitConverter.GetBytes(2L), .. BitConverter.GetBytes(size), .. BitConverter.GetBytes(deleted.ToFileTimeUtc()),
        .. BitConverter.GetBytes(path.Length + 1), .. Encoding.Unicode.GetBytes(path + "\0"),
    ];

    private static byte[] InfoV1(long size, DateTime deleted, string path)
    {
        byte[] name = new byte[520];
        Encoding.Unicode.GetBytes(path).CopyTo(name, 0);
        return [.. BitConverter.GetBytes(1L), .. BitConverter.GetBytes(size), .. BitConverter.GetBytes(deleted.ToFileTimeUtc()), .. name];
    }

    private CleanupService Service() => new([], name => name == "ROOT" ? _root : Environment.GetEnvironmentVariable(name));

    private static CleanupRule Rule(params string[] paths) => new(
        "test", SafetyLevel.Safe, CleanupKind.FolderContents, paths, RequiresAdmin: false,
        new("Test", "Teste"), new("", ""), new("", ""));

    private void Write(string relativePath, int length)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    private void CreateJunction(string link, string target)
    {
        _junctions.Add(link);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private CleanupService AdminService(IAdminCleaner? cleaner, Func<string, bool>? mayCleanInside = null) => new(
        [],
        environment: new CleanupEnvironment(Environment.GetEnvironmentVariable, mayCleanInside ?? (_ => true), StringComparer.OrdinalIgnoreCase, AdminCleaner: cleaner));

    private static CleanupRule AdminRule(string folder) => new(
        "admin-test", SafetyLevel.UsuallySafe, CleanupKind.FolderContents, [folder], RequiresAdmin: true,
        new("Test", "Teste"), new("", ""), new("", ""));

    private string WriteAdminFiles(params (string RelativePath, int Length)[] files)
    {
        string folder = Path.Combine(_root, "system-cache");
        foreach ((string relativePath, int length) in files)
        {
            string path = Path.Combine(folder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[length]);
        }

        return folder;
    }

    private sealed class FakeAdminCleaner(
        PrivilegedCapability capability,
        Func<CleanupEstimate, PrivilegedResult<CleanupResult>>? clean = null) : IAdminCleaner
    {
        public List<CleanupEstimate> Calls { get; } = [];

        public PrivilegedCapability Capability { get; set; } = capability;

        public Task<PrivilegedResult<CleanupResult>> CleanAsync(CleanupEstimate estimate, CancellationToken cancellationToken)
        {
            Calls.Add(estimate);
            return Task.FromResult(clean?.Invoke(estimate) ?? PrivilegedResult<CleanupResult>.Done(new CleanupResult(estimate.Rule, estimate.Bytes, estimate.Items, 0)));
        }
    }

    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value)
        {
            lock (values)
            {
                values.Add(value);
            }
        }
    }
}
