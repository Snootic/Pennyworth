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

The .NET SDK is pinned in `global.json` (10.0.401). The same two commands work on both systems:

```bash
dotnet run --project src/Pennyworth.App
dotnet test --solution Pennyworth.slnx
```

### Windows

Requirements: Windows 10/11 x64.

Native AOT publish (needs the Visual Studio C++ build tools):

```bash
dotnet publish src/Pennyworth.App -c Release -r win-x64 -p:PublishAot=true -o publish
```

### Linux

Requirements: x64 Linux with glibc 2.27+ and an X11 or Wayland session; `libicu` and `libfontconfig`
(present on every common desktop). `libsecret-1` is optional: without it Pennyworth starts, says in one
sentence that API keys cannot be saved, and turns off only the keyring. Nothing asks for a password.

The window uses Wayland natively inside a Wayland session and X11 otherwise;
`PENNYWORTH_DISPLAY=x11` keeps X11 (XWayland) inside a Wayland session.

Release artefacts (a self-contained tarball and an AppImage, no .NET needed on the target machine):

```bash
tools/package/build-linux.sh --version 0.1.0     # writes artifacts/linux-x64/
tools/package/smoke-linux.sh --tarball artifacts/linux-x64/pennyworth-0.1.0-linux-x64.tar.gz
```

The script publishes `linux-x64` self-contained, lays the result out as `bin/`, `lib/pennyworth/` and `share/`
(desktop file and `hicolor` icons), and builds the AppImage with a pinned, checksum-verified `appimagetool`
(downloaded on first use). `--aot` publishes with Native AOT instead (needs clang and zlib headers),
`--no-appimage` stops after the tarball and the AppDir. The smoke script starts a package with an empty
environment and no SDK in a private Xvfb (`--display wayland`: a private headless sway, no X server) and
checks that the window opened, that its class matches the desktop file, and that something was drawn.

The launcher icons are generated from the brand sources:
`python -I tools/brand/build_logo_assets.py docs/design/brand src/Pennyworth.App/Assets tools/package/linux/icons`
(needs Pillow).

CI (`.github/workflows/`): `linux.yml` builds, tests, packages, and launches the packages under Xvfb and under a
Wayland compositor, keeping the screenshots as artifacts; `windows.yml` builds and tests on Windows.

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
