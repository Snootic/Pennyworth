using Pennyworth.App.Resources;
using Pennyworth.Core.Privilege;

namespace Pennyworth.App.ViewModels;

/// <summary>The plain sentences for why something that needs an administrator cannot even be offered.</summary>
internal static class PrivilegedText
{
    /// <summary>
    /// What a person can do about <paramref name="reason"/>, in the current language; a general sentence when the platform
    /// gave none. Read on every call, so a language switch shows at once.
    /// </summary>
    internal static string Reason(PrivilegedUnavailableReason? reason) => reason switch
    {
        PrivilegedUnavailableReason.NoPkexec => Strings.PrivReasonNoPkexec,
        PrivilegedUnavailableReason.NoAuthenticationAgent => Strings.PrivReasonNoAuthenticationAgent,
        PrivilegedUnavailableReason.HelperNotInstalled => Strings.PrivReasonHelperNotInstalled,
        PrivilegedUnavailableReason.HelperNotTrusted => Strings.PrivReasonHelperNotTrusted,
        PrivilegedUnavailableReason.HelperOutdated => Strings.PrivReasonHelperOutdated,
        _ => Strings.PrivReasonUnknown,
    };
}
