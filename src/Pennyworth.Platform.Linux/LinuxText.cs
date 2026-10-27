using Pennyworth.Core.Wording;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Linux' wording: a file manager and the Trash instead of Explorer and the Recycle Bin, the system
/// instead of Windows, and the unprivileged system-space measurement where Windows asks to unlock a
/// full scan. Nothing here promises a permission prompt, because Linux never shows one.
/// </summary>
public sealed class LinuxText : IPlatformText
{
    public static LinuxText Instance { get; } = new();

    private LinuxText()
    {
    }

    public string Text(PlatformText text, bool portuguese) => portuguese ? Portuguese(text) : English(text);

    private static string English(PlatformText text) => text switch
    {
        PlatformText.FileManager => "your file manager",
        PlatformText.FileManagerFull => "your file manager",
        PlatformText.Trash => "Trash",
        PlatformText.System => "the system",
        PlatformText.SystemInitial => "The system",
        PlatformText.SystemCategory => "System",
        PlatformText.SystemItself => "The system itself",
        PlatformText.HiddenSystemCategory => "System space",
        PlatformText.KeyStore => "Secret Service keyring",
        PlatformText.LocalDisk => "Disk",
        PlatformText.SystemDriveRoot => "/",
        PlatformText.OsRowLabel => "Operating system",
        PlatformText.NoDriveLetter => "not mounted",
        PlatformText.HibernationFile => "swapfile",
        PlatformText.HibernationTitle => "Swap file",
        PlatformText.HibernationNote => "Hibernation keeps its image in swap, which is why this space is set aside. I'll leave it as is unless you ask.",
        PlatformText.NoHibernation => "No swap file was found, so no space is set aside for hibernation.",
        PlatformText.RestorePointsPlace => "Snapshots",
        PlatformText.RestorePointsHint => "Space only btrfs snapshots hold",
        PlatformText.RestorePointsLocation => "",
        PlatformText.MetadataPlace => "Filesystem metadata and reserved blocks",
        PlatformText.MetadataHint => "What the filesystem keeps for itself",
        PlatformText.MetadataLocation => "",
        PlatformText.RequirementsNotMeasured => "game or operating system requirements",
        PlatformText.UnlockBanner => "There's a corner of your drive that a scan of its folders can't see: space the filesystem keeps for itself. I can measure it in seconds.",
        PlatformText.UnlockCta => "Measure system space",
        PlatformText.UnlockAction => "Measure system space",
        PlatformText.UnlockActionHint => "offers to measure the system space",
        PlatformText.UnlockTitle => "May I measure the system space?",
        PlatformText.UnlockWhy => "The filesystem keeps some space for itself: metadata, reserved blocks and, on btrfs, snapshots. I read those numbers straight from the system, in seconds. No password is needed; I never ask for one.",
        PlatformText.UnlockSees => "Reserved blocks, filesystem metadata and snapshot space, as far as your account may read them.",
        PlatformText.UnlockButton => "Measure",
        PlatformText.UnlockWaiting => "Measuring…",
        PlatformText.UnlockedToast => "Done. The system space is measured.",
        PlatformText.UnlockFailed => "I couldn't measure the system space this time. Normal mode carries on.",
        PlatformText.UnaccountedHint => "The filesystem keeps a few rooms to itself, metadata and snapshots among them. I can measure them precisely, and without a password.",
        PlatformText.OvercountHint => "A small confession: some files were counted twice (hard links), so these totals run a touch high.",
        PlatformText.LockedBody => "Quite normal for system folders; nothing is amiss. Only an administrator may read this one, so its size stays out of the list.",
        PlatformText.NeedsFullScan => "Needs administrator rights, which I never ask for.",
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, null),
    };

    private static string Portuguese(PlatformText text) => text switch
    {
        PlatformText.FileManager => "gerenciador de arquivos",
        PlatformText.FileManagerFull => "gerenciador de arquivos",
        PlatformText.Trash => "Lixeira",
        PlatformText.System => "o sistema",
        PlatformText.SystemInitial => "O sistema",
        PlatformText.SystemCategory => "Sistema",
        PlatformText.SystemItself => "O próprio sistema",
        PlatformText.HiddenSystemCategory => "Espaço do sistema",
        PlatformText.KeyStore => "chaveiro do Secret Service",
        PlatformText.LocalDisk => "Disco",
        PlatformText.SystemDriveRoot => "/",
        PlatformText.OsRowLabel => "Sistema operacional",
        PlatformText.NoDriveLetter => "não montado",
        PlatformText.HibernationFile => "swapfile",
        PlatformText.HibernationTitle => "Arquivo de swap",
        PlatformText.HibernationNote => "A hibernação guarda a imagem no swap, por isso este espaço fica reservado. Deixo como está, a menos que peça.",
        PlatformText.NoHibernation => "Nenhum arquivo de swap foi encontrado, então nenhum espaço é reservado para a hibernação.",
        PlatformText.RestorePointsPlace => "Snapshots",
        PlatformText.RestorePointsHint => "Espaço que só os snapshots do btrfs ocupam",
        PlatformText.RestorePointsLocation => "",
        PlatformText.MetadataPlace => "Metadados do sistema de arquivos e blocos reservados",
        PlatformText.MetadataHint => "O que o sistema de arquivos guarda para si",
        PlatformText.MetadataLocation => "",
        PlatformText.RequirementsNotMeasured => "requisitos de jogos ou do sistema operacional",
        PlatformText.UnlockBanner => "Há um canto do disco que a leitura das pastas não alcança: o espaço que o sistema de arquivos guarda para si. Meço em segundos.",
        PlatformText.UnlockCta => "Medir o espaço do sistema",
        PlatformText.UnlockAction => "Medir o espaço do sistema",
        PlatformText.UnlockActionHint => "oferece medir o espaço do sistema",
        PlatformText.UnlockTitle => "Posso medir o espaço do sistema?",
        PlatformText.UnlockWhy => "O sistema de arquivos guarda um pouco de espaço para si: metadados, blocos reservados e, no btrfs, snapshots. Leio esses números direto do sistema, em segundos. Não é preciso senha; nunca peço uma.",
        PlatformText.UnlockSees => "Blocos reservados, metadados do sistema de arquivos e o espaço dos snapshots, até onde a sua conta consegue ler.",
        PlatformText.UnlockButton => "Medir",
        PlatformText.UnlockWaiting => "Medindo…",
        PlatformText.UnlockedToast => "Pronto. O espaço do sistema foi medido.",
        PlatformText.UnlockFailed => "Não consegui medir o espaço do sistema desta vez. O modo normal continua.",
        PlatformText.UnaccountedHint => "O sistema de arquivos guarda alguns cômodos para si, como metadados e snapshots. Consigo medi-los com precisão, e sem pedir senha.",
        PlatformText.OvercountHint => "Uma pequena confissão: alguns arquivos foram contados duas vezes (links físicos), então os totais estão um tanto altos.",
        PlatformText.LockedBody => "Perfeitamente normal em pastas do sistema; não há nada de errado. Só um administrador pode ler esta, então o tamanho dela fica fora da lista.",
        PlatformText.NeedsFullScan => "Precisa de direitos de administrador, que nunca peço.",
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, null),
    };
}
