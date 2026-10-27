using Pennyworth.Core.Privilege;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Reads the memory modules, which the kernel exposes only to root (<c>/sys/firmware/dmi/tables/DMI</c> is
/// <c>0400</c>), through <see cref="IPrivilegedOperations"/>, and only when the person pressed the button. The
/// desktop's polkit agent shows the password prompt and owns the password. Nothing is changed and nothing is stored.
/// </summary>
/// <remarks>
/// It asks for the raw SMBIOS table and parses it with the <see cref="Smbios"/> reader Windows uses. If that gives no
/// modules, it asks for the text of <c>dmidecode -t 17</c> instead (a second prompt, in that case only). A dismissed or
/// refused prompt settles the read, as does a failure: asking again would be a nuisance.
/// </remarks>
internal sealed class PrivilegedMemoryReader(IPrivilegedOperations privileged) : IMemoryModuleReader
{
    public async Task<MemoryModulesResult> ReadAsync(CancellationToken cancellationToken)
    {
        PrivilegedResult<byte[]> table = await privileged.ReadFirmwareTablesAsync(cancellationToken);
        if (table.Outcome != PrivilegedOutcome.Done)
        {
            return Settle(table.Outcome, table.Reason);
        }

        SmbiosInfo info = Smbios.ParseStructures(table.Value!);
        if (info.Modules.Count > 0)
        {
            return MemoryModulesResult.Read(info.Modules, info.MemorySlots);
        }

        PrivilegedResult<string> text = await privileged.ReadMemoryDevicesTextAsync(cancellationToken);
        if (text.Outcome != PrivilegedOutcome.Done)
        {
            return Settle(text.Outcome, text.Reason);
        }

        (IReadOnlyList<MemoryModule> modules, int? slots) = DmidecodeMemory.Parse(text.Value!);
        return modules.Count > 0 ? MemoryModulesResult.Read(modules, slots) : MemoryModulesResult.Unavailable;
    }

    private static MemoryModulesResult Settle(PrivilegedOutcome outcome, PrivilegedUnavailableReason? reason) => outcome switch
    {
        PrivilegedOutcome.Cancelled => MemoryModulesResult.Cancelled,
        PrivilegedOutcome.NotAvailable when reason is { } why => MemoryModulesResult.NotAvailable(why),
        _ => MemoryModulesResult.Unavailable, // It failed, was stopped or never finished.
    };
}
