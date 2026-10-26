# Pennyworth

A free Windows app that explains your PC in plain language, in English and Portuguese (Brazil):
where every byte of storage goes, what can be cleaned safely (and exactly which files), which
apps are using memory and the processor right now, every part of the hardware, and — if you
want — a discreet butler who answers questions about your own PC.

## What it does

| Screen | What you get |
|---|---|
| 01 Home | The state of the PC at a glance |
| 02 Storage | Where the space on each drive goes, by category, always adding up to 100% |
| 03 Cleanup | What can be freed, grouped by how safe it is; every file listed before anything is deleted |
| 04 Memory & CPU | Apps using memory and the processor right now, with plain names for Windows processes |
| 05 Folders | A folder browser sorted by size |
| 06 Specs | Every part of the PC in plain words, with "Copy all" |
| 07 Ask | The assistant: answers from what Pennyworth measured, never changes anything by itself |
| 3D | An ASCII PC you can rotate, with live status per part |

## The assistant (07 Ask)

- **Runs on your PC** with a small model downloaded on request (Phi-4-mini on graphics cards
  with 6 GB+ of video memory, Phi-3.5-mini on the processor otherwise; MIT licence, files
  checked against pinned SHA-256 hashes), **or uses your own AI account**: any
  OpenAI-compatible endpoint, Anthropic, Google Gemini or Azure OpenAI.
- **Grounded:** the app measures the facts and applies its own diagnosis rules; the model only
  explains them. It can point to Pennyworth's screens, never act on the PC.
- **Private:** with an AI account, you see exactly what will be sent before it goes; your
  name, your user folder and personal folders are replaced by tags such as `<user>`. Keys live
  in the Windows Credential Manager. Conversations stay on the PC and can be deleted.
- **Safe:** advice such as editing the registry or disabling Windows Security is replaced
  before you see it.
- Nothing about the assistant runs, downloads or connects until you set it up.

Design and decisions: `docs/superpowers/specs/2026-10-25-ai-assistant-design.md`.

## Build and run

Requirements: Windows 10/11 x64, .NET SDK 10.0.401 (pinned in `global.json`).

```bash
dotnet run --project src/Pennyworth.App
```

```bash
dotnet test
```

Native AOT publish (needs the Visual Studio C++ build tools):

```bash
dotnet publish src/Pennyworth.App -c Release -r win-x64 -p:PublishAot=true -o publish
```

## Projects

| Project | Responsibility |
|---|---|
| `Pennyworth.Core` | Storage tree, categories, formatting |
| `Pennyworth.KnowledgeBase` | Bilingual data, one set per operating system (`Data/` for Windows, `Data/linux/` for Linux, picked by `IPlatform.Name`): known locations, processes, cleanup rules, diagnosis rules, blocked advice |
| `Pennyworth.Insights` | Path classification |
| `Pennyworth.Scanning` | Drive scanning |
| `Pennyworth.Cleanup` | Measuring and cleaning, Recycle Bin |
| `Pennyworth.Monitoring` | Live memory/CPU/GPU and hardware specs |
| `Pennyworth.Assistant` | Facts, diagnosis, context, persona prompt, action links, advice filter, anonymiser, session, history, usage |
| `Pennyworth.Assistant.Providers` | Cloud chat clients, API keys in the Credential Manager, test connection |
| `Pennyworth.Assistant.Local` | Model catalogue, hardware check, verified download, on-PC ONNX model |
| `Pennyworth.App` | Avalonia UI |
| `tools/AssistantEval` | Evaluation harness for the assistant (not shipped) |

Fonts: Golos Text and JetBrains Mono, SIL Open Font License 1.1 (licences ship with the app).
