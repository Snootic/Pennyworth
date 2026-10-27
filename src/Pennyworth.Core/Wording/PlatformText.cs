namespace Pennyworth.Core.Wording;

/// <summary>
/// The wording that depends on the operating system. Each value is looked up with
/// <see cref="IPlatformText.Text"/>; a resource string names a value with a <c>{@Name}</c> token
/// (see <see cref="PlatformTokens"/>), so the sentence around it is written once.
/// </summary>
/// <remarks>
/// Values that read as nouns fit mid-sentence in both languages ("Show in {@FileManager}",
/// "Abrindo no {@FileManagerFull}"). Values written as whole sentences are the places where Windows and
/// Linux do different things, so the sentence itself differs.
/// </remarks>
public enum PlatformText
{
    /// <summary>The program that browses folders: "Explorer" / "your file manager". Follows "in" / "no".</summary>
    FileManager,

    /// <summary><see cref="FileManager"/> as the toast names it: "Windows Explorer".</summary>
    FileManagerFull,

    /// <summary>Where deleted files wait: "Recycle Bin" / "Trash".</summary>
    Trash,

    /// <summary>The operating system as an object or mid-sentence subject: "Windows" / "the system".</summary>
    System,

    /// <summary><see cref="System"/> opening a sentence: "Windows" / "The system".</summary>
    SystemInitial,

    /// <summary>The storage category title of the operating system's own files.</summary>
    SystemCategory,

    /// <summary>The line under <see cref="SystemCategory"/>.</summary>
    SystemItself,

    /// <summary>The storage category title of the space a folder scan cannot see.</summary>
    HiddenSystemCategory,

    /// <summary>Where API keys are kept: "Windows Credential Manager" / "Secret Service keyring".</summary>
    KeyStore,

    /// <summary>A drive that has no volume label.</summary>
    LocalDisk,

    /// <summary>The root of the system drive when none is known: "C:\" / "/".</summary>
    SystemDriveRoot,

    /// <summary>The label of the operating-system row in the specs.</summary>
    OsRowLabel,

    /// <summary>A disk that has nothing mounted on it.</summary>
    NoDriveLetter,

    /// <summary>The file that holds the hibernation image, as the power diagram names it.</summary>
    HibernationFile,

    /// <summary>The title of the hibernation row in the power panel.</summary>
    HibernationTitle,

    /// <summary>What the power panel says when that file exists.</summary>
    HibernationNote,

    /// <summary>What the power panel says when it does not.</summary>
    NoHibernation,

    /// <summary>The measured space of old system copies: "Restore points" / "Snapshots".</summary>
    RestorePointsPlace,

    /// <summary>The line under <see cref="RestorePointsPlace"/>.</summary>
    RestorePointsHint,

    /// <summary>Where <see cref="RestorePointsPlace"/> sits inside the drive; empty when it has no folder.</summary>
    RestorePointsLocation,

    /// <summary>The measured space of the file system's own bookkeeping: "NTFS file system data" / "Filesystem metadata".</summary>
    MetadataPlace,

    /// <summary>The line under <see cref="MetadataPlace"/>.</summary>
    MetadataHint,

    /// <summary>Where <see cref="MetadataPlace"/> sits inside the drive; empty when it has no file.</summary>
    MetadataLocation,

    /// <summary>The kinds of requirements the assistant cannot see, named in its instructions.</summary>
    RequirementsNotMeasured,

    /// <summary>The banner offering to measure what a folder scan cannot see.</summary>
    UnlockBanner,

    /// <summary>The button of that banner.</summary>
    UnlockCta,

    /// <summary>The same offer as a button under an assistant answer.</summary>
    UnlockAction,

    /// <summary>That button as the assistant's instructions describe it to the model: a verb phrase after "—".</summary>
    UnlockActionHint,

    /// <summary>The title of the dialog behind that button.</summary>
    UnlockTitle,

    /// <summary>Why, and what the system asks for.</summary>
    UnlockWhy,

    /// <summary>What the measurement covers.</summary>
    UnlockSees,

    /// <summary>The dialog's confirm button.</summary>
    UnlockButton,

    /// <summary>The line shown while the measurement runs.</summary>
    UnlockWaiting,

    /// <summary>The toast after a successful measurement.</summary>
    UnlockedToast,

    /// <summary>The toast after a failed one.</summary>
    UnlockFailed,

    /// <summary>The explanation of the "not measured yet" category.</summary>
    UnaccountedHint,

    /// <summary>The note that the scan counted more than the system reports.</summary>
    OvercountHint,

    /// <summary>What the empty state of a folder the user may not list says after its title.</summary>
    LockedBody,

    /// <summary>The note on a cleanup item that needs administrator rights.</summary>
    NeedsFullScan,
}
