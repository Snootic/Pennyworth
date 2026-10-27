using System.Diagnostics;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>The process runner behind pkexec, against ordinary programs (the real pkexec needs a person).</summary>
public class ElevatedRunnerTests
{
    private static Task<ElevatedRun> Run(string file, TimeSpan timeout, CancellationToken token, params string[] arguments) =>
        ElevatedRunner.RunAsync(file, arguments, null, ElevatedRunner.MaxOutput, timeout, token);

    private static Task<ElevatedRun> RunWith(string file, byte[]? input, int maxOutput, params string[] arguments) =>
        ElevatedRunner.RunAsync(file, arguments, input, maxOutput, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

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

    [LinuxFact]
    public async Task The_limit_is_the_callers_to_set()
    {
        ElevatedRun small = await RunWith("/bin/sh", null, 1000, "-c", "head -c 5000 /dev/zero");
        ElevatedRun large = await RunWith("/bin/sh", null, 8 * 1024 * 1024, "-c", "head -c 5000000 /dev/zero");

        Assert.Equal(ElevatedStatus.TooLarge, small.Status);
        Assert.Equal((ElevatedStatus.Exited, 5_000_000), (large.Status, large.Output.Length));
    }

    [LinuxFact]
    public async Task The_input_reaches_standard_input_and_is_then_closed()
    {
        byte[] input = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

        ElevatedRun run = await RunWith("/bin/cat", input, ElevatedRunner.MaxOutput);

        Assert.Equal((ElevatedStatus.Exited, 0), (run.Status, run.ExitCode)); // cat ends only when its input is closed
        Assert.Equal(input, run.Output);
    }

    [LinuxFact]
    public async Task An_input_far_larger_than_a_pipe_is_written_while_the_output_is_read()
    {
        byte[] input = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(input);

        ElevatedRun run = await RunWith("/bin/cat", input, 8 * 1024 * 1024);

        Assert.Equal(ElevatedStatus.Exited, run.Status);
        Assert.Equal(input, run.Output);
    }

    [LinuxFact]
    public async Task A_program_that_exits_without_reading_its_input_is_not_an_error()
    {
        byte[] input = new byte[5 * 1024 * 1024];

        ElevatedRun run = await RunWith("/bin/sh", input, ElevatedRunner.MaxOutput, "-c", "exit 126");

        Assert.Equal((ElevatedStatus.Exited, 126), (run.Status, run.ExitCode));
    }

    [LinuxFact]
    public async Task A_request_over_the_limit_is_refused_before_anything_starts()
    {
        byte[] input = new byte[HelperProtocol.MaxRequestBytes + 1];

        ElevatedRun run = await RunWith("/nonexistent/pennyworth-pkexec", input, ElevatedRunner.MaxOutput);

        Assert.Equal(ElevatedStatus.TooLarge, run.Status); // not NotStarted: the program was never looked for
    }

    [LinuxFact]
    public async Task Standard_error_is_kept_as_text()
    {
        ElevatedRun run = await RunWith("/bin/sh", null, ElevatedRunner.MaxOutput, "-c", "echo 'pennyworth-helper: refused' >&2; exit 3");

        Assert.Equal((ElevatedStatus.Exited, 3), (run.Status, run.ExitCode));
        Assert.Equal("pennyworth-helper: refused\n", run.Error);
        Assert.Empty(run.Output);
    }

    [LinuxFact]
    public async Task Only_the_first_8_KiB_of_standard_error_is_kept_and_the_program_still_finishes()
    {
        ElevatedRun run = await RunWith("/bin/sh", null, ElevatedRunner.MaxOutput, "-c", "head -c 300000 /dev/zero | tr '\\000' x >&2; exit 2");

        Assert.Equal((ElevatedStatus.Exited, 2), (run.Status, run.ExitCode));
        Assert.Equal(ElevatedRunner.MaxErrorBytes, run.Error.Length);
        Assert.All(run.Error, c => Assert.Equal('x', c));
    }

    [LinuxFact]
    public async Task Standard_error_that_is_not_UTF8_is_decoded_leniently()
    {
        ElevatedRun run = await RunWith("/bin/sh", null, ElevatedRunner.MaxOutput, "-c", "printf 'a\\377b' >&2");

        Assert.Equal("a\uFFFDb", run.Error);
    }

    [LinuxFact]
    public async Task No_standard_error_is_an_empty_string()
    {
        ElevatedRun run = await Run("/bin/true", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("", run.Error);
    }
}
