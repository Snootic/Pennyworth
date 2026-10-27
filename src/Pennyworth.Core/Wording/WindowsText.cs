namespace Pennyworth.Core.Wording;

/// <summary>
/// Windows' wording. It is the app's default text, so it lives beside <see cref="PlatformText"/>; the
/// strings are the ones the screens always showed, character for character.
/// </summary>
public sealed class WindowsText : IPlatformText
{
    public static WindowsText Instance { get; } = new();

    private WindowsText()
    {
    }

    public string Text(PlatformText text, bool portuguese) => portuguese ? Portuguese(text) : English(text);

    private static string English(PlatformText text) => text switch
    {
        PlatformText.FileManager => "Explorer",
        PlatformText.FileManagerFull => "Windows Explorer",
        PlatformText.Trash => "Recycle Bin",
        PlatformText.System => "Windows",
        PlatformText.SystemInitial => "Windows",
        PlatformText.SystemCategory => "Windows",
        PlatformText.SystemItself => "Windows itself",
        PlatformText.HiddenSystemCategory => "Hidden system",
        PlatformText.KeyStore => "Windows Credential Manager",
        PlatformText.LocalDisk => "Local Disk",
        PlatformText.SystemDriveRoot => @"C:\",
        PlatformText.OsRowLabel => "Windows",
        PlatformText.NoDriveLetter => "no drive letter",
        PlatformText.HibernationFile => "hiberfil.sys",
        PlatformText.HibernationTitle => "Hibernation file",
        PlatformText.HibernationNote => "Switching hibernation off would free this space, though the PC could no longer hibernate. I'll leave it as is unless you ask.",
        PlatformText.NoHibernation => "Hibernation is off, so no space is set aside for it.",
        PlatformText.RestorePointsPlace => "Restore points",
        PlatformText.RestorePointsHint => "Snapshots that can undo system changes",
        PlatformText.RestorePointsLocation => "System Volume Information",
        PlatformText.MetadataPlace => "NTFS file system data",
        PlatformText.MetadataHint => "The drive's own index of files",
        PlatformText.MetadataLocation => "$MFT",
        PlatformText.RequirementsNotMeasured => "game or Windows requirements",
        PlatformText.UnlockBanner => "There's a corner of your drive I'm not yet permitted to see. With your leave, I can measure it in seconds.",
        PlatformText.UnlockCta => "Unlock full scan",
        PlatformText.UnlockTitle => "May I see the rest of the drive?",
        PlatformText.UnlockWhy => "To measure the hidden system space, and to do it in seconds, I need to read the drive directly. Windows will ask for your permission; it's only proper.",
        PlatformText.UnlockSees => "Restore points, protected folders and the exact hidden system space.",
        PlatformText.UnlockButton => "Unlock",
        PlatformText.UnlockWaiting => "Awaiting Windows' permission…",
        PlatformText.UnlockedToast => "Thank you. I can now see everything, hidden system space included.",
        PlatformText.UnlockFailed => "I couldn't measure the hidden space this time. Normal mode carries on.",
        PlatformText.UnaccountedHint => "Windows keeps a few rooms locked, restore points among them. With your permission, the full scan will measure them precisely.",
        PlatformText.OvercountHint => "A small confession: some files were counted twice (hard links), so these totals run a touch high. The full scan will set it straight.",
        PlatformText.LockedBody => "Quite normal for system folders; nothing is amiss. With your permission, the full scan can measure it.",
        PlatformText.NeedsFullScan => "Needs the full-scan permission, coming in a later version.",
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, null),
    };

    private static string Portuguese(PlatformText text) => text switch
    {
        PlatformText.FileManager => "Explorador",
        PlatformText.FileManagerFull => "Explorador do Windows",
        PlatformText.Trash => "Lixeira",
        PlatformText.System => "o Windows",
        PlatformText.SystemInitial => "O Windows",
        PlatformText.SystemCategory => "Windows",
        PlatformText.SystemItself => "O próprio Windows",
        PlatformText.HiddenSystemCategory => "Sistema oculto",
        PlatformText.KeyStore => "Gerenciador de Credenciais do Windows",
        PlatformText.LocalDisk => "Disco Local",
        PlatformText.SystemDriveRoot => @"C:\",
        PlatformText.OsRowLabel => "Windows",
        PlatformText.NoDriveLetter => "sem letra de unidade",
        PlatformText.HibernationFile => "hiberfil.sys",
        PlatformText.HibernationTitle => "Arquivo de hibernação",
        PlatformText.HibernationNote => "Desligar a hibernação liberaria este espaço, mas o PC deixaria de hibernar. Deixo como está, a menos que peça.",
        PlatformText.NoHibernation => "A hibernação está desligada, então nenhum espaço é reservado para ela.",
        PlatformText.RestorePointsPlace => "Pontos de restauração",
        PlatformText.RestorePointsHint => "Cópias que desfazem mudanças no sistema",
        PlatformText.RestorePointsLocation => "System Volume Information",
        PlatformText.MetadataPlace => "Dados do sistema de arquivos NTFS",
        PlatformText.MetadataHint => "O índice de arquivos do próprio disco",
        PlatformText.MetadataLocation => "$MFT",
        PlatformText.RequirementsNotMeasured => "requisitos de jogos ou do Windows",
        PlatformText.UnlockBanner => "Há um canto do disco que ainda não tenho permissão para ver. Com a sua licença, meço em segundos.",
        PlatformText.UnlockCta => "Desbloquear verificação completa",
        PlatformText.UnlockTitle => "Posso ver o restante do disco?",
        PlatformText.UnlockWhy => "Para medir o espaço oculto do sistema, e em segundos, preciso ler o disco diretamente. O Windows vai pedir sua permissão; é o correto.",
        PlatformText.UnlockSees => "Pontos de restauração, pastas protegidas e o espaço oculto exato.",
        PlatformText.UnlockButton => "Desbloquear",
        PlatformText.UnlockWaiting => "Aguardando a permissão do Windows…",
        PlatformText.UnlockedToast => "Obrigado. Agora vejo tudo, inclusive o espaço oculto do sistema.",
        PlatformText.UnlockFailed => "Não consegui medir o espaço oculto desta vez. O modo normal continua.",
        PlatformText.UnaccountedHint => "O Windows mantém alguns cômodos trancados, como os pontos de restauração. Com sua permissão, a verificação completa mede tudo com precisão.",
        PlatformText.OvercountHint => "Uma pequena confissão: alguns arquivos foram contados duas vezes (links físicos), então os totais estão um tanto altos. A verificação completa põe tudo em ordem.",
        PlatformText.LockedBody => "Perfeitamente normal em pastas do sistema; não há nada de errado. Com sua permissão, a verificação completa consegue medir.",
        PlatformText.NeedsFullScan => "Precisa da permissão da verificação completa, que chega numa próxima versão.",
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, null),
    };
}
