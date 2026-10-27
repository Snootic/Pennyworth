using Pennyworth.Core.Privilege;

namespace Pennyworth.Monitoring.Hardware;

public enum MemoryModulesOutcome
{
    /// <summary>The firmware tables were read.</summary>
    Read,

    /// <summary>The person dismissed the password prompt, or was not allowed.</summary>
    Cancelled,

    /// <summary>The tables could not be read: no way to ask for the password, a timeout, or a table nobody can parse.</summary>
    Unavailable,
}

/// <param name="Slots">Memory slots on the motherboard, when the firmware says.</param>
/// <param name="Reason">With <see cref="MemoryModulesOutcome.Unavailable"/>: why there was no way to ask, when that is the cause.</param>
public sealed record MemoryModulesResult(MemoryModulesOutcome Outcome, IReadOnlyList<MemoryModule> Modules, int? Slots, PrivilegedUnavailableReason? Reason = null)
{
    public static MemoryModulesResult Read(IReadOnlyList<MemoryModule> modules, int? slots) => new(MemoryModulesOutcome.Read, modules, slots);

    public static MemoryModulesResult Cancelled { get; } = new(MemoryModulesOutcome.Cancelled, [], null);

    public static MemoryModulesResult Unavailable { get; } = new(MemoryModulesOutcome.Unavailable, [], null);

    public static MemoryModulesResult NotAvailable(PrivilegedUnavailableReason reason) => new(MemoryModulesOutcome.Unavailable, [], null, reason);
}

/// <summary>
/// An optional extra for operating systems that keep the memory modules where only an administrator can
/// read them. It is always an explicit request by the person, never part of <see cref="ISpecsReader.Read"/>.
/// </summary>
public interface IMemoryModuleReader
{
    /// <summary>
    /// Asks for the administrator's authorization and reads the modules. Waits for a person, so it can take
    /// minutes; cancelling the token closes the request.
    /// </summary>
    Task<MemoryModulesResult> ReadAsync(CancellationToken cancellationToken);
}
