using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Pennyworth.Platform.Linux.Tests;

/// <summary>One command line the app tried to run, as the fake runner saw it.</summary>
internal sealed record RunCall(string File, string[] Arguments, byte[]? Input, int MaxOutput, TimeSpan Timeout, CancellationToken Token)
{
    public string Line => string.Join(' ', [File, .. Arguments]);
}

/// <summary>
/// Plays pkexec: answers in order and records every command line, its standard input and its limits. A run nobody
/// prepared an answer for throws, which is how a test proves that nothing was run (or asked a second time).
/// </summary>
internal sealed class FakeRunner(params ElevatedRun[] answers)
{
    private readonly Queue<ElevatedRun> _answers = new(answers);

    public List<RunCall> Calls { get; } = [];

    public IEnumerable<string> Lines => Calls.Select(call => call.Line);

    public Task<ElevatedRun> Run(string file, IReadOnlyList<string> arguments, byte[]? input, int maxOutput, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add(new RunCall(file, [.. arguments], input, maxOutput, timeout, cancellationToken));
        return _answers.Count > 0
            ? Task.FromResult(_answers.Dequeue())
            : throw new InvalidOperationException($"Unexpected run: {Calls[^1].Line}");
    }
}

/// <summary>Answers and file systems the privileged tests share.</summary>
internal static class Privileged
{
    public const string Pkexec = "/usr/bin/pkexec";
    public const string Helper = HelperProtocol.InstalledPath;

    public static ElevatedRun Exit(int code, byte[]? output = null, string error = "") => new(ElevatedStatus.Exited, code, output ?? [], error);

    public static ElevatedRun Text(string text) => Exit(0, Encoding.UTF8.GetBytes(text));

    public static ElevatedRun Json<T>(T value, JsonTypeInfo<T> type) => Exit(0, JsonSerializer.SerializeToUtf8Bytes(value, type));

    public static T Parse<T>(byte[]? bytes, JsonTypeInfo<T> type) => JsonSerializer.Deserialize(bytes!, type)!;

    /// <summary>A file system in which the helper and every folder above it belong to root and nobody else may write there.</summary>
    public static EntryInfo TrustedLstat(string path) => path == Helper
        ? new EntryInfo(EntryKind.File, 0, 0b111_101_101)
        : IsAncestor(path) ? new EntryInfo(EntryKind.Directory, 0, 0b111_101_101) : new EntryInfo(EntryKind.Missing, 0, 0);

    /// <summary>The same, with nothing installed at the helper's path.</summary>
    public static EntryInfo MissingLstat(string path) => path == Helper ? new EntryInfo(EntryKind.Missing, 0, 0) : TrustedLstat(path);

    /// <summary>The same, but the helper's own folder is writable by its group.</summary>
    public static EntryInfo GroupWritableLstat(string path) => path == "/usr/libexec/pennyworth" ? new EntryInfo(EntryKind.Directory, 0, 0b111_111_101) : TrustedLstat(path);

    /// <summary>The same, but the helper is owned by an ordinary user.</summary>
    public static EntryInfo UserOwnedLstat(string path) => path == Helper ? new EntryInfo(EntryKind.File, 1000, 0b111_101_101) : TrustedLstat(path);

    private static bool IsAncestor(string path) => path is "/" or "/usr" or "/usr/libexec" or "/usr/libexec/pennyworth";

    public static LinuxPrivilegedOperations Ops(
        FakeRunner runner,
        Func<string, EntryInfo> lstat,
        string[]? installed = null,
        string mountInfo = "",
        VfsCounters? counters = null,
        TimeSpan? timeout = null) =>
        Ops(runner.Run, lstat, installed, mountInfo, counters, timeout);

    public static LinuxPrivilegedOperations Ops(
        ElevatedRunnerFunc run,
        Func<string, EntryInfo> lstat,
        string[]? installed = null,
        string mountInfo = "",
        VfsCounters? counters = null,
        TimeSpan? timeout = null)
    {
        string[] present = installed ?? [Pkexec, "/usr/bin/cat", "/usr/sbin/dmidecode", "/usr/bin/btrfs"];
        return new LinuxPrivilegedOperations(run, present.Contains, lstat, timeout ?? TimeSpan.FromSeconds(1), () => mountInfo, _ => counters);
    }
}
