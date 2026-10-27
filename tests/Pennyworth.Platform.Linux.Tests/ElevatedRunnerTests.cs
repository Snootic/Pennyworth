using System.Diagnostics;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>The process runner behind pkexec, against ordinary programs (the real pkexec needs a person).</summary>
public class ElevatedRunnerTests
{
    private static Task<ElevatedRun> Run(string file, TimeSpan timeout, CancellationToken token, params string[] arguments) =>
        ElevatedRunner.RunAsync(file, arguments, timeout, token);

    [LinuxFact]
    public async Task Returns_the_exit_code_and_the_raw_bytes()
    {
        ElevatedRun run = await Run("/bin/sh", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken, "-c", "printf '\\000\\377abc'; exit 126");

        Assert.Equal(ElevatedStatus.Exited, run.Status);
        Assert.Equal(126, run.ExitCode);
        Assert.Equal([0, 255, (byte)'a', (byte)'b', (byte)'c'], run.Output);
    }

    [LinuxFact]
    public async Task Fixes_the_locale()
    {
        ElevatedRun run = await Run("/bin/sh", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken, "-c", "printf %s \"$LC_ALL\"");

        Assert.Equal("C", System.Text.Encoding.ASCII.GetString(run.Output));
    }

    [LinuxFact]
    public async Task A_program_that_is_not_installed_did_not_start()
    {
        ElevatedRun run = await Run("/nonexistent/pennyworth-pkexec", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(ElevatedStatus.NotStarted, run.Status);
    }

    [LinuxFact]
    public async Task A_program_that_waits_for_input_does_not_hang()
    {
        ElevatedRun run = await Run("/bin/cat", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal((ElevatedStatus.Exited, 0), (run.Status, run.ExitCode));
        Assert.Empty(run.Output);
    }

    [LinuxFact]
    public async Task A_program_that_outlives_the_timeout_is_killed()
    {
        var clock = Stopwatch.StartNew();

        ElevatedRun run = await Run("/bin/sleep", TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken, "30");

        Assert.Equal(ElevatedStatus.TimedOut, run.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed}");
    }

    [LinuxFact]
    public async Task Cancelling_kills_the_program_at_once()
    {
        using var source = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        Task<ElevatedRun> pending = Run("/bin/sleep", TimeSpan.FromSeconds(60), source.Token, "30");

        await Task.Delay(200, TestContext.Current.CancellationToken);
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed}");
    }

    [LinuxFact]
    public async Task A_token_cancelled_before_the_start_starts_nothing()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run("/bin/true", TimeSpan.FromSeconds(10), source.Token));
    }

    [LinuxFact]
    public async Task Output_past_the_limit_is_cut_off_and_the_program_killed()
    {
        ElevatedRun run = await Run("/bin/sh", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken, "-c", "head -c 8000000 /dev/zero");

        Assert.Equal(ElevatedStatus.TooLarge, run.Status);
        Assert.Empty(run.Output);
    }
}
