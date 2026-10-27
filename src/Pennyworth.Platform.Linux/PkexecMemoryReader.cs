using System.Text;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Reads the memory modules, which the kernel exposes only to root (<c>/sys/firmware/dmi/tables/DMI</c> is
/// <c>0400</c>). This is the one place Pennyworth asks for a password, and only when the person pressed the
/// button: the desktop's polkit agent shows the prompt and owns the password; it never passes through the
/// app, its arguments or its environment. Nothing is changed and nothing is stored.
/// </summary>
/// <remarks>
/// It runs <c>pkexec cat /sys/firmware/dmi/tables/DMI</c> and parses the raw table with the same
/// <see cref="Smbios"/> reader Windows uses. If that gives no modules and <c>dmidecode</c> is installed, it asks
/// <c>pkexec dmidecode -t 17</c> instead (a second prompt, in that case only). Programs are found in fixed
/// system folders, never through <c>PATH</c>; only polkit's <c>pkexec</c> is used, never <c>sudo</c>.
/// </remarks>
public sealed class PkexecMemoryReader : IMemoryModuleReader
{
    /// <summary>pkexec's exit code when the authentication dialog was dismissed.</summary>
    private const int Dismissed = 126;

    /// <summary>pkexec's exit code when the person is not authorised, or no authentication agent could ask.</summary>
    private const int NotAuthorized = 127;

    private const string Table = "/sys/firmware/dmi/tables/DMI";

    private static readonly string[] Pkexec = ["/usr/bin/pkexec", "/bin/pkexec"];
    private static readonly string[] Cat = ["/usr/bin/cat", "/bin/cat"];
    private static readonly string[] Dmidecode = ["/usr/sbin/dmidecode", "/usr/bin/dmidecode", "/sbin/dmidecode", "/bin/dmidecode"];

    /// <summary>Long enough for a person to find the password, short enough to end if the prompt was forgotten.</summary>
    internal static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(120);

    private readonly ElevatedRunnerFunc _run;
    private readonly Func<string, bool> _exists;
    private readonly TimeSpan _timeout;

    public PkexecMemoryReader()
        : this(ElevatedRunner.RunAsync, File.Exists, PromptTimeout)
    {
    }

    /// <param name="run">Runs a program; tests hand in a fake.</param>
    /// <param name="exists">Whether a program is installed at an absolute path.</param>
    internal PkexecMemoryReader(ElevatedRunnerFunc run, Func<string, bool> exists, TimeSpan timeout)
    {
        _run = run;
        _exists = exists;
        _timeout = timeout;
    }

    public async Task<MemoryModulesResult> ReadAsync(CancellationToken cancellationToken)
    {
        if (Find(Pkexec) is not { } pkexec)
        {
            return MemoryModulesResult.Unavailable; // No polkit: there is no way to ask.
        }

        if (Find(Cat) is { } cat)
        {
            ElevatedRun table = await _run(pkexec, [cat, Table], _timeout, cancellationToken);
            if (Judge(table, run => FromTable(run.Output)) is { } fromTable)
            {
                return fromTable;
            }
        }

        if (Find(Dmidecode) is { } dmidecode)
        {
            ElevatedRun text = await _run(pkexec, [dmidecode, "-t", "17"], _timeout, cancellationToken);
            if (Judge(text, run => FromText(Encoding.UTF8.GetString(run.Output))) is { } fromText)
            {
                return fromText;
            }
        }

        return MemoryModulesResult.Unavailable;
    }

    /// <summary>
    /// The result a run settles, or null when this source gave nothing usable and the next one may be tried.
    /// A dismissed prompt and a refusal settle it: asking again would be a nuisance, and wrong. So does a
    /// program killed by a signal (exit code 128 and up): someone stopped it on purpose.
    /// </summary>
    private static MemoryModulesResult? Judge(ElevatedRun run, Func<ElevatedRun, MemoryModulesResult?> parse) => run switch
    {
        { Status: ElevatedStatus.Exited, ExitCode: Dismissed or NotAuthorized } => MemoryModulesResult.Cancelled,
        { Status: ElevatedStatus.Exited, ExitCode: 0 } => parse(run),
        { Status: ElevatedStatus.Exited, ExitCode: > 0 and < Dismissed } => null,
        _ => MemoryModulesResult.Unavailable, // Could not start, never finished, or was killed.
    };

    private static MemoryModulesResult? FromTable(byte[] table)
    {
        SmbiosInfo info = Smbios.ParseStructures(table);
        return info.Modules.Count > 0 ? MemoryModulesResult.Read(info.Modules, info.MemorySlots) : null;
    }

    private static MemoryModulesResult? FromText(string text)
    {
        (IReadOnlyList<MemoryModule> modules, int? slots) = DmidecodeMemory.Parse(text);
        return modules.Count > 0 ? MemoryModulesResult.Read(modules, slots) : null;
    }

    private string? Find(string[] candidates) => candidates.FirstOrDefault(_exists);
}
