using System.Diagnostics;
using Pennyworth.Platform.Linux;
using Pennyworth.Tests;
using static Pennyworth.Platform.Linux.SignalProcessCloser;

namespace Pennyworth.Platform.Linux.Tests;

public class SignalProcessCloserTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(600);

    [Theory]
    [InlineData("1234 (sleep) S 1 1234 1234", false)]
    [InlineData("1234 (sleep) Z 1 1234 1234", true)]
    [InlineData("1234 (a) Z (b) R 1 1", false)]
    [InlineData("1234 (we ird) ) Z 1 1", true)]
    [InlineData("1234 (Z) R 1", false)]
    [InlineData("", false)]
    [InlineData("1234 (x", false)]
    public void Zombie_state_is_read_after_the_last_parenthesis(string stat, bool zombie)
    {
        Assert.Equal(zombie, IsZombie(stat));
    }

    [Fact]
    public async Task Pid_1_pid_0_negative_and_own_pid_are_never_signalled()
    {
        var sent = new List<(int Pid, int Signal)>();
        var closer = new SignalProcessCloser((p, s) => { sent.Add((p, s)); return SignalOutcome.Sent; }, _ => false, ownPid: 4242);

        bool closed = await closer.CloseAsync([1, 0, -5, 4242], Short, CancellationToken.None);
        closer.ForceClose([1, 0, -5, 4242]);

        Assert.Empty(sent);
        Assert.False(closed);
    }

    [Fact]
    public async Task Sends_sigterm_then_sigkill()
    {
        var sent = new List<(int Pid, int Signal)>();
        var closer = new SignalProcessCloser((p, s) => { sent.Add((p, s)); return SignalOutcome.Sent; }, _ => false, ownPid: 4242);

        Assert.True(await closer.CloseAsync([100, 200], Short, CancellationToken.None));
        closer.ForceClose([100]);

        Assert.Equal([(100, 15), (200, 15), (100, 9)], sent);
    }

    [Fact]
    public async Task An_already_exited_pid_counts_as_closed()
    {
        var closer = new SignalProcessCloser((_, _) => SignalOutcome.NoSuchProcess, _ => false, ownPid: 4242);

        Assert.True(await closer.CloseAsync([100], Short, CancellationToken.None));
        closer.ForceClose([100]);
    }

    [Fact]
    public async Task A_denied_signal_is_not_closed_and_does_not_throw()
    {
        var closer = new SignalProcessCloser((_, _) => SignalOutcome.Denied, _ => true, ownPid: 4242);

        Assert.False(await closer.CloseAsync([100], Short, CancellationToken.None));
        closer.ForceClose([100]);
    }

    [Fact]
    public async Task One_denied_pid_makes_the_whole_close_false_even_if_the_others_exit()
    {
        var closer = new SignalProcessCloser((p, _) => p == 100 ? SignalOutcome.Denied : SignalOutcome.Sent, _ => false, ownPid: 4242);

        Assert.False(await closer.CloseAsync([100, 200], Short, CancellationToken.None));
    }

    [Fact]
    public async Task Waits_until_the_last_process_is_gone()
    {
        var polls = 0;
        var closer = new SignalProcessCloser((_, _) => SignalOutcome.Sent, _ => ++polls < 4, ownPid: 4242);

        Assert.True(await closer.CloseAsync([100], TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(4, polls);
    }

    [Fact]
    public async Task A_process_that_stays_alive_times_out_as_false()
    {
        var closer = new SignalProcessCloser((_, _) => SignalOutcome.Sent, _ => true, ownPid: 4242);
        var clock = Stopwatch.StartNew();

        Assert.False(await closer.CloseAsync([100], Short, CancellationToken.None));
        Assert.InRange(clock.Elapsed, Short, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_stops_the_wait()
    {
        var closer = new SignalProcessCloser((_, _) => SignalOutcome.Sent, _ => true, ownPid: 4242);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => closer.CloseAsync([100], TimeSpan.FromSeconds(30), cts.Token));
    }

    [LinuxFact]
    public async Task A_normal_child_is_closed_by_sigterm()
    {
        using Process child = Spawn("sleep 30");
        try
        {
            Assert.True(await new SignalProcessCloser().CloseAsync([child.Id], TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.True(child.WaitForExit(5000));
        }
        finally
        {
            Reap(child);
        }
    }

    [LinuxFact]
    public async Task A_child_that_ignores_sigterm_is_not_closed_but_dies_on_force_close()
    {
        using Process child = Spawn("trap '' TERM; sleep 30");
        try
        {
            var closer = new SignalProcessCloser();
            await Task.Delay(300); // Let sh install the trap before the signal arrives.

            Assert.False(await closer.CloseAsync([child.Id], Short, CancellationToken.None));
            Assert.False(child.HasExited);

            closer.ForceClose([child.Id]);
            Assert.True(child.WaitForExit(5000));
            Assert.True(await closer.CloseAsync([child.Id], Short, CancellationToken.None));
        }
        finally
        {
            Reap(child);
        }
    }

    [LinuxFact]
    public async Task A_pid_that_never_existed_is_closed_without_throwing()
    {
        var closer = new SignalProcessCloser();
        int unused = 4_194_000;

        Assert.True(await closer.CloseAsync([unused], Short, CancellationToken.None));
        closer.ForceClose([unused]);
    }

    [LinuxFact]
    public async Task A_process_of_another_user_is_refused_not_thrown()
    {
        // pid 2 (kthreadd) is root's and not ours; the real kill() answers EPERM.
        if (Environment.UserName == "root" || !File.Exists("/proc/2/stat"))
        {
            return;
        }

        var closer = new SignalProcessCloser();
        Assert.False(await closer.CloseAsync([2], Short, CancellationToken.None));
    }

    private static Process Spawn(string script)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        return Process.Start(start)!;
    }

    private static void Reap(Process child)
    {
        if (!child.HasExited)
        {
            child.Kill(entireProcessTree: true);
        }
    }
}
