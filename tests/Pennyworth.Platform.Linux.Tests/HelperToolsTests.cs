using System.Diagnostics;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public class HelperToolsTests
{
    private static string? Run(string tool, string[] arguments, Func<string, TrustVerdict>? trust = null, int timeoutMs = 20_000, int maxBytes = 1 << 20, CancellationToken cancellationToken = default) =>
        HelperTools.Run([tool], arguments, trust ?? (_ => TrustVerdict.Trusted), TimeSpan.FromMilliseconds(timeoutMs), maxBytes, cancellationToken);

    [Fact]
    public void TheBtrfsToolIsOnlyEverTakenFromTheFixedAbsolutePaths()
    {
        Assert.Equal(["/usr/bin/btrfs", "/usr/sbin/btrfs", "/sbin/btrfs", "/bin/btrfs"], HelperTools.PathsOf("btrfs"));
        Assert.All(HelperTools.PathsOf("btrfs"), path => Assert.StartsWith("/", path));
        Assert.Empty(HelperTools.PathsOf("sh"));
        Assert.Empty(HelperTools.PathsOf("/bin/sh"));
        Assert.Empty(HelperTools.PathsOf(""));
    }

    [Fact]
    public void TheLimitsAreTheDocumentedOnes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), HelperTools.CommandTimeout);
        Assert.Equal(16 * 1024 * 1024, HelperTools.MaxOutputBytes);
    }

    [LinuxFact]
    public void TheChildGetsNoEnvironmentButLcAllC()
    {
        Environment.SetEnvironmentVariable("PW_HELPER_TEST_SECRET", "leak");
        try
        {
            Assert.Equal("LC_ALL=C\n", Run("/usr/bin/env", []));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PW_HELPER_TEST_SECRET", null);
        }
    }

    [LinuxFact]
    public void ArgumentsAreHandedOverVerbatimWithoutAShell()
    {
        Assert.Equal("a b;touch x $HOME *\n", Run("/usr/bin/echo", ["a b;touch x $HOME *"]));
    }

    [LinuxFact]
    public void TheChildSeesAClosedStandardInput()
    {
        // cat would wait for input forever if stdin stayed open.
        Assert.Equal("", Run("/usr/bin/cat", [], timeoutMs: 20_000));
    }

    [LinuxFact]
    public void ANonZeroExitIsNull()
    {
        Assert.Null(Run("/usr/bin/false", []));
    }

    [LinuxFact]
    public void ATimeoutKillsTheChildAndIsNull()
    {
        var clock = Stopwatch.StartNew();

        string? output = Run("/usr/bin/sleep", ["60"], timeoutMs: 300);

        Assert.Null(output);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "the child was not killed");
    }

    [LinuxFact]
    public void TooMuchOutputKillsTheChildAndIsNull()
    {
        var clock = Stopwatch.StartNew();

        string? output = Run("/usr/bin/yes", [], maxBytes: 10_000);

        Assert.Null(output);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "the child was not killed");
    }

    [LinuxFact]
    public void OutputExactlyAtTheLimitIsKept()
    {
        Assert.Equal("12345", Run("/usr/bin/printf", ["12345"], maxBytes: 5));
    }

    [LinuxFact]
    public void CancellationKillsTheChildAndThrows()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();

        Assert.Throws<OperationCanceledException>(() => Run("/usr/bin/sleep", ["60"], cancellationToken: cts.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "the child was not killed");
    }

    [LinuxFact]
    public void AToolTheTrustCheckRejectsIsNeverStarted()
    {
        string marker = Path.Combine(Path.GetTempPath(), "pw-helper-tool-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(Run("/usr/bin/touch", [marker], trust: _ => TrustVerdict.NotTrusted));
            Assert.Null(Run("/usr/bin/touch", [marker], trust: _ => TrustVerdict.Missing));
            Assert.False(File.Exists(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [LinuxFact]
    public void TheFirstTrustedCandidateRuns()
    {
        string? output = HelperTools.Run(
            ["/nonexistent/echo", "/usr/bin/echo", "/usr/bin/false"],
            ["x"],
            path => path == "/nonexistent/echo" ? TrustVerdict.Missing : TrustVerdict.Trusted,
            TimeSpan.FromSeconds(20),
            1 << 20,
            CancellationToken.None);

        Assert.Equal("x\n", output);
    }

    [LinuxFact]
    public void ATrustedPathThatCannotStartIsNull()
    {
        Assert.Null(Run("/nonexistent/tool", []));
    }

    [LinuxFact]
    public void TheProductionRunnerDoesNotStartAnUnknownTool()
    {
        Assert.Null(HelperTools.Run("sh", ["-c", "echo pwned"], CancellationToken.None));
    }
}
