# Ruse handoff

Keep this short. New sessions start here.

## Snapshot

- Version: **0.6.7**
- Repo: `C:\Users\ih_me\Projects\Ruse` (`main`)
- Worktree: `C:\Users\ih_me\Projects\wt\Ruse-work` (`work`)
- Build: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`
- Package: `C:\Users\ih_me\Projects\Ruse-package\Ruse-0.6.7-win-x64.zip`
- Runtime data: `%APPDATA%\Ruse` (`settings.json`, `session.ruse.json`,
  `chains.json`, `frame-templates.json`, `control.json`, `ruse.log`, `models\`).
- Screenshots: `C:\Users\ih_me\OneDrive\Documentos\voicemet-screenshots`.

## Architecture

- `src/Ruse.Engine` - audio core, no WPF. NAudio + OwnAudioVst.
- `src/Ruse.App` - WPF/WebView2 host, command bridge, persistence, speech,
  translation, TTS model management, control API.
- `src/Ruse.TtsHost` - child-process SherpaOnnx TTS host.
- `GraphEngine` renders every source/modifier once on one thread; output pumps
  and source captures run independently. Taps use worker threads.
- DSP is 48 kHz stereo float. SRC is band-limited windowed-sinc. VST3 is
  isolated by default with watchdog/restart.
- UI: React + `@xyflow/react` in `src/Ruse.App/WebUi`; JSON bridge from
  `WebShellController`.

## Current state (since 0.6.1)

- **Control API:** loopback HTTP + WebSocket, bearer token, discovery file
  `%APPDATA%\Ruse\control.json`. Endpoints for state, nodes, parameters, node
  gain/mute/solo/bypass, transport, telemetry. WebSocket push events.
  Persisted per-node `ControlId` plus node-ID/title fallback.
  Settings > Behavior > Control API toggle, port, URL, masked token with
  reveal/hide. `ControlApiEnabled`/`ControlApiPort` in `settings.json`.
- **Noise suppression:** Load noise floor, cached 5 s / 1024-point FFT profile,
  257-tap linear-phase FIR, `Remove` 0-100% slider. Profile path persists in
  sessions, presets, frame templates and saved chains. Adaptive NAudio stage
  remains; profile filter is additive.
- **UI:** chain editor drag-handle sorting; library category sorting persisted
  in localStorage; shortcut hints in tooltips/context menu; new Space
  transport, Ctrl+Shift+M mic mute, Ctrl+, settings, F5 refresh, B bypass;
  custom `NumberField` arrows that scale with field height; control ID shown on
  nodes when diagnostics is on.
- **Visualizers:** spectrogram telemetry is incremental (new columns plus a
  monotonic counter, full snapshot on reset/reload); history is now 256
  columns (~8.2 s); UI uses a native 256x128 offscreen canvas + color LUT +
  cached labels. Spectrum animates at 30 fps between 10 Hz snapshots and caches
  its static grid/gradient.
- **Network audio:** block sizes are split instead of truncated (previous
  fix); receive pump uses a high-resolution timer; sender restarts are
  accepted via session IDs.

## Build, test, package

```text
dotnet build src\Ruse.App\Ruse.App.csproj -v q --nologo
cd src\Ruse.App\WebUi && npm test
powershell -ExecutionPolicy Bypass -File scripts\regress-ruse.ps1
powershell -ExecutionPolicy Bypass -File scripts\package-ruse.ps1
powershell -ExecutionPolicy Bypass -File scripts\transparency-test.ps1
```

Useful modes: `--web-test`, `--effects-test`, `--chain-test`, `--selftest`,
`--control-test`, `--demo`, `--live-demo --pid <pid>`,
`--monitor-test --pid <pid>`. Close Ruse before building; it locks the debug
exe. Last verified: clean build, `--web-test`, `--effects-test`, `--chain-test`,
control API, and the visualization/telemetry harness.

## Known / next

- Optional visualizer work and API/noise follow-ups are in `docs/BACKLOG.md`.
- `work` is not automatically synced; merge when asked.
- Native status/history text, device names and engine errors remain English.
- VST3 editor-only state is not restored after a watchdog restart; editor
  parameter changes are not polled back to sliders. VST3 sidechain buses and
  VST3 MIDI are unimplemented.
- ASIO needs real loopback/insert testing. Per-app capture needs Windows 11 or
  Server 2022. Engine sample rate is fixed at 48 kHz.
- Clip guard is zero-latency sample-peak, not true-peak look-ahead.
- Control API HTTP has no CORS policy; browser dashboards need one.

## Key files

```text
src/Ruse.Engine/Audio/
  GraphEngine.cs, AudioGraph.cs, AudioEngineConfig.cs
  SourceNode.cs, OutputNode.cs, FloatRingBuffer.cs, FormatConverter.cs
  NaudioEffects.cs, NaudioEffectProcessor.cs
  NoiseSuppressionProcessor.cs, INoiseProfileProcessor.cs
  SpectrumProcessor.cs, SpectrogramProcessor.cs, FftAnalyzer.cs
  PresetModel.cs, Subgraph.cs, FrameTemplateLibrary.cs
  Vst3Processor.cs, IsolatedVst3Processor.cs
src/Ruse.App/
  ControlServer.cs, ControlApiModels.cs, WebShellController.Control.cs
  WebShellController*.cs
  WebUi/src/App.tsx, components/, locales/, shortcuts.ts
scripts/
  regress-ruse.ps1, package-ruse.ps1, transparency-test.ps1
```

## Conventions

- Comments explain why, not what. Keep them short. No emoji.
- UI text uses `useI18n().t('English')`; update all locales.
- Effect IDs and parameter names are stable (presets depend on them).
- Keep `bin/`, `obj/`, `node_modules/`, `WebUi/dist/`, models, screenshots and
  package output out of the repo.
- Do not commit or merge unless the user asks.
