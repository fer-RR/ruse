# Ruse handoff

Keep this file short. New sessions should be able to start here.

## Snapshot

Ruse is a Windows node-based audio router: .NET 10, NAudio 3.3, WPF host,
WebView2 + React UI. The Web UI is the default; `--legacy-ui` opens the old WPF
workspace.

- Canonical repo: `C:\Users\ih_me\Projects\Ruse` (`main`)
- Worktree: `C:\Users\ih_me\Projects\wt\Ruse-work` (`work`)
- Build: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`
- Runtime data: `%APPDATA%\Ruse` (`settings.json`, `session.ruse.json`,
  `chains.json`, `ruse.log`, `models\`)
- Screenshots: `C:\Users\ih_me\OneDrive\Documentos\voicemet-screenshots`

Committed checkpoints: `1083331` (engine/transcriber/recorder/Graphic EQ/WASAPI
fallback) and `a4af8c5` (WebView workspace/diagnostics polish). The worktree has
an uncommitted batch on top: onboarding/splash, 9 UI languages, tray behavior,
file player, sidebar/search refinements. Do not reset or clean it.

## Build and test

```text
dotnet build src\Ruse.App\Ruse.App.csproj -v q --nologo
powershell -ExecutionPolicy Bypass -File C:\Users\ih_me\AppData\Local\Temp\opencode\regress-ruse.ps1 -Repo C:\Users\ih_me\Projects\wt\Ruse-work
```

Needs .NET 10 and Node.js 20+. The build compiles `WebUi/dist` and copies it
beside the exe. Current status: build, `--web-test`, `--effects-test` and
`--preset-test` pass; full regression passed before the latest UI batch.

## Architecture

- Engine: `src/Ruse.Engine`, no WPF. UI: `src/Ruse.App`; `WebShellController`
  serializes commands/snapshots on the dispatcher.
- Mixing: 48 kHz stereo float. One capture thread per source, one render thread
  per output. Every route gets its own `SourceNode.Subscription` for fan-out.
- Sources: app/process loopback, capture devices, system loopback, ASIO, and
  `AudioFileCapture` (real-time WAV/MP3/FLAC/etc. file playback, optional loop).
- Taps (waveform, dB meter, transcription) never enter the output mix;
  `AudioGraph.RefreshTapMonitors` gives each tap a hidden monitor output.
- Whisper: model load is preflighted in a disposable child process; recognition
  runs on a background worker. Shared model cache, per-node sessions.
- Diagnostics: `--selftest` suite plus an optional canvas node. CPU from process
  counters, GPU from Windows GPU Engine PDH, audio latency/backlog from live
  sources/outputs. Node position/scale live in WebView2 localStorage.
- Duplicate audio: only warns when the route output is the same endpoint the app
  is already playing on. Different outputs do not warn. Banner is dismissable.
- Welcome: launch splash (info, language, “don’t show again”, setup button);
  four-step guide: source → output → test → basics.
- UI languages: English, pt-BR, es, fr, de, it, ja, ko, zh-CN. pt-BR is reviewed;
  the other seven are machine-translated and need native review.
- Window: resizable from all edges with a minimum tracking size. Minimize can
  hide to a native tray icon; click restores, menu offers Open/Exit.
- Library: search is an icon beside the filters; filters collapse to a select on
  narrow sidebars. Node colors mirror the library palette.

## Key files

```text
src/Ruse.Engine/
  Audio/SourceNode.cs              capture, fan-out, file-player dispatch
  Audio/AudioFileCapture.cs        paced audio-file playback
  Audio/OutputNode.cs              render pump, routes, recorder
  Audio/AudioGraph.cs              graph + route derivation
  Audio/GraphicEqualizerProcessor.cs  10-band ISO GraphicEqualizer wrapper
  Audio/DbMeterProcessor.cs        RMS/peak dBFS tap
  Audio/TranscriptionProcessor.cs  16 kHz tap + decimator
  Audio/PresetModel.cs             preset JSON; source FilePath/FileLoop
  Audio/Subgraph.cs                saved chains
src/Ruse.App/
  WebMainWindow.xaml(.cs)          WebView2 host, tray/minimize behavior
  TrayIcon.cs                      native tray icon and menu
  WebShellController*.cs           bridge, catalog, graph, history, persistence
  WebShellController.Diagnostics.cs  CPU/GPU/audio diagnostics
  GpuUsage.cs                      PDH GPU Engine sampler
  TranscriptionService/Models.cs   Whisper workers, model catalog
  ModelProbe.cs                    child-process model load check
  WebUi/src/components/            React nodes, dialogs, splash/wizard, sidebar
  WebUi/src/locales/               pt-BR + es/fr/de/it/ja/ko/zh-CN
  WebUi/src/i18n.tsx               language list/fallback
  MainWindow.*.cs                  legacy WPF workspace
```

## Recent behavior

- Effects: 29 audio effects + waveform/dB-meter/transcriber nodes. Graphic EQ is
  NAudio’s click-free 10-band `GraphicEqualizer`.
- dB meter: compact, vertical by default; orientation in its context menu.
- File player: choose file, loop, restart; routes like any source; saved in
  sessions/presets.
- Canvas: fixed node widths, resizable frames, no per-node resize.
- Cables: active-source animation only; telemetry pauses while dragging or
  pulling a cable; connection preview is driven by pointer + rAF.
- Diagnostics node: draggable, scalable, closable; Settings > Info > For nerds.

## Known issues / next

- Native status/history text, device/model names and engine errors remain English.
- Review the seven generated locales; add an automated coverage check.
- File player has no seek/position UI or play/pause deck; only loop/restart.
- Tray icon is created at startup when enabled; toggling the setting may need a
  restart for the icon to appear.
- Duplicate detection depends on the app’s current render session; devices can
  move or be absent from the session list.
- VST3/CLAP needs a native C++ host; not started.
- WPF render tier is shown in Settings > Info; tier 2 reported recently.

## Conventions

- Comments explain why, not what. Keep them short. No emoji.
- UI text goes through `useI18n().t('English')`; update every locale file.
- Effect ids and parameter names are stable (presets depend on them).
- Keep `bin/`, `obj/`, `node_modules/`, `WebUi/dist/`, models and screenshots out
  of the repo.
- Do not commit or merge unless the user asks.
- Demo graphs are reviewed visually; keep `--demo` tidy.
