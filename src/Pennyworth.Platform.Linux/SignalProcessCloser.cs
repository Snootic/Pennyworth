using System.Diagnostics;
using System.Runtime.InteropServices;
using Pennyworth.Monitoring;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Ends processes with signals: SIGTERM (the polite request, which a program can handle to save its
/// work) and SIGKILL. A process that ignores SIGTERM makes <see cref="CloseAsync"/> report false.
/// </summary>
/// <remarks>
/// A pid is only a number: if the process exits and the kernel hands its number to an unrelated
/// process before the signal lands, that process is signalled instead. The window is tiny, so callers
/// must pass pids they sampled moments ago, never ones kept from an earlier session. Pids 0 and 1 (a
/// whole process group, init) and the current process are never signalled.
/// </remarks>
public sealed partial class SignalProcessCloser : IProcessCloser
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;
    private const int Esrch = 3;
    private const int PollInterval = 50;

    internal enum SignalOutcome
    {
        /// <summary>The signal was delivered.</summary>
        Sent,

        /// <summary>There is no such process: it is already gone.</summary>
        NoSuchProcess,

        /// <summary>The process exists but belongs to someone else.</summary>
        Denied,
    }

    private readonly Func<int, int, SignalOutcome> _send;
    private readonly Func<int, bool> _isAlive;
    private readonly int _ownPid;

    public SignalProcessCloser()
        : this(SendSignal, IsAlive, Environment.ProcessId)
    {
    }

    /// <param name="send">Sends signal number (second argument) to a pid.</param>
    /// <param name="isAlive">True while the pid is a running process (a zombie is not).</param>
    /// <param name="ownPid">The pid that must never be signalled.</param>
    internal SignalProcessCloser(Func<int, int, SignalOutcome> send, Func<int, bool> isAlive, int ownPid)
    {
        _send = send;
        _isAlive = isAlive;
        _ownPid = ownPid;
    }

    /// <summary>
    /// Sends SIGTERM to every pid, then polls until they are all gone or <paramref name="timeout"/> passes.
    /// False when one is still running, refused the signal (not ours) or was never signalled (pid 1, ours).
    /// </summary>
    public async Task<bool> CloseAsync(IReadOnlyList<int> pids, TimeSpan timeout, CancellationToken cancellationToken)
    {
        (List<int> signalled, bool allAccepted) = Signal(pids, Sigterm);

        var deadline = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!signalled.Any(_isAlive))
            {
                return allAccepted;
            }

            if (deadline.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    public void ForceClose(IReadOnlyList<int> pids) => Signal(pids, Sigkill);

    /// <returns>The pids that may be waited on, and whether none was refused.</returns>
    private (List<int> Signalled, bool AllAccepted) Signal(IReadOnlyList<int> pids, int signal)
    {
        var signalled = new List<int>(pids.Count);
        bool allAccepted = true;
        foreach (int pid in pids)
        {
            if (pid <= 1 || pid == _ownPid)
            {
                allAccepted = false;
                continue;
            }

            switch (_send(pid, signal))
            {
                case SignalOutcome.Sent:
                    signalled.Add(pid);
                    break;
                case SignalOutcome.Denied:
                    allAccepted = false;
                    break;
            }
        }

        return (signalled, allAccepted);
    }

    private static SignalOutcome SendSignal(int pid, int signal)
    {
        if (Kill(pid, signal) == 0)
        {
            return SignalOutcome.Sent;
        }

        return Marshal.GetLastPInvokeError() == Esrch ? SignalOutcome.NoSuchProcess : SignalOutcome.Denied;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            return !IsZombie(File.ReadAllText($"/proc/{pid}/stat"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // No /proc/<pid>: it has exited.
        }
    }

    /// <summary>
    /// A zombie has exited and only waits for its parent to collect it. The state is the character after
    /// the last <c>)</c>: the command name before it may itself contain parentheses and spaces.
    /// </summary>
    internal static bool IsZombie(string stat)
    {
        int end = stat.LastIndexOf(')');
        return end >= 0 && end + 2 < stat.Length && stat[end + 2] is 'Z' or 'X' && stat[end + 1] == ' ';
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
