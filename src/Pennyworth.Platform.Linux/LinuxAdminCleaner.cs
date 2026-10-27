using Pennyworth.Cleanup;
using Pennyworth.Core.Privilege;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Cleans the root-owned rules (<c>system-package-cache</c>, <c>system-crash-reports</c>) by handing the preview to the
/// privileged helper through <see cref="IPrivilegedOperations"/>. It adds nothing: what may be deleted is decided
/// by the helper's own compiled-in list of folders and by the preview.
/// </summary>
internal sealed class LinuxAdminCleaner(IPrivilegedOperations privileged) : IAdminCleaner
{
    public PrivilegedCapability Capability => privileged.Capability(PrivilegedOperation.SystemCleanup);

    public Task<PrivilegedResult<CleanupResult>> CleanAsync(CleanupEstimate estimate, CancellationToken cancellationToken) =>
        privileged.CleanSystemAsync(estimate, cancellationToken);
}
