using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Pennyworth.App.Resources;
using Pennyworth.Assistant;
using Pennyworth.Core.Wording;
using Pennyworth.Platform.Linux;

namespace Pennyworth.App.Tests;

/// <summary>
/// The words that name the operating system's things: Windows reproduces what the screens always said,
/// character for character, and Linux names nothing that exists only on Windows. Both are checked on the
/// resource strings after their <c>{@Name}</c> tokens are resolved, in both languages.
/// </summary>
public class PlatformWordingTests
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Every string whose wording depends on the OS, as it read before the wording was a seam.</summary>
    private static readonly Dictionary<string, (string English, string Portuguese)> WindowsOriginals = new()
    {
        ["NavFoldersHint"] = ("Browse like in Explorer", "Navegue como no Explorador"),
        ["ExplorerToast"] = ("Opening in Windows Explorer: {0}", "Abrindo no Explorador do Windows: {0}"),
        ["FreeSafelySub"] = ("Caches and temporary files. Windows rebuilds them on its own; you won't miss them.", "Caches e arquivos temporários. O Windows os refaz sozinho; não farão falta."),
        ["UnlockBanner"] = ("There's a corner of your drive I'm not yet permitted to see. With your leave, I can measure it in seconds.", "Há um canto do disco que ainda não tenho permissão para ver. Com a sua licença, meço em segundos."),
        ["UnlockCta"] = ("Unlock full scan", "Desbloquear verificação completa"),
        ["LocalDisk"] = ("Local Disk", "Disco Local"),
        ["Category_Windows"] = ("Windows", "Windows"),
        ["Category_HiddenSystem"] = ("Hidden system", "Sistema oculto"),
        ["CategoryHint_Windows"] = ("Windows itself", "O próprio Windows"),
        ["CategoryHint_HiddenSystem"] = ("Space Windows uses behind the scenes", "Espaço que o Windows usa nos bastidores"),
        ["UnaccountedHint"] = ("Windows keeps a few rooms locked, restore points among them. With your permission, the full scan will measure them precisely.", "O Windows mantém alguns cômodos trancados, como os pontos de restauração. Com sua permissão, a verificação completa mede tudo com precisão."),
        ["OvercountHint"] = ("A small confession: some files were counted twice (hard links), so these totals run a touch high. The full scan will set it straight.", "Uma pequena confissão: alguns arquivos foram contados duas vezes (links físicos), então os totais estão um tanto altos. A verificação completa põe tudo em ordem."),
        ["Place_RestorePoints"] = ("Restore points", "Pontos de restauração"),
        ["Place_RestorePointsHint"] = ("Snapshots that can undo system changes", "Cópias que desfazem mudanças no sistema"),
        ["Place_Ntfs"] = ("NTFS file system data", "Dados do sistema de arquivos NTFS"),
        ["Place_NtfsHint"] = ("The drive's own index of files", "O índice de arquivos do próprio disco"),
        ["ShowInExplorer"] = ("Show in Explorer", "Mostrar no Explorador"),
        ["LockedHint"] = ("Windows keeps this folder locked. Quite normal for system folders.", "O Windows mantém esta pasta trancada. Normal em pastas do sistema."),
        ["LockedTitle"] = ("Windows wouldn't let me in here.", "O Windows não me deixou entrar aqui."),
        ["LockedBody"] = ("Quite normal for system folders; nothing is amiss. With your permission, the full scan can measure it.", "Perfeitamente normal em pastas do sistema; não há nada de errado. Com sua permissão, a verificação completa consegue medir."),
        ["SafetyHint_Safe"] = ("Safe to clear. Windows rebuilds these on its own. Already ticked for you.", "Pode limpar. O Windows refaz sozinho. Já deixei marcado."),
        ["NeedsFullScan"] = ("Needs the full-scan permission, coming in a later version.", "Precisa da permissão da verificação completa, que chega numa próxima versão."),
        ["BinNote"] = ("The Recycle Bin holds {0:N0} items. They'll be gone for good, I'm afraid.", "A Lixeira guarda {0:N0} itens. Receio que sumirão de vez."),
        ["PreviewSubBin"] = ("{0} in {1:N0} items. Emptying the Recycle Bin deletes them for good, so it's worth a look first.", "{0} em {1:N0} itens. Esvaziar a Lixeira os apaga de vez, então vale dar uma olhada antes."),
        ["PreviewShowFolder"] = ("Show this folder in Explorer", "Mostrar esta pasta no Explorador"),
        ["PreviewItemsBin"] = ("Items in the Recycle Bin, largest first", "Itens na Lixeira, dos maiores para os menores"),
        ["CachedNote"] = ("Cached memory isn't wasted, I assure you. Windows hands it back the instant an app asks.", "Memória em cache não é desperdício, garanto. O Windows a devolve no instante em que um app pede."),
        ["ProtectedTip"] = ("Windows needs this to run, so it can’t be closed.", "O Windows precisa disto para funcionar, então não pode ser fechado."),
        ["UnlockTitle"] = ("May I see the rest of the drive?", "Posso ver o restante do disco?"),
        ["UnlockWhy"] = ("To measure the hidden system space, and to do it in seconds, I need to read the drive directly. Windows will ask for your permission; it's only proper.", "Para medir o espaço oculto do sistema, e em segundos, preciso ler o disco diretamente. O Windows vai pedir sua permissão; é o correto."),
        ["SeeList"] = ("Restore points, protected folders and the exact hidden system space.", "Pontos de restauração, pastas protegidas e o espaço oculto exato."),
        ["Unlock"] = ("Unlock", "Desbloquear"),
        ["Waiting"] = ("Awaiting Windows' permission…", "Aguardando a permissão do Windows…"),
        ["UnlockedToast"] = ("Thank you. I can now see everything, hidden system space included.", "Obrigado. Agora vejo tudo, inclusive o espaço oculto do sistema."),
        ["UnlockFailed"] = ("I couldn't measure the hidden space this time. Normal mode carries on.", "Não consegui medir o espaço oculto desta vez. O modo normal continua."),
        ["SpecNoLetter"] = ("no drive letter", "sem letra de unidade"),
        ["GpuUnavailable"] = ("Windows isn't sharing graphics card usage right now.", "O Windows não está informando o uso da placa de vídeo agora."),
        ["PsuHiber"] = ("Hibernation file", "Arquivo de hibernação"),
        ["PsuHiberNote"] = ("Switching hibernation off would free this space, though the PC could no longer hibernate. I'll leave it as is unless you ask.", "Desligar a hibernação liberaria este espaço, mas o PC deixaria de hibernar. Deixo como está, a menos que peça."),
        ["PsuNoHiber"] = ("Hibernation is off, so no space is set aside for it.", "A hibernação está desligada, então nenhum espaço é reservado para ela."),
        ["PartName_Trash"] = ("Recycle Bin & cleanup", "Lixeira e limpeza"),
        ["KeyNote"] = ("Kept in the Windows Credential Manager, never in a file.", "Guardada no Gerenciador de Credenciais do Windows, nunca em arquivo."),
        ["BlockedNote"] = ("I held back a suggestion that could harm Windows.", "Segurei uma sugestão que poderia prejudicar o Windows."),
    };

    /// <summary>What no Linux screen may say, in either language.</summary>
    private static readonly Regex WindowsOnly = new(
        @"Windows|Explorer|Explorador|Recycle|Credential|Credenciais|hiberfil|NTFS|\bC:|Local Disk|Disco Local|Volume Information",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Windows_wording_reproduces_every_original_string()
    {
        Dictionary<string, string> english = Raw(null);
        Dictionary<string, string> portuguese = Raw(Portuguese);

        foreach (var (key, original) in WindowsOriginals)
        {
            Assert.Equal(original.English, PlatformTokens.Resolve(english[key], WindowsText.Instance, portuguese: false));
            Assert.Equal(original.Portuguese, PlatformTokens.Resolve(portuguese[key], WindowsText.Instance, portuguese: true));
        }
    }

    [Fact]
    public void Every_string_that_depends_on_the_system_is_pinned_to_its_original()
    {
        string[] tokenized = [.. Raw(null).Where(pair => pair.Value.Contains("{@", StringComparison.Ordinal)).Select(pair => pair.Key)
            .Union(Raw(Portuguese).Where(pair => pair.Value.Contains("{@", StringComparison.Ordinal)).Select(pair => pair.Key))
            .Order(StringComparer.Ordinal)];

        Assert.Equal(WindowsOriginals.Keys.Order(StringComparer.Ordinal).ToArray(), tokenized);
    }

    [Fact]
    public void Windows_text_values_are_the_original_phrases()
    {
        Assert.Equal("Explorer", WindowsText.Instance.Text(PlatformText.FileManager, portuguese: false));
        Assert.Equal("Explorador do Windows", WindowsText.Instance.Text(PlatformText.FileManagerFull, portuguese: true));
        Assert.Equal("Recycle Bin", WindowsText.Instance.Text(PlatformText.Trash, portuguese: false));
        Assert.Equal("Lixeira", WindowsText.Instance.Text(PlatformText.Trash, portuguese: true));
        Assert.Equal("Windows Credential Manager", WindowsText.Instance.Text(PlatformText.KeyStore, portuguese: false));
        Assert.Equal("Gerenciador de Credenciais do Windows", WindowsText.Instance.Text(PlatformText.KeyStore, portuguese: true));
        Assert.Equal(@"C:\", WindowsText.Instance.Text(PlatformText.SystemDriveRoot, portuguese: false));
        Assert.Equal("hiberfil.sys", WindowsText.Instance.Text(PlatformText.HibernationFile, portuguese: true));
        Assert.Equal("System Volume Information", WindowsText.Instance.Text(PlatformText.RestorePointsLocation, portuguese: false));
        Assert.Equal("$MFT", WindowsText.Instance.Text(PlatformText.MetadataLocation, portuguese: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Linux_wording_names_nothing_that_exists_only_on_windows(bool portuguese)
    {
        foreach (var (key, raw) in Raw(portuguese ? Portuguese : null))
        {
            string text = PlatformTokens.Resolve(raw, LinuxText.Instance, portuguese);
            Assert.False(WindowsOnly.IsMatch(text), $"'{key}' says '{text}' on Linux.");
        }

        foreach (PlatformText value in Enum.GetValues<PlatformText>())
        {
            string text = LinuxText.Instance.Text(value, portuguese);
            Assert.False(WindowsOnly.IsMatch(text), $"{value} says '{text}' on Linux.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_wordings_answer_every_text(bool portuguese)
    {
        foreach (PlatformText value in Enum.GetValues<PlatformText>())
        {
            Assert.NotNull(WindowsText.Instance.Text(value, portuguese));
            Assert.NotNull(LinuxText.Instance.Text(value, portuguese));

            // Only a place inside the drive may be empty, and only where the platform has no such folder.
            if (value is not (PlatformText.RestorePointsLocation or PlatformText.MetadataLocation))
            {
                Assert.NotEmpty(WindowsText.Instance.Text(value, portuguese));
                Assert.NotEmpty(LinuxText.Instance.Text(value, portuguese));
            }
        }
    }

    [Fact]
    public void Every_token_names_a_text_and_both_languages_use_the_same_ones()
    {
        Dictionary<string, string> english = Raw(null);
        Dictionary<string, string> portuguese = Raw(Portuguese);

        foreach (var (key, raw) in english)
        {
            string[] names = [.. PlatformTokens.Names(raw).Order(StringComparer.Ordinal)];
            Assert.Equal(names, PlatformTokens.Names(portuguese[key]).Order(StringComparer.Ordinal).ToArray());
            Assert.All(names, name => Assert.True(Enum.TryParse(name, out PlatformText _), $"'{key}' names no platform text: {name}."));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolved_strings_still_format_with_their_arguments(bool windows)
    {
        IPlatformText wording = windows ? WindowsText.Instance : LinuxText.Instance;
        foreach (CultureInfo culture in new[] { CultureInfo.GetCultureInfo("en-US"), Portuguese })
        {
            foreach (var (key, raw) in Raw(culture.Name == "en-US" ? null : culture))
            {
                string text = PlatformTokens.Resolve(raw, wording, culture.Name != "en-US");
                Assert.DoesNotContain("{@", text, StringComparison.Ordinal);
                _ = string.Format(culture, text, "1 GB", 1234L);
            }
        }
    }

    [Fact]
    public void Linux_bin_notes_say_trash()
    {
        Assert.Equal("The Trash holds 3 items. They'll be gone for good, I'm afraid.",
            string.Format(CultureInfo.InvariantCulture, PlatformTokens.Resolve(Raw(null)["BinNote"], LinuxText.Instance, portuguese: false), 3));
        Assert.Equal("Show in your file manager", PlatformTokens.Resolve(Raw(null)["ShowInExplorer"], LinuxText.Instance, portuguese: false));
        Assert.Equal("Mostrar no gerenciador de arquivos", PlatformTokens.Resolve(Raw(Portuguese)["ShowInExplorer"], LinuxText.Instance, portuguese: true));
    }

    [Theory]
    [InlineData("en-US", false)]
    [InlineData("pt-BR", true)]
    public void The_assistant_context_and_persona_name_nothing_that_exists_only_on_windows(string culture, bool portuguese)
    {
        CultureInfo info = CultureInfo.GetCultureInfo(culture);
        ContextBundle bundle = ContextBuilder.Build(AssistantTopic.Overview, [], [], PcSnapshot.Empty, info, LinuxText.Instance);

        string prompt = PersonaPrompt.Compose(bundle);

        Assert.False(WindowsOnly.IsMatch(prompt), prompt);
        Assert.Contains(portuguese ? "esvaziar a Lixeira" : "emptying the Trash", prompt, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Raw(CultureInfo? culture) =>
        Strings.ResourceManager.GetResourceSet(culture ?? CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
}
