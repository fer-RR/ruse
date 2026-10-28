# Ruse - current handoff

Keep this file compact. It is the entry point for a new session.

## Snapshot

Ruse is a Windows node-based audio router (.NET 10, NAudio 3.3, WPF host).
The default UI is WebView2 + React; the old WPF workspace remains behind
`--legacy-ui`.

**Important:** the WebView2/engine migration is committed as `d25d923`. The
worktree now has a larger uncommitted set: the transcriber overhaul (tap
monitor, resampler, shared model cache, model manager, transcript actions) plus
recent UI work (diagnostics node, collapsible library, Graphic EQ, compact dB
meter, palette and window fixes). Do not reset or clean the worktree.

## Repos and paths

- Canonical repo: `C:\Users\ih_me\Projects\Ruse` (branch `main`)
- Worktree: `C:\Users\ih_me\Projects\wt\Ruse-work` (branch `work`)
- Build output: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`
- Runtime data: `%APPDATA%\Ruse`
  - `settings.json`, `session.ruse.json`, `chains.json`, `ruse.log`
  - `models\whisper-*.bin` (downloaded Whisper models)
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

Current status: build, `--web-test`, `--effects-test` and `--preset-test` pass
after the latest UI changes; the full regression baseline passed before them.
The effects test reports 32 catalog entries: 29 audio effects plus Waveform,
dB meter and Transcriber. Graphic EQ uses NAudio's MIT `GraphicEqualizer`
(10 ISO octave bands, click-free changes), so it needs no new dependency.
Whisper recognition was verified with synthesized US English audio through both
the Whisper.net stream API and the float-sample API. A model that crashes the
native loader is caught by a child-process probe and reported in the node
instead of killing Ruse.

## Architecture

- Engine: `src/Ruse.Engine`; no WPF dependency.
- UI: `src/Ruse.App`; default `WebMainWindow` hosts React in WebView2.
- Bridge: `WebShellController` serializes commands/snapshots on the dispatcher.
- Mixer: 48 kHz stereo float. One capture thread per source; one render thread
  per output. Each route has its own `SourceNode.Subscription` for fan-out.
- Taps: waveform/transcriber processors are never mixed into output routes.
  `AudioGraph.RefreshTapMonitors` gives each tap modifier a hidden real-time
  monitor output, so a tap works with no output node and is fed once per block.
- Audio never runs on the UI thread. Whisper transcription runs on a
  background worker and reads a 16 kHz mono PCM tap. One Whisper factory is
  shared by all sessions that use it; each session has its own processor.
- Model loading is preflighted in a disposable child process
  (`Ruse.exe --model-probe <path> <result>`). Whisper is native code and a bad
  model can raise an uncatchable access violation; the probe turns that into an
  error in the node instead of a process crash. Results are cached per model
  file for the app run.
- First run: `--demo`/tests excluded, `WebShellController` posts `welcome` when
  there is no saved session and `OnboardingCompleted` is false. The React
  `WelcomeWizard` builds the first source → output route via `first-route`.
- Duplicate audio: `AudioSessionEnumerator` reports each app session's render
  endpoint. `BuildGraphWarnings` warns only when that endpoint is also the
  route's output endpoint; copies to a different device stay silent. The
  banner, node badges and help dialog use the result (skipped in demo/UI tests).
  The banner can be dismissed; a different warning shows again.
- Diagnostics: Settings > Info > For nerds adds a UI-only diagnostics node to
  the canvas. It is draggable, scalable (CSS zoom), closable and stores its
  position/scale in WebView2 localStorage. CPU comes from process counters, GPU
  from the Windows GPU Engine PDH counters (`GpuUsage`), audio latency/backlog
  from the live sources/outputs.
- Device catalog: virtual render devices are listed once (Virtual inputs), not
  duplicated under Output devices. Items carry direction/provider metadata and
  an on-canvas check; the library groups capture/playback/ASIO/virtual buses.
  Library sections start collapsed, and the whole sidebar can retract to a slim
  expandable rail. Node accents mirror the library category palette.
- App follow: active apps are scanned every 750 ms while `FollowPlayingApps` is
  enabled (4 s otherwise), so a newly playing app is added and connected quickly.
  `FollowPlayingApps` stores each app source's process name; when the app
  restarts and is active, `ReattachPlayingApps` retargets that source to the new
  process, keeping its existing route.
- Viewport: `ReactFlow` has no `fitView` prop. It fits once when the first
  non-empty graph arrives and restores the previous viewport on later graph
  updates, so disconnects do not move the canvas.
- UI language: `WebUi/src/i18n.tsx` uses English source strings as keys and
  falls back to English when a locale is missing. `WebUi/src/locales/pt-BR.ts`
  is the first translation. `UiLanguage` is stored in settings; native status
  text and device names are intentionally left in English.

## Key files

```text
src/Ruse.Engine/
  Audio/SourceNode.cs             capture, subscriptions, meters, clip flag
  Audio/OutputNode.cs             render pump, routes, recorder, meter
  Audio/AudioGraph.cs             graph, route derivation, effect flattening
  Audio/BiquadProcessor.cs        filters; low-shelf a2 bug fixed
  Audio/GraphicEqualizerProcessor.cs  NAudio 10-band ISO GraphicEqualizer wrapper
  Audio/ModifierNode.cs           effect chain, Enabled bypass
  Audio/WaveformProcessor.cs      pass-through waveform tap
  Audio/DbMeterProcessor.cs       pass-through RMS/peak dBFS tap
  Audio/TranscriptionProcessor.cs 16 kHz tap, anti-alias + 3:1 decimator
  Audio/PresetModel.cs            preset JSON
  Audio/Subgraph.cs               saved chains
  Diagnostics/AppLog.cs           rotating log
src/Ruse.App/
  WebMainWindow.xaml(.cs)         WebView2 host and legacy fallback
  WebShellController*.cs          catalog, graph, history, chains, settings, host
  WebShellController.Diagnostics.cs  CPU/process/audio diagnostics snapshot
  GpuUsage.cs                     PDH GPU Engine usage sampler
  TranscriptionService.cs         Whisper workers, shared model cache, sessions
  TranscriptionModels.cs          model catalog, verified install, downloads
  ModelProbe.cs                   child-process model load check
  WebUi/src/components/WelcomeWizard.tsx  first-run source → output guide
  WebUi/src/components/DiagnosticsNode.tsx  live canvas diagnostics node
  WebUi/src/diagnostics.ts        diagnostics node position/scale storage
  WebUi/src/components/ModelCatalog.tsx   language/tier model cards
  WebUi/src/components/Dialogs.tsx        settings tabs, duplicate-audio help
  WebUi/src/i18n.tsx                      UI language context and translate()
  WebUi/src/locales/pt-BR.ts              Portuguese UI strings
  WebUi/src/                      React/TypeScript UI
  StartupRegistration.cs          HKCU Run entry
  MainWindow.*.cs                 legacy WPF workspace
```

## Verified features

- Capture: app/process loopback, devices, system loopback, ASIO input.
- Outputs: WASAPI, ASIO, WAV 16/24/32-float, MP3, FLAC.
- 29 audio effects + Waveform + dB meter + Transcriber; presets/chains
  round-trip. Graphic EQ is NAudio's 10-band ISO octave equalizer (±12 dB).
- Recording: format/path, per-recorder Stop, REC/SAVED/ERR feedback.
- Startup: Start with Windows, Start routing on launch.
- Transport: compact Routing switch showing only ON/OFF.
- Web UI: searchable library, app icons, speaker/headphone icons, rename
  aliases, drag/double-click add, pointer-position drop, inline cable insert,
  cable pull with large hit area and click-to-connect, undo/redo, duplicate,
  Tidy (`T`, sources left/effects center/outputs right), context menus,
  minimap, presets/sessions/settings. The library starts with sections closed;
  the icon-only chevron retracts it to a same-palette rail.
- Library: All / Sources / Destinations / Processing tabs; sections for
  Applications, Microphones & inputs, ASIO inputs, Virtual buses, Speakers &
  headphones, ASIO outputs, Virtual inputs and Recording. Each device shows
  capture/playback direction; already-used outputs show a check instead of an
  add button. A device guide explains Voicemeeter Out vs In and CABLE sides.
  Node accents follow the library palette: apps blue, system cyan, microphones
  yellow, virtual buses deep purple, virtual outputs/inputs light purple,
  outputs green, recorders coral, effects amber and chains violet.
- Routing: the viewport is preserved when cables/nodes change (one initial
  fit only). App sources can follow process restarts when enabled.
- Canvas groups: Ctrl-drag empty canvas to draw a named background frame, hold
  Alt and drag an edge/corner to resize, double-click the title to rename, and
  remove with the header × (asks whether to delete nodes inside). Frames sit
  behind nodes/cables, above the background, and are saved in sessions/presets.
- Window chrome: the custom title bar uses a native caption drag, so the
  window can be moved or restored from maximized by dragging it. The window is
  resizable from all edges; WPF's minimum tracking size is reapplied for the
  frameless WebView2 host, and the toolbar wraps instead of clipping.
- Feedback: per-node meter bars were removed for performance. Active-signal
  glow (optional), marching cable packets, a 2 s renewing CLIP badge, active
  app/list glow, waveform tap, and a dedicated dB meter node (compact, vertical
  by default; switch to horizontal from its context menu). The waveform tap has
  Gain, Smoothing and Scroll speed controls.
- Recording: recorder nodes use persistent Off / Armed / On state. On starts
  immediately, Armed starts on the first audio, and Off stops and saves. Auto
  save writes timestamped files under Music/Ruse Recordings; removing an active
  recorder or closing the app asks for confirmation.
- Effects: bypass power toggle (grayed/BYPASS), waveform scope, 10-band
  Graphic EQ, compact dB meter (vertical/horizontal), standalone offline
  Whisper transcriber (no output required) with live level, status,
  clear/copy/export/retry and a per-node model picker.
- Speech models: 10 OpenAI Whisper models in English and Multilingual groups,
  backed by Whisper.net/whisper.cpp. Settings shows per-model size, installed
  size, download progress, cancel, delete and "open models folder". Models are
  downloaded as `.bin` files and work offline. One factory is shared by all
  nodes that use it. Model loading is isolated in a child probe because the
  native runtime can crash on a corrupt file.
- Settings: tabbed Behavior / Workspace / Speech / Shortcuts / Info dialog,
  movable by its header. Options include restore-last-session, cable animation,
  snap-to-grid and the UI language; Shortcuts (also F1) lists the keyboard map.
  Info shows versions, WebView2, render tier, folders, installed model disk use,
  third-party licences and diagnostics actions. "For nerds" toggles the live
  diagnostics canvas node (audio, delays, CPU/GPU, memory, FPS, UI frame, heap).
  Behavior has "Show welcome guide" and "Open Windows sound settings".
- UI languages: English and Português (Brasil), switchable live from Settings
  > Workspace. The React shell, settings, wizard, duplicate-audio help, model
  catalog, node labels and transcription panel are translated.
- First run: a three-step wizard picks a source, an output, warns about
  same-device duplication, creates the route in one step and shows a live
  source meter plus a "I can't hear it" troubleshooting list.
- Duplicate audio: source sessions are checked against the output endpoint.
  A same-device route shows an amber banner, DUP badges and a required
  acknowledgment in the wizard. A different-device copy (the original still
  plays on its own endpoint) shows a cyan info banner and COPY badges so the
  user knows both paths exist. Both open the "Avoid hearing it twice" dialog
  (different output / virtual cable / monitoring). The banner has an X dismiss
  control; it stays hidden for the same warning and returns for a new one.
- Fixes: low-shelf EQ stability, NaN/Infinity telemetry crash, non-finite audio
  guards, ASIO capture cleanup, z-order/culling, stable node positions on
  mute/delete and after a drag followed by a disconnect, duplicate edge
  disconnect, WebView navigation restrictions, frameless-window minimum size,
  smooth sidebar resizing, fixed node widths (frame resizing remains),
  auto-add detection latency cut from 4 s to 750 ms while following apps, and
  WASAPI low latency is now optional so CABLE/Voicemeeter endpoints fall back
  to standard shared mode instead of failing to start.
- Legacy WPF shell remains buildable and covered by `--uitest`.

## Settings

`settings.json` includes window bounds, AutoLoadSession, OnboardingCompleted,
FollowPlayingApps, UiLanguage, HighlightMutedNodes, MinimapMode,
StartRoutingOnLaunch, SignalGlow, SnapToGrid, HighlightCableOnHover,
CableAnimation, ShowDiagnostics and TranscriberModel. Start with Windows is
read/written from HKCU Run. WebView2 localStorage holds the sidebar width/state
and the diagnostics node position/scale. The welcome guide auto-shows only when
there is no saved session; it can always be reopened from Settings > Behavior.

Transcriber models: 10 OpenAI Whisper models (Tiny/Base/Small/Medium English and
Multilingual, plus Large v3/Turbo). Models are not auto-downloaded by nodes;
Settings downloads them. Installed `.bin` files live under
`%APPDATA%\Ruse\models`, are validated by size, and are moved into place after a
cancellable download. OpenAI Whisper code and weights are MIT licensed; see the
Info tab and `THIRD-PARTY-NOTICES.md`. Node model choice persists in presets.

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
A transcriber node adds one hidden monitor render thread and one Whisper worker;
the tap itself is a two-biquad filter plus 3:1 decimation on the monitor thread.
Cable edges subscribe only to each source's active/inactive flag instead of
10 Hz level changes, and live telemetry pauses while dragging a node or pulling
a cable. Packet animation is also suspended during those interactions. The
connection preview path is driven directly from pointer events with rAF instead
of waiting for a React Flow store render, so it stays attached to the pointer
when the canvas is busy.

## Known issues and next work

- Duplicate detection uses the app's current render session. An app that is
  silent, not in the session list, or spread across several devices may not be
  flagged until the next catalog refresh (750 ms while following apps, 4 s
  otherwise). Different-device copies are informational so intentional
  mirroring is not blocked; the banner can be dismissed.
- Ruse deliberately does not mute the original app/device, because the capture
  can follow that mute. The reliable fixes are a different output or a virtual
  cable; the help dialog says so.
- Existing users with a saved session do not get the welcome wizard
  automatically. Use Settings > Behavior > Show welcome guide.
- UI translation is intentionally partial: native status/history text, device
  and model names, and error messages from the engine remain English. Missing
  locale keys fall back to the English source string.
- `FollowPlayingApps` reattaches an app source that is still on the canvas. If
  the source node was removed, the app must be added again; a saved per-app
  route template is future work. With several active instances of one process
  name, the first active PID is used.
- Model validation checks the downloaded file size, not a cryptographic
  checksum. A corrupted but non-empty model is caught by the child probe, but
  re-downloading is the only repair.
- Whisper models are downloaded as `.bin` files from the whisper.cpp model
  repository. Model loading is preflighted in the child process; a corrupt file
  is reported in the node without killing Ruse.
- After the probe passes, recognition still runs in-process. A native crash
  during recognition would still end the process; a fully out-of-process worker
  is the next hardening step.
- Windows also logged kernel memory-management bugchecks (0x1A/0x4E/0xEF) near
  the same period. If they recur, check RAM/XMP stability.
- Deleting a model while its worker is still loading can leave files locked;
  the delete action reports the error instead of crashing.
- Transcriber/waveform processors are node-level taps. The chain editor hides
  them and old saved chains that contain one drop it (a tap cannot be inside a
  saved chain).
- Whisper accuracy varies by model and microphone; Tiny is fast but weak, while
  Small/Medium are the practical quality options. Real microphones are not yet
  covered by the automated checks.
- Punctuation, capitalization and transcript export beyond plain text are still
  open.
- The diagnostics node is UI-only: it is not part of engine graph snapshots or
  presets. Its position/scale live in WebView2 localStorage. CPU is the whole
  process; GPU is system-wide from the GPU Engine counters and can be absent on
  machines without those counters.
- WPF render tier is shown under Settings > Info; it reported tier 2 in
  the latest session, so the earlier tier-0 note may be stale.
- Commit the uncommitted transcriber/settings/UI overhaul before starting risky
  work.

## Conventions

- Comments explain why, not what. Keep them short, preferably one line. No emoji.
- New UI text goes through `useI18n().t('English source text')`; add the
  Portuguese entry in `WebUi/src/locales/pt-BR.ts`. Missing keys fall back to
  English. Native status/device names are not translated yet.
- Keep `bin/`, `obj/`, `node_modules/`, `WebUi/dist/`, models, and screenshots
  out of the repo.
- Do not commit unless the user asks.
- The user reviews screenshots; keep `--demo` tidy.
