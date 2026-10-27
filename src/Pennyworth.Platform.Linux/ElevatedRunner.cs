using System.ComponentModel;
using System.Diagnostics;

namespace Pennyworth.Platform.Linux;

internal enum ElevatedStatus
{
    /// <summary>The program ran to the end; <see cref="ElevatedRun.ExitCode"/> says how it went.</summary>
    Exited,

    /// <summary>The program could not be started.</summary>
    NotStarted,

    /// <summary>It was still running when the time ran out, and was killed.</summary>
    TimedOut,

    /// <summary>It printed more than <see cref="ElevatedRunner.MaxOutput"/> bytes, and was killed.</summary>
    TooLarge,
}

internal readonly record struct ElevatedRun(ElevatedStatus Status, int ExitCode, byte[] Output)
{
    public static ElevatedRun Failed(ElevatedStatus status) => new(status, -1, []);
}

/// <summary>Runs a program and returns its exit code and raw standard output; the seam tests replace.</summary>
internal delegate Task<ElevatedRun> ElevatedRunnerFunc(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);

/// <summary>
/// Runs <c>pkexec</c> and what it elevates. Unlike <see cref="ProcessProbe"/> it returns the exit code
/// (pkexec says "dismissed" and "not authorised" through it), keeps standard output as bytes (the firmware
/// table is binary), and waits as long as a person needs to type a password. Cancelling kills <c>pkexec</c>,
/// which makes the desktop's authentication dialog go away.
/// </summary>
internal static class ElevatedRunner
{
    /// <summary>The whole SMBIOS table is a few tens of kilobytes; anything near this is not it.</summary>
    public const int MaxOutput = 4 * 1024 * 1024;

    private static readonly TimeSpan PipeDrain = TimeSpan.FromSeconds(5);

    public static async Task<ElevatedRun> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
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
            process.StandardInput.Close(); // pkexec reads no input here, and a program that waits for some must not hang.
            var output = new MemoryStream();
            Task<bool> read = ReadLimited(process, process.StandardOutput.BaseStream, output);
            Task error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);

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
            Task pipes = Task.WhenAll(read, error);
            if (await Task.WhenAny(pipes, Task.Delay(PipeDrain, CancellationToken.None)) != pipes)
            {
                return ElevatedRun.Failed(ElevatedStatus.TimedOut);
            }

            return read.Result
                ? new ElevatedRun(ElevatedStatus.Exited, process.ExitCode, output.ToArray())
                : ElevatedRun.Failed(ElevatedStatus.TooLarge);
        }
    }

    /// <summary>Copies the stream to <paramref name="output"/>; false, with the program killed, when it prints more than the limit.</summary>
    private static async Task<bool> ReadLimited(Process process, Stream input, MemoryStream output)
    {
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer)) > 0)
        {
            if (output.Length + count > MaxOutput)
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
