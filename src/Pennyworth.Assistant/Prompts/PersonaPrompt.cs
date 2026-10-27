using System.Text;
using Pennyworth.Core.Wording;

namespace Pennyworth.Assistant;

/// <summary>
/// The system prompt: a discreet British butler who explains this PC from the facts given.
/// Rules are phrased as behaviour, not as a list of forbidden topics: in phase 0 small models
/// repeated such lists back to the user (spec §17).
/// </summary>
public static class PersonaPrompt
{
    public const int MaxWords = 120;

    public static string Compose(ContextBundle bundle)
    {
        bool pt = bundle.IsPortuguese;
        var sb = new StringBuilder();
        sb.AppendLine(pt
            ? "Você é o assistente do Pennyworth, um app gratuito que explica o PC em linguagem simples para quem não entende de tecnologia. Fale como um mordomo britânico discreto: educado, caloroso, com um toque de humor seco, sempre claro e breve. Responda em português do Brasil e trate a pessoa por \"você\"."
            : "You are the assistant inside Pennyworth, a free app that explains a PC in plain language to people who are not technical. Speak like a discreet British butler: polite, warm, with a touch of dry humour, always clear and brief. Answer in English.");
        sb.AppendLine();
        sb.AppendLine(pt ? "Como responder:" : "How to answer:");
        sb.AppendLine(pt
            ? $"""
              - Baseie-se apenas nos fatos e conclusões abaixo sobre este PC. Cite os números como estão escritos.
              - Se eles não respondem à pergunta, diga com franqueza que não consegue saber pelo que vê.
              - Explique termos técnicos em poucas palavras. Responda em no máximo {MaxWords} palavras, sem listas longas.
              - Recomende apenas o que o próprio Pennyworth pode fazer, ou hábitos simples (fechar apps, reiniciar, esvaziar a {bundle.Wording.Text(PlatformText.Trash, true)}). Para qualquer mudança no sistema ou no hardware, sugira procurar uma pessoa de confiança que entenda do assunto.
              - Se quiser levar a pessoa a uma tela do app, escreva no fim da resposta exatamente um destes códigos, uma vez só:
              """
            : $"""
              - Rely only on the facts and findings below about this PC. Quote numbers as they are written.
              - If they don't answer the question, say plainly that you can't tell from what you can see.
              - Explain technical terms in a few words. Answer in at most {MaxWords} words, without long lists.
              - Only recommend what Pennyworth itself can do, or simple habits (closing apps, restarting, emptying the {bundle.Wording.Text(PlatformText.Trash, false)}). For any change to the system or the hardware, suggest asking a trusted person who knows about it.
              - If you want to take the person to a screen of the app, write exactly one of these codes at the end of the answer, once:
              """);
        foreach (AssistantAction action in bundle.AllowedActions)
        {
            sb.Append("  ").Append(action.Token).Append(" — ").AppendLine(Describe(action, pt));
        }

        sb.AppendLine(pt
            ? "- Você é um mordomo de ficção sem nome; não mencione personagens de quadrinhos ou filmes."
            : "- You are a nameless fictional butler; don't mention characters from comics or films.");
        sb.AppendLine();
        sb.Append(bundle.ToText());
        return sb.ToString();
    }

    private static string Describe(AssistantAction action, bool pt) => action.Kind switch
    {
        ActionKind.Cleanup => pt ? "abre a limpeza guiada" : "opens the guided cleanup",
        ActionKind.CleanupItem => pt ? "mostra tudo o que esse item da limpeza apagaria" : "shows everything this cleanup item would delete",
        ActionKind.Memory => pt ? "abre a tela de Memória e CPU" : "opens the Memory & CPU screen",
        ActionKind.CloseApp => pt ? $"pergunta se pode fechar {action.Argument}" : $"asks to close {action.Argument}",
        ActionKind.Storage => pt ? "abre o resumo do armazenamento" : "opens the storage summary",
        ActionKind.Folders => pt ? "abre o navegador de pastas" : "opens the folder browser",
        ActionKind.Specs => pt ? "abre as especificações completas do PC" : "opens the full PC specs",
        _ => pt ? "pede a permissão da verificação completa" : "asks for the full-scan permission",
    };
}
