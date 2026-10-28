# Ruse handoff

Keep this file short. New sessions start here.

## Snapshot

Ruse is a Windows node-based audio router: .NET 10, NAudio 3.3, WPF host,
WebView2 + React UI, Whisper.net for speech, LLamaSharp for translation and
OwnAudioVst for VST3. There is one UI; the old WPF workspace was removed.

- Canonical repo: `C:\Users\ih_me\Projects\Ruse` (`main`, stale at `240d065`)
- Worktree: `C:\Users\ih_me\Projects\wt\Ruse-work` (`work`, `a4af8c5` plus a
  large uncommitted batch). Do not reset or clean it.
- Build: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`
- Package: `C:\Users\ih_me\Projects\Ruse-package\Ruse-win-x64-2026-10-28.zip`
  (+ `.sha256`); self-contained win-x64, WebUI included, models/VSTs excluded.
- Runtime data: `%APPDATA%\Ruse` (`settings.json`, `session.ruse.json`,
  `chains.json`, `ruse.log`, `models\`).
- Screenshots: `C:\Users\ih_me\OneDrive\Documentos\voicemet-screenshots`.

Uncommitted batch: onboarding/splash, 9 locales, tray behavior, file player,
translator, VST3 host, workspace/sidebar cleanup, cable performance, package
publish fix. Commit only when the user asks.

## Build and test

```text
dotnet build src\Ruse.App\Ruse.App.csproj -v q --nologo
powershell -ExecutionPolicy Bypass -File C:\Users\ih_me\AppData\Local\Temp\opencode\regress-ruse.ps1 -Repo C:\Users\ih_me\Projects\wt\Ruse-work
```

Needs .NET 10 and Node.js 20+. The build runs `WebUi/dist`; publish also copies
it via `CopyWebUiAssetsToPublish`. Close a running Ruse before building; it
locks `bin\Debug\net10.0-windows\Ruse.exe`.

Current status: build, `--web-test`, `--effects-test`, `--selftest`, the
regression script, translation tests and VST3 load/preset/route/editor/scan
tests pass. Legacy `--legacy-ui`, `--uitest`, `--feedback-bench` and
`--perf-test` are gone.

Useful modes: `--demo`, `--live-demo --pid <pid>`, `--translation-e2e-test`,
`--vst3-test`, `--vst3-route-test`, `--vst3-editor-test`, `--vst3-scan-test`.
Test fixtures already on this machine: Whisper medium `.en`/multilingual,
Qwen2.5 1.5B, and VST3 `ring-out`, `rnnoise`, `DirtyFilterPlus`.

## Architecture

- Engine: `src/Ruse.Engine` (no WPF). UI: `src/Ruse.App`; `WebShellController`
  serializes commands/snapshots on the dispatcher.
- Mixing: 48 kHz stereo float. One capture thread per source, one render thread
  per output. Every route owns a `SourceNode.Subscription` for fan-out.
- Sources: process loopback, devices, system loopback, ASIO and paced audio-file
  playback.
- Taps (waveform, dB meter, transcriber, translator) use hidden monitor outputs
  and never enter the mix.
- Whisper and translation model loads are preflighted in child processes.
  Translation generation is serialized across sessions.
- Speech and translation run on **CPU** in the shipped build:
  `Whisper.net.Runtime` + `LLamaSharp.Backend.Cpu`, `GpuLayerCount = 0`.
  CUDA/Vulkan backends are possible but not packaged.
- VST3: one in-process `Vst3Processor` per node, shared by its routes. Fast
  scan at startup/refresh; native editor is a separate window; state is a
  base64 chunk in presets. VST2 is intentionally unsupported.
- Route chains rebuild atomically under a lock. Shared VST processors are not
  cloned; managed effects are cloned per route.

## Key files

```text
src/Ruse.Engine/Audio/
  SourceNode.cs, AudioFileCapture.cs       sources and file playback
  OutputNode.cs                            render pump, routes, alignment, recorder
  AudioGraph.cs                            graph and route derivation
  ModifierNode.cs                          effect chains and disposal
  Vst3Processor.cs                         OwnAudioVst adapter
  TranslatorProcessor.cs                   translation tap
  PresetModel.cs, Subgraph.cs              presets and saved chains
src/Ruse.App/
  WebMainWindow.xaml(.cs), TrayIcon.cs     WebView2 host and tray
  WebShellController*.cs                   bridge, catalog, graph, persistence
  TranscriptionService.cs, TranscriptionModels.cs
  TranslationService.cs, TranslationModels.cs, TranslationModelCache.cs
  TranslationModelProbe.cs, TranslationLanguages.cs
  StartupErrorDialog.xaml(.cs)             WebView2 startup failure
  Vst3SelfTest.cs                          VST3 diagnostics
  WebUi/src/                               React workspace and locales
```

## Known issues / next

- Native status/history text, device/model names and engine errors are English.
  Review the seven machine-translated locales and add a `t()` coverage check.
- Translation is chunked (roughly 2–12 s lag); no VAD, timestamps or SRT/VTT.
- Qwen 0.5B is approximate, 1.5B is the default, 3B is slower and needs RAM.
- VST3 is in-process, so a crashing plugin can still take Ruse down. An
  out-of-process host is the next robustness step.
- A VST3 node shares one plugin across its routes; avoid fan-out to several
  outputs until per-route instances exist. Editor parameter edits are not
  polled back into node sliders.
- Translator/VST3 type ids, presets and test modes are stable; keep them.
- File player has no seek/play-pause deck. Tray icon may need a restart after
  toggling. Duplicate detection depends on the current render session.
- GPU backends, real-microphone translator/VST3 testing, SRT export, mixer view,
  sidechain/MIDI and CLAP remain in `docs/BACKLOG.md`.

## Conventions

- Comments explain why, not what. Keep them short. No emoji.
- UI text goes through `useI18n().t('English')`; update every locale file.
- Effect ids and parameter names are stable (presets depend on them).
- Keep `bin/`, `obj/`, `node_modules/`, `WebUi/dist/`, models and screenshots
  out of the repo. Do not commit or merge unless the user asks.
- Demo graphs are reviewed visually; keep `--demo` tidy.
