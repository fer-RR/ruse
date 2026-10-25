# Ruse - current handoff

Keep this file compact. It is the entry point for a new session.

## Snapshot

Ruse is a Windows node-based audio router (.NET 10, NAudio 3.3, WPF host).
The default UI is WebView2 + React; the old WPF workspace remains behind
`--legacy-ui`.

**Important:** the worktree contains a large uncommitted migration. The last
commit is `9269ff1` and does not include the WebView2 UI, engine extraction,
transcriber, waveform, or recent fixes. Do not reset or clean the worktree.

## Repos and paths

- Canonical repo: `C:\Users\ih_me\Projects\Ruse` (branch `main`)
- Worktree: `C:\Users\ih_me\Projects\wt\Ruse-work` (branch `work`)
- Build output: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`
- Runtime data: `%APPDATA%\Ruse`
  - `settings.json`, `session.ruse.json`, `chains.json`, `ruse.log`
  - `models\vosk-model-small-en-us-0.15` (installed)
- Screenshots: `C:\Users\ih_me\OneDrive\Documentos\voicemet-screenshots`

## Build and test

```text
dotnet build src\Ruse.App\Ruse.App.csproj -v q --nologo
powershell -ExecutionPolicy Bypass -File C:\Users\ih_me\AppData\Local\Temp\opencode\regress-ruse.ps1 -Repo C:\Users\ih_me\Projects\wt\Ruse-work
```

Requires .NET 10 and Node.js 20+. The build installs Web UI packages if needed,
compiles `WebUi/dist`, and copies it beside the exe. `-ExecutionPolicy Bypass`
is required on this machine.

Regression modes include `--converter-test --selftest --modifier-test
--preset-test --filter-test --effects-test --align-test --chain-test
--fanout-test --record-test --asio-test --feedback-bench --uitest --web-test
--route-test --device-test --graph-test --perf-test --latency-test --delay-test
--sessions --demo --live-demo`. `--legacy-ui` selects the WPF shell.

Current status: full regression passes; `--web-test` passes; effects test reports
30 catalog entries (28 audio effects + waveform + transcriber).

## Architecture

- Engine: `src/Ruse.Engine`; no WPF dependency.
- UI: `src/Ruse.App`; default `WebMainWindow` hosts React in WebView2.
- Bridge: `WebShellController` serializes commands/snapshots on the dispatcher.
- Mixer: 48 kHz stereo float. One capture thread per source; one render thread
  per output. Each route has its own `SourceNode.Subscription` for fan-out.
- Audio never runs on the UI thread. Vosk transcription runs on a background
  worker and reads a 16 kHz PCM tap.

## Key files

```text
src/Ruse.Engine/
  Audio/SourceNode.cs             capture, subscriptions, meters, clip flag
  Audio/OutputNode.cs             render pump, routes, recorder, meter
  Audio/AudioGraph.cs             graph, route derivation, effect flattening
  Audio/BiquadProcessor.cs        filters; low-shelf a2 bug fixed
  Audio/ModifierNode.cs           effect chain, Enabled bypass
  Audio/WaveformProcessor.cs      pass-through waveform tap
  Audio/TranscriptionProcessor.cs 16 kHz PCM tap for Vosk
  Audio/PresetModel.cs            preset JSON
  Audio/Subgraph.cs               saved chains
  Diagnostics/AppLog.cs           rotating log
src/Ruse.App/
  WebMainWindow.xaml(.cs)         WebView2 host and legacy fallback
  WebShellController*.cs          catalog, graph, history, chains, settings, host
  TranscriptionService.cs         Vosk worker and session registry
  TranscriptionModels.cs          model catalog, install paths, downloads
  WebUi/src/                      React/TypeScript UI
  StartupRegistration.cs          HKCU Run entry
  MainWindow.*.cs                 legacy WPF workspace
```

## Verified features

- Capture: app/process loopback, devices, system loopback, ASIO input.
- Outputs: WASAPI, ASIO, WAV 16/24/32-float, MP3, FLAC.
- 28 audio effects + Waveform + Transcriber; presets/chains round-trip.
- Recording: format/path, per-recorder Stop, REC/SAVED/ERR feedback.
- Startup: Start with Windows, Start routing on launch.
- Transport: single Routing switch; no Stop button.
- Web UI: searchable library, app icons, speaker/headphone icons, rename
  aliases, drag/double-click add, pointer-position drop, inline cable insert,
  cable pull with large hit area and click-to-connect, undo/redo, duplicate,
  Tidy (`T`, sources left/effects center/outputs right), context menus,
  minimap, presets/sessions/settings.
- Feedback: source/output/modifier meters, glow (optional), marching cable
  packets, 2 s renewing CLIP badge on any node, active app/list glow.
- Effects: bypass power toggle (grayed/BYPASS), waveform scope, offline Vosk
  transcriber with installed-model picker and no-model Settings prompt.
- Fixes: low-shelf EQ stability, NaN/Infinity telemetry crash, non-finite audio
  guards, ASIO capture cleanup, z-order/culling, stable node positions on
  mute/delete, duplicate edge disconnect, WebView navigation restrictions.
- Legacy WPF shell remains buildable and covered by `--uitest`.

## Settings

`settings.json` includes window bounds, AutoLoadSession, HighlightMutedNodes,
MinimapMode, StartRoutingOnLaunch, SignalGlow, HighlightCableOnHover, and
TranscriberModel. Start with Windows is read/written from HKCU Run.

Transcriber models: 14 verified Vosk small models. Models are not auto-downloaded
by nodes; Settings downloads them. Installed models live under
`%APPDATA%\Ruse\models`. Node model choice persists in presets.

## Performance (2026-10-24)

Two 15 s runs after warm-up, full process tree:

| Scenario | One core | Whole 24-thread machine | Working set |
|---|---:|---:|---:|
| Legacy WPF idle | 0.36% | 0.015% | 149.6 MB |
| WebView2 idle | 0.88% | 0.037% | 480.6 MB |
| Legacy WPF demo routing | 7.81% | 0.326% | 185.0 MB |
| WebView2 demo routing | 6.72% | 0.280% | 516.8 MB |

Engine benchmark: about 60,000 frames/s mixed, 0.1-0.2% of one core, near-zero
GC allocation. WebView2 costs roughly +330 MB memory and +0.5 points idle versus
WPF; routing is within run variance. The main early web cost was 10 Hz node
telemetry churn; telemetry now uses an external store and idle polling is 1 Hz.

## Known issues and next work

- Model download UI is wired but the actual download button click was not
  end-to-end verified; the URL/download/extract path is the same one used to
  install the working English model.
- Each transcriber node/session loads its own Vosk model; multiple transcriber
  nodes can duplicate model memory.
- No model delete, cancel, progress bar, or checksum validation yet.
- Multiple routes feeding one transcriber tap can interleave their PCM.
- WPF still renders in software tier 0 on this machine; see BACKLOG.
- Commit the uncommitted migration before starting risky work.

## Conventions

- Comments explain why, not what. No emoji in code. Keep comments sparse.
- Keep `bin/`, `obj/`, `node_modules/`, `WebUi/dist/`, models, and screenshots
  out of the repo.
- Do not commit unless the user asks.
- The user reviews screenshots; keep `--demo` tidy.
