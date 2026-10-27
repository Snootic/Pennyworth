using System.Diagnostics;
using System.Text;

namespace Pennyworth.Platform.Linux;

/// <summary>Runs the system tool called <paramref name="tool"/> with <paramref name="arguments"/>; its standard output, or null when it is missing, fails, times out or prints too much.</summary>
internal delegate string? ToolRunner(string tool, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>
/// The system tools the helper runs as root, and how. A tool is only ever chosen from the fixed absolute paths below (never
/// looked up through <c>PATH</c>) and only if every folder on the way is root-owned and not writable by others
/// (<see cref="PathTrust"/>). It gets no environment but <c>LC_ALL=C</c>, no shell, no standard input and a time and output budget,
/// so a hostile environment or a hostile file cannot steer what runs as root.
/// </summary>
internal static class HelperTools
{
    internal const string Btrfs = "btrfs";

    /// <summary>Longest a single tool may run.</summary>
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Most standard output kept from a tool.</summary>
    internal const int MaxOutputBytes = 16 * 1024 * 1024;

    private static readonly string[] BtrfsPaths = ["/usr/bin/btrfs", "/usr/sbin/btrfs", "/sbin/btrfs", "/bin/btrfs"];

    /// <summary>The fixed absolute paths of <paramref name="tool"/>, in the order they are tried; empty for a tool the helper does not know.</summary>
    internal static IReadOnlyList<string> PathsOf(string tool) => tool == Btrfs ? BtrfsPaths : [];

    /// <summary>The production runner: fixed paths, <see cref="PathTrust"/> on each, <see cref="CommandTimeout"/> and <see cref="MaxOutputBytes"/>.</summary>
    internal static string? Run(string tool, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        Run(PathsOf(tool), arguments, trust: static path => PathTrust.CheckExecutable(path, PathTrust.Lstat), CommandTimeout, MaxOutputBytes, cancellationToken);

    /// <summary>
    /// Runs the first of <paramref name="candidates"/> that <paramref name="trust"/> accepts. Null when none is trusted, the
    /// tool exits with a non-zero status, runs longer than <paramref name="timeout"/> or prints more than
    /// <paramref name="maxOutputBytes"/> (the child is killed in the last two cases). Standard error is read and dropped.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the child is killed first.</exception>
    internal static string? Run(
        IReadOnlyList<string> candidates,
        IReadOnlyList<string> arguments,
        Func<string, TrustVerdict> trust,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        string? tool = candidates.FirstOrDefault(path => trust(path) == TrustVerdict.Trusted);
        if (tool is null)
        {
            return null;
        }

        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.Environment.Clear();
        start.Environment["LC_ALL"] = "C";
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            process.StandardInput.Close();
            bool overflowed = false;
            Task<byte[]> output = Drain(process.StandardOutput.BaseStream, maxOutputBytes, () =>
            {
                overflowed = true;
                Kill(process);
            });
            Task<byte[]> errors = Drain(process.StandardError.BaseStream, 0, static () => { });

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                process.WaitForExitAsync(limit.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                process.WaitForExit();
                Settle(output, errors);
                cancellationToken.ThrowIfCancellationRequested();
                return null; // The tool's own time limit.
            }

            Settle(output, errors);
            return !overflowed && process.ExitCode == 0 && output.IsCompletedSuccessfully
                ? Encoding.UTF8.GetString(output.Result)
                : null;
        }
    }

    /// <summary>
    /// Reads <paramref name="stream"/> to its end so the child never blocks on a full pipe. Up to <paramref name="limit"/> bytes are
    /// kept; the first byte beyond calls <paramref name="onOverflow"/> once and the rest is read and dropped.
    /// </summary>
    private static async Task<byte[]> Drain(Stream stream, int limit, Action onOverflow)
    {
        using var kept = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        bool overflowed = false;
        while (true)
        {
            int read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0)
            {
                return kept.ToArray();
            }

            if (overflowed)
            {
                continue;
            }

            if (kept.Length + read > limit)
            {
                overflowed = true;
                onOverflow();
                continue;
            }

            kept.Write(chunk, 0, read);
        }
    }

    /// <summary>Waits for the pipes to close; a grandchild that keeps one open must not hold the helper, so the wait is bounded.</summary>
    private static void Settle(Task output, Task errors) =>
        Task.WaitAll([output, errors], TimeSpan.FromSeconds(5));

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
