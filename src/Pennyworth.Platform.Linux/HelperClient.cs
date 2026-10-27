using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Pennyworth.Core.Privilege;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Speaks <see cref="HelperProtocol"/> to the root helper through <c>pkexec</c>: builds the one fixed command line, hands
/// the request over on standard input, and turns what came back (pkexec's exit code, the helper's exit code, its one
/// line on standard error, its JSON) into a <see cref="PrivilegedResult{T}"/>. It runs only what it is given and
/// never decides whether the helper may be trusted: <see cref="HelperTrust"/> does, before every call.
/// </summary>
/// <remarks>
/// A dismissed or refused prompt settles the call (126 and 127 are pkexec's, never the helper's); nothing here asks twice.
/// </remarks>
internal sealed class HelperClient(ElevatedRunnerFunc run, TimeSpan promptTimeout)
{
    /// <summary>pkexec's exit code when the authentication dialog was dismissed.</summary>
    private const int Dismissed = 126;

    /// <summary>pkexec's exit code when the person is not authorised, or no authentication agent could ask.</summary>
    private const int NotAuthorized = 127;

    /// <summary>Added to the helper's own run limit so the helper stops itself (and says why) before the app gives up on it.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);

    private const int MaxDetail = 300;

    /// <summary>
    /// Runs <c>pkexec &lt;helper&gt; &lt;subcommand&gt; [argument]</c> with <paramref name="input"/> on standard input and
    /// <paramref name="read"/>s its standard output when it succeeded.
    /// </summary>
    /// <param name="longRunning">
    /// The helper may work for up to <see cref="HelperProtocol.MaxRuntime"/> after the password was typed (folders, cleanup);
    /// the wait is that long on top of the prompt's. The others answer at once and keep the prompt's wait alone.
    /// </param>
    public async Task<PrivilegedResult<T>> CallAsync<T>(
        string pkexec,
        string subcommand,
        string? argument,
        byte[]? input,
        bool longRunning,
        Func<byte[], PrivilegedResult<T>> read,
        CancellationToken cancellationToken)
    {
        var program = new List<string>(3) { HelperProtocol.InstalledPath, subcommand };
        if (argument is not null)
        {
            program.Add(argument);
        }

        TimeSpan timeout = longRunning ? promptTimeout + HelperProtocol.MaxRuntime + Margin : promptTimeout;
        ElevatedRun result = await run(pkexec, program, input, HelperProtocol.MaxResponseBytes, timeout, cancellationToken);

        if (Settle(result) is { } settled)
        {
            return settled.Outcome == PrivilegedOutcome.Done ? read(settled.Value!) : settled.As<T>();
        }

        // The helper itself said no. Usage means it did not understand a subcommand this app knows: an older helper.
        return result.ExitCode == HelperExit.Usage
            ? PrivilegedResult<T>.NotAvailable(PrivilegedUnavailableReason.HelperOutdated)
            : PrivilegedResult<T>.Failed(Detail(result));
    }

    /// <summary>Runs <c>pkexec &lt;program&gt;</c> (one of the fixed system tools); the caller reads the whole <see cref="ElevatedRun"/>.</summary>
    public Task<ElevatedRun> RunToolAsync(string pkexec, IReadOnlyList<string> program, CancellationToken cancellationToken) =>
        run(pkexec, program, null, ElevatedRunner.MaxOutput, promptTimeout, cancellationToken);

    /// <summary>
    /// What a run says about asking, before anything is read from it: <see cref="PrivilegedOutcome.Done"/> with the standard
    /// output when it exited 0; a settled outcome for a dismissed or refused prompt, a program that could not start, ran too
    /// long, printed too much or was killed; and null when the program ran and exited with an ordinary failure code
    /// (1 to 125), which only the caller can interpret.
    /// </summary>
    public static PrivilegedResult<byte[]>? Settle(ElevatedRun run) => run switch
    {
        { Status: ElevatedStatus.NotStarted } => PrivilegedResult<byte[]>.NotAvailable(PrivilegedUnavailableReason.NoPkexec),
        { Status: ElevatedStatus.TimedOut } => PrivilegedResult<byte[]>.Failed("Nobody answered in time, or it ran too long."),
        { Status: ElevatedStatus.TooLarge } => PrivilegedResult<byte[]>.Failed("The answer was larger than allowed."),
        { ExitCode: Dismissed } => PrivilegedResult<byte[]>.Cancelled,
        { ExitCode: NotAuthorized } => run.Error.Contains("No authentication agent", StringComparison.OrdinalIgnoreCase)
            ? PrivilegedResult<byte[]>.NotAvailable(PrivilegedUnavailableReason.NoAuthenticationAgent)
            : PrivilegedResult<byte[]>.Cancelled,
        { ExitCode: 0 } => PrivilegedResult<byte[]>.Done(run.Output),
        { ExitCode: > NotAuthorized } => PrivilegedResult<byte[]>.Failed($"Stopped by signal {run.ExitCode - 128}."),
        _ => null,
    };

    /// <summary>The first line of what the program said on standard error, or its exit code: a short English reason for a log.</summary>
    public static string Detail(ElevatedRun run)
    {
        string line = run.Error.Split('\n', 2)[0].Trim();
        if (line.Length == 0)
        {
            return $"Exited with code {run.ExitCode}.";
        }

        return line.Length <= MaxDetail ? line : line[..MaxDetail];
    }

    /// <summary>
    /// Reads a JSON answer. A different <c>version</c> means the installed helper is not the one this app was built for
    /// (<see cref="PrivilegedUnavailableReason.HelperOutdated"/>); anything unreadable is a failure.
    /// </summary>
    public static PrivilegedResult<T> Decode<T>(byte[] output, JsonTypeInfo<T> type, Func<T, int> versionOf)
        where T : class
    {
        try
        {
            if (JsonSerializer.Deserialize(output, type) is not { } answer)
            {
                return PrivilegedResult<T>.Failed("The helper's answer was empty.");
            }

            return versionOf(answer) == HelperProtocol.Version
                ? PrivilegedResult<T>.Done(answer)
                : PrivilegedResult<T>.NotAvailable(PrivilegedUnavailableReason.HelperOutdated);
        }
        catch (JsonException)
        {
            return PrivilegedResult<T>.Failed("The helper's answer could not be read.");
        }
    }
}
