# Ruse handoff

Keep this file short. New sessions start here.

## Snapshot

Ruse is a Windows node-based audio router: .NET 10, NAudio 3.3, WPF host,
WebView2 + React UI, Whisper.net for speech, LLamaSharp/Qwen and optional
ONNX Runtime OPUS-MT for translation, and OwnAudioVst for VST3. There is one UI;
the old WPF workspace was removed.

- Canonical repo: `C:\Users\ih_me\Projects\Ruse` (`main`, stale at `240d065`)
- Worktree: `C:\Users\ih_me\Projects\wt\Ruse-work` (`work`, clean; latest feature
  commit `113c428`).
  Do not reset or clean it.
- Build: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe` (version 0.2.0-beta.1)
- Package: `C:\Users\ih_me\Projects\Ruse-package\Ruse-0.2.0-beta.1-win-x64.zip`
  (+ `.sha256`, SHA-256 `84c5e375...a5d0f519`); self-contained win-x64,
  WebUI and LICENSE included, models/VSTs excluded.
- Runtime data: `%APPDATA%\Ruse` (`settings.json`, `session.ruse.json`,
  `chains.json`, `ruse.log`, `models\`).
- Screenshots: `C:\Users\ih_me\OneDrive\Documentos\voicemet-screenshots`.

Latest batch: onboarding/splash, 9 locales, tray behavior, file player,
translator, OPUS-MT provider, VST3 isolation, effects, analysis taps,
per-app capture fallback, workspace/sidebar cleanup, package publish fix.

## Build and test

```text
dotnet build src\Ruse.App\Ruse.App.csproj -v q --nologo
powershell -ExecutionPolicy Bypass -File C:\Users\ih_me\AppData\Local\Temp\opencode\regress-ruse.ps1 -Repo C:\Users\ih_me\Projects\wt\Ruse-work
```

Needs .NET 10 and Node.js 20+. The build runs `WebUi/dist`; publish also copies
it via `CopyWebUiAssetsToPublish`. Close a running Ruse before building; it
locks `bin\Debug\net10.0-windows\Ruse.exe`.

Current status: build, `--web-test`, `--effects-test` (36 processors), `--selftest`,
the regression script, translation tests and VST3 load/preset/route/editor/scan/
isolated tests, OPUS-MT engine/download tests, and the per-app capability contract pass.
Legacy `--legacy-ui`, `--uitest`, `--feedback-bench` and `--perf-test` are gone.

Useful modes: `--demo`, `--live-demo --pid <pid>`, `--translation-e2e-test`
(add `--opus` to use OPUS-MT), `--opus-test`, `--opus-download-test`,
`--vst3-test`, `--vst3-isolated-test`, `--vst3-route-test`, `--vst3-editor-test`,
`--vst3-scan-test`.
Test fixtures already on this machine: Whisper medium `.en`/multilingual,
Qwen2.5 1.5B, OPUS-MT en/de/es/fr/it/pt/zh and de/es/fr/it/pt/ja/zh/ko-to-en
pairs, and VST3 `ring-out`, `rnnoise`, `DirtyFilterPlus`.

## Architecture

- Engine: `src/Ruse.Engine` (no WPF). UI: `src/Ruse.App`; `WebShellController`
  serializes commands/snapshots on the dispatcher.
- Mixing: 48 kHz stereo float. One capture thread per source, one render thread
  per output. Every route owns a `SourceNode.Subscription` for fan-out.
- Sources: process loopback, devices, system loopback, ASIO and paced audio-file
  playback.
- Taps (dB meter, FFT spectrum, scrolling spectrogram, transcriber, translator)
  use hidden monitor outputs and never enter the mix. The FFT is 2048-point
  Hann-windowed, 64 log-spaced bands, refreshed about 23 times per second.
- Whisper and Qwen model loads are preflighted in child processes. Qwen
  generation is serialized across sessions; OPUS-MT runs in managed ONNX
  Runtime and is serialized by an engine-wide lock.
- Speech and translation run on **CPU by default**. The build ships the Vulkan
  runtimes; the GPU option sets Whisper `UseGpu` and LLamaSharp
  `GpuLayerCount = 99`, with automatic CPU fallback. OPUS-MT uses ONNX Runtime
  on the CPU.
- VST3: one processor per node, shared by its routes. Plugins run in a child
  host process by default (`IsolatedVst3Processor` + named pipes); Settings >
  Plugins can switch back to the in-process adapter. Fast scan at startup/
  refresh; native editor is a separate window; state is a base64 chunk in
  presets. VST2 is intentionally unsupported.
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
  IsolatedVst3Processor.cs, Vst3HostRunner.cs  out-of-process host
  FftAnalyzer.cs, SpectrumProcessor.cs,
  SpectrogramProcessor.cs                  FFT analysis taps
  TranslatorProcessor.cs                   translation tap
  OpusTranslationModels.cs, OpusMtEngine.cs  optional OPUS-MT catalog and ONNX engine
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
- Translation is chunked (roughly 2–12 s lag). VAD, sentence timestamps and
  SRT/VTT/TXT export are in place; cue timing is approximate.
- Qwen 0.5B is approximate, 1.5B is the default, 3B is slower and needs RAM.
- Bluetooth output could not be retested on the dev PC (no paired endpoint). The
  engine mix is fixed at 48 kHz; use the Stereo endpoint, not Hands-Free/16 kHz.
- Per-app capture needs Windows 10 build 20348 or later. Older builds hide the
  Applications list, show a banner, and use System audio instead. Existing app
  sources fail quietly once instead of retrying on every route change.
- OPUS-MT is optional and CPU-only. English to Japanese/Korean is not offered;
  use Qwen for those targets. Each pair is ~100 MB; a non-English pivot needs
  two models. English to Portuguese uses the en-ROMANCE model with a prefix.
- VST3 isolation covers plugin crashes; if the host dies the node falls back
  to dry audio and shows an error. It uses synchronous pipe audio, so a hung
  (not crashed) plugin can still stall its render thread. No auto-restart yet.
- A VST3 node shares one plugin across its routes; avoid fan-out to several
  outputs until per-route instances exist. Editor parameter edits are not
  polled back into node sliders.
- Translator/VST3 type ids, presets and test modes are stable; keep them.
- File player has seek, play/pause and a time readout; it still has no dedicated
  per-node output selector. Tray icon updates live. Duplicate detection depends
  on the current render session.
- GPU backends, real-microphone translator/VST3 testing, mixer view,
  sidechain/MIDI and CLAP remain in `docs/BACKLOG.md`.

## Conventions

- Comments explain why, not what. Keep them short. No emoji.
- UI text goes through `useI18n().t('English')`; update every locale file.
- Effect ids and parameter names are stable (presets depend on them).
- Keep `bin/`, `obj/`, 
ode_modules/`, `WebUi/dist/`, models and screenshots
  out of the repo. Do not commit or merge unless the user asks.
- Demo graphs are reviewed visually; keep `--demo` tidy.
