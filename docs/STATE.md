# Project state — handoff notes

Keep this file short. It replaces the earlier handoff summaries and is the fast
way for a new session to pick up where the last one stopped.

## What Ruse is

A node-based audio router for Windows (C# / .NET 10 / WPF / NAudio 3.3).
Sources (apps, devices, system audio, ASIO inputs) are dragged onto a canvas,
wired through effect modifiers, into outputs (WASAPI endpoints or ASIO drivers).

## Repos and restore points

- Canonical: `C:\Users\ih_me\Projects\Ruse` (branch `main`)
- Worktree: `C:\Users\ih_me\Projects\wt\Ruse-work` (branch `work`)
- Work happens in the worktree, then `git merge work` into `main`.
- Restore points: tag `pre-night-2026-10-22`, branch `backup/pre-night`,
  folder copy `C:\Users\ih_me\Projects\Ruse-backup-2026-10-22` (+ `.bundle`).

## How to build and test

```
dotnet build src\Ruse.App\Ruse.App.csproj -v q --nologo
powershell -ExecutionPolicy Bypass -File C:\Users\ih_me\AppData\Local\Temp\opencode\regress-ruse.ps1 -Repo <worktree>
```

`-ExecutionPolicy Bypass` is required on this machine (scripts are disabled).

The regression script runs every self-test and writes `<mode>.txt` next to the
exe. Modes: `--converter-test --selftest --modifier-test --preset-test
--filter-test --effects-test --align-test --chain-test --fanout-test
--asio-test --record-test --feedback-bench --uitest --route-test --device-test
--graph-test --perf-test --latency-test --delay-test --sessions --demo --live-demo`.

Screenshot probe: `C:\Users\ih_me\AppData\Local\Temp\opencode\window-shot.ps1`
(`-Name <file> -ZoomSteps n -PanX px -PanY px`). It maximises the window and
captures it with PrintWindow. Screenshots land outside the repo, in
`C:\Users\ih_me\OneDrive\Documentos\voicemet-screenshots\` (legacy folder name).
Do not commit screenshots into `docs/`.

## Architecture, in one paragraph

The engine always mixes at **48 kHz stereo float**. Each source has a capture
thread (`ProcessLoopbackCapture`, `DeviceCapture`, `AsioCapture`). Each output
has its own render thread (`OutputNode.PumpLoop`) that pulls from its routes,
runs the route's effect chain, and pushes into a WASAPI (`WasapiPlayer`) or ASIO
endpoint. A source can feed several outputs: every route takes its **own
`SourceNode.Subscription`** (a private ring buffer fed by the capture thread), so
routes never steal audio from each other. The UI is a separate normal WPF
dispatcher thread.

Threading: 1 UI thread + 1 capture thread per source + 1 render thread per
output. The UI never touches the audio path.

## Files worth knowing

```
src/Ruse.App/
  Audio/SourceNode.cs        capture + Subscription fan-out + stale-audio skip
  Audio/OutputNode.cs        render pump, Route (per-route subscription), mixing
  Audio/AudioFileRecorder.cs WAV + Media Foundation MP3/FLAC file writer
  Audio/AudioGraph.cs        graph + route derivation (SyncOutputRoutes)
  Audio/BiquadProcessor.cs   high/low pass, peak, shelves (EQ building block)
  Audio/NaudioEffects.cs     registry of the 22 NAudio effects
  Audio/HighPassProcessor.cs thin wrapper over BiquadProcessor (preset id)
  Audio/Subgraph.cs          saved chains (ChainProcessor, SubgraphLibrary)
  Audio/PresetModel.cs       preset JSON + processor serialisation
  StartupRegistration.cs     per-user Windows startup entry (HKCU Run key)
  Ui/ParameterEditor.cs      one widget builder for sliders/toggles/drop-downs
  MainWindow.*.cs            18 partials, one per area (see the file names)
  MainWindow.SidebarList.cs  one shared double-click handler for every list
  Diagnostics/AppLog.cs      %APPDATA%\Ruse\ruse.log, 512 KB rotation
```

## What works (verified by tests)

- Capture (process loopback, device, system loopback, ASIO), 48 kHz float mixing.
- 28 effects (22 NAudio + gain, high-pass and 4 biquad shapes), every parameter
  kind (slider, toggle, drop-down, integer snapping), presets and chains
  round-trip.
- One source → many outputs (fan-out, bit-identical on both outputs).
- Latency: ~15-20 ms end to end (12 ms render buffer + device period + capture
  backlog 5-20 ms). `--delay-test` measures ~0 ms between the process tap and
  the speakers, so the engine adds nothing measurable.
- File recording to WAV 16/24-bit, WAV 32-bit float, MP3 192 kbps or FLAC.
  `--record-test` writes and reads back all five formats, the node pump, and a
  live route; `--uitest` covers the format picker and path controls. Restored
  recorders start disarmed (`AutoStart=false`) so loading a preset cannot
  overwrite a take.
- Recording feedback: a recorder header shows NO FILE / ARMED / REC / SAVED /
  ERR with a blinking dot, the title bar shows a global REC timer and count,
  and each output gets a CLIP badge plus a red recording border. `--uitest`
  checks the live and saved badge states. `--feedback-bench` measures the hit:
  on 19 nodes / 3 recorders, the 10 Hz UI path costs about 0.012 ms per tick
  (0.012 % of one core), up from 0.010 ms before the feedback (+0.17 KiB/tick);
  render time stays inside run-to-run noise.
- Start with Windows: Settings has a checkbox that adds or removes the current
  `Ruse.exe` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. It
  reads the registry when the dialog opens, so the checkbox reflects the real
  startup state rather than a cached preference.
- Routing launch and transport state: a restored session loads idle by default;
  Settings can start it automatically after the window is ready. Start uses a
  grey idle button and switches to the blue accent as soon as routing runs.
  Verified with a test session: enabled -> auto-started at load and the recorder
  began writing; disabled -> restored idle with a grey Start button.
- Undo/redo, zoom (wheel, 25-250 %), middle-drag pan, mini-map, shortcuts,
  context menu, chains drag-and-drop, ASIO in/out, click sound. Grabbing or
  creating a node raises it above overlapping nodes, and releasing a dragged
  node on top of another moves it to the nearest clear slot (`--uitest` checks
  both). ASIO output is
  callback-verified end to end with FlexASIO 1.10b and Voicemeeter Virtual ASIO:
  `--asio-test` reports both streaming and consuming the render buffer.
- Sidebar wheel: when the pointer is over a list item the wheel still scrolls
  the whole sidebar (covered by `--uitest`).
- Canvas zoom/pan: the world is scaled with a **RenderTransform**, not a
  LayoutTransform, so meter updates inside the layer no longer force a full
  measure/arrange/render pass over the whole graph.
- Custom window frame: WindowChrome with a VFD-style title bar, square window
  keys, drag-to-move and Aero snap; maximising stays above the taskbar
  (`--uitest` checks the keys and the restore glyph).
- CPU while routing three sources through a compressor: ~12 % of one core
  (**0.5 % of the whole 12-core / 24-thread Ryzen 9 5900X**). Idle ~6 % of one
  core, of which the periodic device/session refresh is most.

## Things that went wrong, and the lessons

- **Fan-out was broken** (found while answering "is the app multithreaded?").
  Two outputs sharing one source competed for one ring buffer, so each heard
  only part of the signal (23 % imbalance). Fixed with per-route
  `SourceNode.Subscription`; `--fanout-test` proves 0.0 % difference.
- **The stale-audio skip was too aggressive** (40 ms). It is now 150 ms
  trigger / keep 50 ms, per subscription.
- **A subscription created lazily loses the audio written before the first
  read.** Routes take their subscription eagerly in `RefreshRoutes()`.
- **A WPF ListBox swallows the wheel even when it cannot scroll.** Setting
  `e.Handled = false` in a `PreviewMouseWheel` handler does nothing; scroll the
  outer `ScrollViewer` yourself and mark the event handled.
- **A LayoutTransform on the graph layer makes every child change re-layout the
  whole world.** A RenderTransform scales at draw time and leaves layout alone;
  that is the right tool for a zoomable canvas.
- **NAudio's `AsioDevice.InitPlayback` only configures the driver; it does not
  start it.** `AsioOutputEndpoint` also has to call `AsioDevice.Start()`, or the
  output stays `Configured` and never streams.
- **`AsioDevice.State` turns `Running` as soon as `ASIOStart` succeeds**, before
  any buffer arrives. Wait for a real callback (Ruse counts them in
  `AsioFloatProvider`) before calling the driver streaming; otherwise a test
  can pass and stop before a single sample flows.
- **`WindowStyle=None` maximises to the whole monitor, taskbar included.**
  WindowChrome clips the window but does not fix the size; handle
  `WM_GETMINMAXINFO` with the monitor's work area or the bottom of the window
  hides behind the taskbar.
- **WindowChrome treats the caption as non-client.** Interactive children need
  `WindowChrome.IsHitTestVisibleInChrome="True"` or they never receive clicks.
- **`Direct3DCreate9On12` "crashing" was our own probe's wrong P/Invoke
  signature**, not a system fault. Do not repeat that claim.
- **Measuring the delay needs onset matching, not envelope correlation**, when
  the test signal is sparse (many lags score alike).
- **Chromium's process loopback pid matters**: a renderer pid captures silence;
  the audio-session owner (utility process) is the right one. `--sessions`
  shows the mapping.
- **`dotnet run` file-based apps have built-in COM disabled**, so COM interop
  probes must live in a normal project.

## Open issues

1. **WPF renders in software (tier 0) on this machine.** Ruled out: DDU driver
   reinstall, SFC/DISM repair, HAGS off, the pre-October `d3d9.dll`, .NET
   10.0.11/12/13, registry overrides, driver date. D3D9 (hardware VP), D3D11
   (FL 12_1) and DXGI all work. Remaining suspects: the NVIDIA driver build or
   the October cumulative update. The banner was removed at the user's request;
   the tier is still logged (INFO) and shown in Settings.
2. **VST3 needs a native host** (NAudio 3.3 has no VST at all): C++ component +
   Steinberg SDK + IPC. Deliberately deferred, see `docs/BACKLOG.md`.
3. The chain builder and the modifier node share `ParameterEditor`, but the
   chain dialog has had no eye test with a drop-down effect yet.

## Conventions

- Comments explain *why*, not *what*. No emoji in code.
- Effect parameters are exposed through `AudioParameter` (`IsToggle`,
  `IsChoice`, `IsInteger`, `Logarithmic`); the UI builds them via
  `ParameterEditor.Create`.
- Effect type ids are stable strings (`gain`, `highpass`, `compressor`, …);
  presets depend on them. The high-pass parameter is named `Cutoff` on purpose.
- `main` and `work` are kept in sync; commit on `work`, merge into `main`.
- Keep `bin/`, `obj/` and screenshots out of the working tree before searching
  or handing off: they are build junk and make a session's context enormous.
- The user reviews screenshots, so keep the demo graph tidy (`--demo`).

## Last session (2026-10-23)

- Added live feedback for long-running state: recorder header badges
  (NO FILE / ARMED / REC / SAVED / ERR), a global title-bar REC timer with the
  active recording count, a per-output CLIP badge, recording/error border
  states, and start/stop status messages that name the recordings.
- Added `--feedback-bench` and recorded the before/after cost (see above).
  The extra UI work is measurable but negligible at the 10 Hz refresh rate.
- `--uitest` now drives a real recorder through ARMED -> REC -> SAVED and
  checks the badge, border and global chip.
- Added the Start with Windows checkbox to Settings. Verified end to end by
  checking it, seeing the `Ruse` value appear in the HKCU Run key with the
  current exe path, unchecking it, and confirming the value was removed.
- Added the Start routing on launch checkbox and made the transport button
  reflect run state. Sessions now restore stopped unless the option is enabled;
  the automatic start waits for the dispatcher idle pass after the window loads.
  Screenshots: `startup-routing-button.png` (active) and
  `idle-start-button.png` (idle).
- Grabbed nodes now move above every other node, and new nodes are created on
  top as well. When a dragged node is released over another, it is nudged to
  the nearest free grid slot instead of covering it. Added z-order and overlap
  checks to `--uitest`.

## Previous session (2026-10-22, later)

- Removed the old `Voicemet` name from test labels and the stale "cable
  waveform" comments.
- Deleted an unreachable duplicate `--chain-test` branch in `App.xaml.cs`.
- Fixed sidebar wheel scrolling over list items and added a self-test for it.
- Merged the duplicate canvas drag handlers and the six near-identical
  list double-click handlers into one function each.
- Replaced the canvas `LayoutTransform` with a `RenderTransform`, removing the
  full-world re-layout on every meter tick; verified at 100 % and 132 % zoom.
- Replaced the native title bar with a Posy-inspired hi-fi frame (VFD plate,
  level bars, square keys) on `WindowChrome`, plus a work-area fix so a
  maximised window no longer hides its status bar behind the taskbar.
- Removed the outdated screenshots from `docs/` and corrected doc counts
  (22 NAudio effects, 28 total).
- Installed FlexASIO 1.10b (x64) for standalone ASIO verification and found a
  real bug: `AsioOutputEndpoint` configured the driver but never called
  `AsioDevice.Start()`.
- Fixed the missing start, then changed the streaming check to wait for a real
  buffer callback instead of NAudio's immediate `Running` state. `--asio-test`
  now reports callbacks; FlexASIO's log showed 9 buffer switches at 48 kHz with
  advancing sample positions, and both test drivers consumed the render buffer.
- Added the file-recorder output with a format picker and save-path dialog,
  WAV + Media Foundation MP3/FLAC writers, preset/undo persistence, and
  `--record-test` coverage of every format and the live route.
