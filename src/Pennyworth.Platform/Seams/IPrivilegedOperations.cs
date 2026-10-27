using Pennyworth.Cleanup;
using Pennyworth.Core.Privilege;
using Pennyworth.Scanning;

namespace Pennyworth.Platform;

/// <summary>What <see cref="IPrivilegedOperations"/> can do.</summary>
public enum PrivilegedOperation
{
    /// <summary>Read the firmware tables that list the memory modules.</summary>
    FirmwareTables,

    /// <summary>Measure snapshots and file system metadata that only an administrator can see.</summary>
    SystemSpace,

    /// <summary>Measure folders the person cannot list.</summary>
    Folders,

    /// <summary>Delete the listed files of a system folder.</summary>
    SystemCleanup,
}

/// <summary>The size of one folder the person could not list, as measured with an administrator's rights.</summary>
/// <param name="SizeOnDisk">Allocated bytes, counted the way the scan counts them.</param>
/// <param name="Complete">False when something inside could not be read or measuring stopped early: the size is a lower bound.</param>
public sealed record MeasuredFolder(string Path, long SizeOnDisk, long LogicalSize, long Files, bool Complete);

/// <summary>
/// The few things Pennyworth does only when a person asks and then types an administrator password.
/// Every call shows the system's own password dialog (the app never sees the password); none runs unless the
/// person pressed the button that says what it will do. <see cref="Capability"/> answers without asking anything.
/// </summary>
public interface IPrivilegedOperations
{
    /// <summary>Whether <paramref name="operation"/> can be asked for now; cheap, never shows a prompt, re-evaluated on every call.</summary>
    PrivilegedCapability Capability(PrivilegedOperation operation);

    /// <summary>The raw SMBIOS structure table (<c>/sys/firmware/dmi/tables/DMI</c>). Read only.</summary>
    Task<PrivilegedResult<byte[]>> ReadFirmwareTablesAsync(CancellationToken cancellationToken);

    /// <summary>The text of <c>dmidecode -t 17</c>, for firmware the raw table reader finds nothing in. Read only; needs <c>dmidecode</c> installed.</summary>
    Task<PrivilegedResult<string>> ReadMemoryDevicesTextAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Snapshot space and file system metadata of the file system mounted at <paramref name="mountPoint"/>
    /// (<see cref="HiddenSpaceReport.NtfsMetadata"/> keeps its Windows name). A part that could not be measured is null. Read only.
    /// </summary>
    Task<PrivilegedResult<HiddenSpaceReport>> MeasureSystemSpaceAsync(string mountPoint, CancellationToken cancellationToken);

    /// <summary>Sizes of folders (absolute paths). Read only; needs the helper installed.</summary>
    Task<PrivilegedResult<IReadOnlyList<MeasuredFolder>>> MeasureFoldersAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes exactly the files of the preview in <paramref name="estimate"/> (a rule with <c>requiresAdmin</c>),
    /// inside the folders the helper itself lists for that rule. Needs the helper installed.
    /// </summary>
    Task<PrivilegedResult<CleanupResult>> CleanSystemAsync(CleanupEstimate estimate, CancellationToken cancellationToken);
}
