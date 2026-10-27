using Pennyworth.Core.Privilege;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Decides whether Pennyworth's root helper may be started through <c>pkexec</c>. A program that runs as root is
/// only as safe as the place it is read from, so the app starts it only if the file and every folder above it
/// belong to root and nobody else can change them, and no part of the path is a symbolic link
/// (<see cref="PathTrust.CheckExecutable"/>). The answer is never kept: it is asked again on every capability query and
/// right before every run, so a folder that is made writable later is noticed.
/// </summary>
internal static class HelperTrust
{
    /// <summary>The helper's state right now, read with the real <c>statx</c>.</summary>
    public static PrivilegedCapability Evaluate() => Evaluate(PathTrust.Lstat);

    /// <param name="lstat">Reads one path without following a link; tests hand in a fake file system.</param>
    /// <returns><see cref="PrivilegedCapability.Ready"/> when the helper may be run, otherwise <see cref="PrivilegedUnavailableReason.HelperNotInstalled"/> or <see cref="PrivilegedUnavailableReason.HelperNotTrusted"/>.</returns>
    public static PrivilegedCapability Evaluate(Func<string, EntryInfo> lstat) =>
        PathTrust.CheckExecutable(HelperProtocol.InstalledPath, lstat) switch
        {
            TrustVerdict.Trusted => PrivilegedCapability.Ready,
            TrustVerdict.Missing => PrivilegedCapability.Missing(PrivilegedUnavailableReason.HelperNotInstalled),
            _ => PrivilegedCapability.Missing(PrivilegedUnavailableReason.HelperNotTrusted),
        };
}
