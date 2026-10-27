using System.Runtime.Versioning;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public sealed class LinuxPreflightTests
{
    [Fact]
    public void A_complete_machine_has_no_problems()
    {
        PreflightReport report = LinuxPreflight.Run(new FakeProbe());

        Assert.Empty(report.Problems);
        Assert.Null(report.Keyring);
        Assert.Empty(report.Blocking);
    }

    [Fact]
    public void A_missing_libsecret_disables_only_the_keyring_with_one_plain_sentence()
    {
        PreflightReport report = LinuxPreflight.Run(new FakeProbe { MissingLibraries = { LinuxPreflight.SecretLibrary } });

        PreflightProblem problem = Assert.Single(report.Problems);
        Assert.Equal(PreflightFeature.Keyring, problem.Feature);
        Assert.Same(problem, report.Keyring);
        Assert.Empty(report.Blocking);
        Assert.Contains("libsecret-1.so.0", problem.Message);
        Assert.Contains("install the libsecret package", problem.Message);
        Assert.DoesNotContain('\n', problem.Message);
        Assert.DoesNotContain("Exception", problem.Message);
        Assert.DoesNotContain("password", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_glib_or_a_libsecret_without_the_needed_functions_is_the_same_problem()
    {
        PreflightReport noGlib = LinuxPreflight.Run(new FakeProbe { MissingLibraries = { LinuxPreflight.GlibLibrary } });
        PreflightReport tooOld = LinuxPreflight.Run(new FakeProbe { MissingExports = { "secret_password_clearv_sync" } });

        Assert.Equal(PreflightFeature.Keyring, Assert.Single(noGlib.Problems).Feature);
        Assert.Equal(PreflightFeature.Keyring, Assert.Single(tooOld.Problems).Feature);
        Assert.Equal(LinuxPreflight.SecretLibrary, tooOld.Keyring!.Missing);
    }

    [Fact]
    public void A_libc_without_statx_blocks_storage_and_names_the_function()
    {
        PreflightReport report = LinuxPreflight.Run(new FakeProbe { MissingExports = { "statx" } });

        PreflightProblem problem = Assert.Single(report.Problems);
        Assert.Equal(PreflightFeature.Storage, problem.Feature);
        Assert.Equal("statx", problem.Missing);
        Assert.Same(problem, Assert.Single(report.Blocking));
        Assert.Null(report.Keyring);
    }

    [Fact]
    public void Without_proc_the_live_numbers_are_blocked()
    {
        PreflightReport report = LinuxPreflight.Run(new FakeProbe { MissingFiles = { "/proc/meminfo" } });

        PreflightProblem problem = Assert.Single(report.Blocking);
        Assert.Equal(PreflightFeature.Monitoring, problem.Feature);
        Assert.Equal("/proc/meminfo", problem.Missing);
    }

    [Fact]
    public void Everything_missing_reports_every_feature_once()
    {
        var probe = new FakeProbe { MissingLibraries = { LinuxPreflight.SecretLibrary, LinuxPreflight.CLibrary } };

        PreflightReport report = LinuxPreflight.Run(probe);

        Assert.Equal(
            [PreflightFeature.Keyring, PreflightFeature.Storage, PreflightFeature.Monitoring],
            report.Problems.Select(p => p.Feature));
    }

    [Fact]
    public void The_disabled_keyring_stores_nothing_and_never_asks_for_anything()
    {
        const string sentence = "API keys cannot be saved because the keyring library (libsecret-1.so.0) is missing or too old; install the libsecret package to turn the keyring on.";
        var store = new SecretServiceStore(new UnavailableKeyring(sentence));

        var ex = Assert.Throws<KeyringException>(() => store.Save("cloud-1", "sk-secret"));

        Assert.Equal(sentence, ex.Message);
        Assert.DoesNotContain("sk-secret", ex.ToString());
        Assert.Null(store.Read("cloud-1"));
        store.Delete("cloud-1");
        Assert.False(SecretServiceStore.IsAvailable(new UnavailableKeyring(sentence)));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void The_platform_hands_the_assistant_the_disabled_keyring()
    {
        PreflightProblem problem = new(PreflightFeature.Keyring, LinuxPreflight.SecretLibrary, "No keyring here, install libsecret.");
        var platform = new LinuxPlatform(new PreflightReport([problem]));

        var ex = Assert.Throws<KeyringException>(() => platform.CreateAssistant(() => 0).Keys.Save("cloud-1", "k"));

        Assert.Equal(problem.Message, ex.Message);
    }

    [LinuxFact]
    public void This_machine_has_what_scanning_and_monitoring_call_into()
    {
        PreflightReport report = LinuxPreflight.Run();

        Assert.Empty(report.Blocking);
    }

    private sealed class FakeProbe : INativeProbe
    {
        public HashSet<string> MissingLibraries { get; } = [];

        public HashSet<string> MissingExports { get; } = [];

        public HashSet<string> MissingFiles { get; } = [];

        public bool CanLoad(string library) => !MissingLibraries.Contains(library);

        public bool HasExport(string library, string function) => CanLoad(library) && !MissingExports.Contains(function);

        public bool FileExists(string path) => !MissingFiles.Contains(path);
    }
}
