using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Pennyworth.Platform.Linux;

internal enum ElevatedStatus
{
    /// <summary>The program ran to the end; <see cref="ElevatedRun.ExitCode"/> says how it went.</summary>
    Exited,

    /// <summary>The program could not be started.</summary>
    NotStarted,

    /// <summary>It was still running when the time ran out, and was killed.</summary>
    TimedOut,

    /// <summary>It printed more than the allowed number of bytes, or the request to write to it was larger than allowed, and it was killed or never started.</summary>
    TooLarge,
}

/// <param name="Output">Raw standard output.</param>
/// <param name="Error">The first <see cref="ElevatedRunner.MaxErrorBytes"/> bytes of standard error, decoded as UTF-8 (invalid bytes become U+FFFD); empty when there were none. Only for the one-line reason, never parsed.</param>
internal readonly record struct ElevatedRun(ElevatedStatus Status, int ExitCode, byte[] Output, string Error = "")
{
    public static ElevatedRun Failed(ElevatedStatus status) => new(status, -1, []);
}

/// <summary>
/// Runs a program and returns its exit code, raw standard output and the start of its standard error; the seam tests replace.
/// <paramref name="input"/> is written to its standard input, which is then closed (null: closed at once);
/// more than <paramref name="maxOutput"/> bytes of output end the run with <see cref="ElevatedStatus.TooLarge"/>.
/// </summary>
internal delegate Task<ElevatedRun> ElevatedRunnerFunc(string fileName, IReadOnlyList<string> arguments, byte[]? input, int maxOutput, TimeSpan timeout, CancellationToken cancellationToken);

/// <summary>
/// Runs <c>pkexec</c> and what it elevates. Unlike <see cref="ProcessProbe"/> it returns the exit code
/// (pkexec says "dismissed" and "not authorised" through it), keeps standard output as bytes (the firmware
/// table is binary), passes a request on standard input (the helper reads its JSON there), keeps the start of
/// standard error (the helper's one-line reason), and waits as long as a person needs to type a password.
/// Cancelling kills <c>pkexec</c>, which makes the desktop's authentication dialog go away.
/// </summary>
internal static class ElevatedRunner
{
    /// <summary>The whole SMBIOS table is a few tens of kilobytes and the text of a system tool is smaller still; anything near this is not it. The helper's JSON answers have their own, larger limit (<see cref="HelperProtocol.MaxResponseBytes"/>).</summary>
    public const int MaxOutput = HelperProtocol.MaxFirmwareTableBytes;

    /// <summary>How much of standard error is kept; a reason is one line.</summary>
    public const int MaxErrorBytes = 8 * 1024;

    private static readonly TimeSpan PipeDrain = TimeSpan.FromSeconds(5);

    public static async Task<ElevatedRun> RunAsync(string fileName, IReadOnlyList<string> arguments, byte[]? input, int maxOutput, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (input is { Length: > HelperProtocol.MaxRequestBytes })
        {
            return ElevatedRun.Failed(ElevatedStatus.TooLarge); // The helper would refuse it; do not even ask for a password.
        }

        // Nothing is passed in the environment or on the command line but the program and its fixed arguments.
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { ["LC_ALL"] = "C" },
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ElevatedRun.Failed(ElevatedStatus.NotStarted);
        }

        if (process is null)
        {
            return ElevatedRun.Failed(ElevatedStatus.NotStarted);
        }

        using (process)
        using (cancellationToken.Register(static state => Kill((Process)state!), process))
        {
            var output = new MemoryStream();
            var error = new MemoryStream();
            Task<bool> read = ReadLimited(process, process.StandardOutput.BaseStream, output, maxOutput);
            Task keep = KeepStart(process.StandardError.BaseStream, error);
            Task write = Feed(process, input); // Closes standard input at the end, so a program that waits for more must not hang.

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                cancellationToken.ThrowIfCancellationRequested();
                return ElevatedRun.Failed(ElevatedStatus.TimedOut);
            }

            // The program is gone, but a child can hold a pipe open: never wait on one forever.
            Task pipes = Task.WhenAll(read, keep, write);
            if (await Task.WhenAny(pipes, Task.Delay(PipeDrain, CancellationToken.None)) != pipes)
            {
                return ElevatedRun.Failed(ElevatedStatus.TimedOut);
            }

            return read.Result
                ? new ElevatedRun(ElevatedStatus.Exited, process.ExitCode, output.ToArray(), Encoding.UTF8.GetString(error.GetBuffer(), 0, (int)error.Length))
                : ElevatedRun.Failed(ElevatedStatus.TooLarge);
        }
    }

    /// <summary>
    /// Writes the request and closes standard input. A program that exits without reading it (a dismissed prompt) makes
    /// the write fail with a broken pipe: that is its answer, not an error here.
    /// </summary>
    private static async Task Feed(Process process, byte[]? input)
    {
        try
        {
            Stream stdin = process.StandardInput.BaseStream;
            if (input is { Length: > 0 })
            {
                await stdin.WriteAsync(input);
                await stdin.FlushAsync();
            }

            stdin.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The program is gone or closed its input.
        }
    }

    /// <summary>Keeps the first <see cref="MaxErrorBytes"/> bytes and drains the rest, so the program never blocks on a full pipe.</summary>
    private static async Task KeepStart(Stream input, MemoryStream kept)
    {
        var buffer = new byte[4 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer)) > 0)
        {
            int room = MaxErrorBytes - (int)kept.Length;
            if (room > 0)
            {
                kept.Write(buffer, 0, Math.Min(room, count));
            }
        }
    }

    /// <summary>Copies the stream to <paramref name="output"/>; false, with the program killed, when it prints more than <paramref name="limit"/>.</summary>
    private static async Task<bool> ReadLimited(Process process, Stream input, MemoryStream output, int limit)
    {
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer)) > 0)
        {
            if (output.Length + count > limit)
            {
                Kill(process);
                return false;
            }

            output.Write(buffer, 0, count);
        }

        return true;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It exited between the decision and the kill.
        }
    }
}
