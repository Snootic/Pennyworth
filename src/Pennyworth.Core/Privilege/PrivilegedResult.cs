namespace Pennyworth.Core.Privilege;

/// <summary>How an operation that needs an administrator ended.</summary>
public enum PrivilegedOutcome
{
    /// <summary>It ran and produced its result.</summary>
    Done,

    /// <summary>The person dismissed the password prompt, or was not allowed. Nothing was done; asking again would be a nuisance.</summary>
    Cancelled,

    /// <summary>There is no way to ask on this machine (see <see cref="PrivilegedUnavailableReason"/>). No prompt was shown.</summary>
    NotAvailable,

    /// <summary>It was asked for and did not work: the helper refused, crashed, ran out of time or said something unreadable.</summary>
    Failed,
}

/// <summary>Why an operation cannot even be offered, in terms the person can act on.</summary>
public enum PrivilegedUnavailableReason
{
    /// <summary>polkit's <c>pkexec</c> is not installed.</summary>
    NoPkexec,

    /// <summary>No authentication agent (the desktop's password dialog) is running to ask with.</summary>
    NoAuthenticationAgent,

    /// <summary>Pennyworth's helper is not installed in its system folder.</summary>
    HelperNotInstalled,

    /// <summary>The helper is there, but it or a folder above it is not owned by root or can be changed by others, so it is never run as root.</summary>
    HelperNotTrusted,

    /// <summary>The installed helper does not speak this version of the app's protocol; installing it again fixes that.</summary>
    HelperOutdated,
}

/// <summary>What can be done right now, known without showing any prompt.</summary>
public sealed record PrivilegedCapability(bool Available, PrivilegedUnavailableReason? Reason = null)
{
    public static PrivilegedCapability Ready { get; } = new(true);

    public static PrivilegedCapability Missing(PrivilegedUnavailableReason reason) => new(false, reason);
}

/// <summary>The result of one privileged operation: a value only when <see cref="Outcome"/> is <see cref="PrivilegedOutcome.Done"/>.</summary>
/// <param name="Reason">Set with <see cref="PrivilegedOutcome.NotAvailable"/>.</param>
/// <param name="Detail">Set with <see cref="PrivilegedOutcome.Failed"/> when the helper said why (a short English line for logs, not for the screen).</param>
public sealed record PrivilegedResult<T>(PrivilegedOutcome Outcome, T? Value = default, PrivilegedUnavailableReason? Reason = null, string? Detail = null)
{
    public static PrivilegedResult<T> Done(T value) => new(PrivilegedOutcome.Done, value);

    public static PrivilegedResult<T> Cancelled { get; } = new(PrivilegedOutcome.Cancelled);

    public static PrivilegedResult<T> NotAvailable(PrivilegedUnavailableReason reason) => new(PrivilegedOutcome.NotAvailable, Reason: reason);

    public static PrivilegedResult<T> Failed(string? detail = null) => new(PrivilegedOutcome.Failed, Detail: detail);

    /// <summary>The same outcome, without a value, for another result type. Not for <see cref="PrivilegedOutcome.Done"/>.</summary>
    public PrivilegedResult<TOther> As<TOther>() => Outcome == PrivilegedOutcome.Done
        ? throw new InvalidOperationException("A finished result has a value; map it instead.")
        : new PrivilegedResult<TOther>(Outcome, default, Reason, Detail);
}
