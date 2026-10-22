# Project state — handoff notes

Keep this file short. It is the fast way for a new session to pick up where the
last one stopped.

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
powershell -File C:\Users\ih_me\AppData\Local\Temp\opencode\regress-ruse.ps1 -Repo <worktree>
```

The regression script runs every self-test and writes `<mode>.txt` next to the
exe. Modes: `--converter-test --selftest --modifier-test --preset-test
--filter-test --effects-test --align-test --chain-test --fanout-test
--asio-test --uitest --route-test --device-test --graph-test --perf-test
--latency-test --delay-test --sessions --demo --live-demo`.

Screenshot probe: `C:\Users\ih_me\AppData\Local\Temp\opencode\window-shot.ps1`
(`-Name <file> -ZoomSteps n -PanX px -PanY px`). It maximises the window and
captures it with PrintWindow, so it works even when another window is on top.
Screenshots land in `C:\Users\ih_me\OneDrive\Documentos\voicemet-screenshots\`.

## Architecture, in one paragraph

The engine always mixes at **48 kHz stereo float**. Each source has a capture
thread (`ProcessLoopbackCapture`, `DeviceCapture`, `WasapiRecorder`,
`AsioCapture`). Each output has its own render thread (`OutputNode.PumpLoop`)
that pulls from its routes, runs the route's effect chain, and pushes into a
WASAPI (`WasapiPlayer`) or ASIO endpoint. A source can feed several outputs:
every route takes its **own `SourceNode.Subscription`** (a private ring buffer
fed by the capture thread), so routes never steal audio from each other. The UI
is a separate normal WPF dispatcher thread.

Threading: 1 UI thread + 1 capture thread per source + 1 render thread per
output. The UI never touches the audio path.

## Files worth knowing

```
src/Ruse.App/
  Audio/SourceNode.cs        capture + Subscription fan-out + stale-audio skip
  Audio/OutputNode.cs        render pump, Route (per-route subscription), mixing
  Audio/AudioGraph.cs        graph + route derivation (SyncOutputRoutes)
  Audio/BiquadProcessor.cs   high/low pass, peak, shelves (EQ building block)
  Audio/NaudioEffects.cs     registry of the 26 NAudio effects
  Audio/Subgraph.cs          saved chains (ChainProcessor, SubgraphLibrary)
  Audio/PresetModel.cs       preset JSON + processor serialisation
  Ui/ParameterEditor.cs      one widget builder for sliders/toggles/drop-downs
  MainWindow.*.cs            19 partials, one per area (see the file names)
  Diagnostics/AppLog.cs      %APPDATA%\Ruse\ruse.log, 512 KB rotation
```

## What works (verified by tests)

- Capture (process loopback, device, system loopback, ASIO), 48 kHz float mixing.
- 28 effects, all parameter kinds (slider, toggle, drop-down, integer snapping),
  presets round-trip, chains round-trip.
- One source → many outputs (fan-out, bit-identical on both outputs).
- Latency: ~15-20 ms end to end (12 ms render buffer + device period + capture
  backlog 5-20 ms). `--delay-test` measures ~0 ms between the process tap and
  the speakers, so the engine adds nothing measurable.
- Undo/redo, zoom (wheel, 25-250 %), middle-drag pan, mini-map, shortcuts,
  context menu, chains drag-and-drop, ASIO in/out, click sound.
- CPU while routing three sources through a compressor: ~12 % of one core
  (**0.5 % of the whole 12-core / 24-thread Ryzen 9 5900X**). Idle ~6 % of one
  core, of which the periodic device/session refresh is most.

## Things that went wrong, and the lessons

- **Fan-out was broken** (found while answering "is the app multithreaded?").
  Two outputs sharing one source competed for one ring buffer, so each heard
  only part of the signal (23 % imbalance). Fixed with per-route
  `SourceNode.Subscription`; `--fanout-test` now proves 0.0 % difference.
- **The stale-audio skip was too aggressive** (40 ms). It is now 150 ms
  trigger / keep 50 ms, per subscription.
- **A subscription created lazily loses the audio written before the first
  read.** Routes now take their subscription eagerly in `RefreshRoutes()`.
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
   (FL 12_1) and DXGI all work. Remaining suspects: the NVIDIA driver build
   (32.0.16.2101, 3 days old) or the October cumulative update. The banner was
   removed at the user's request; the tier is still logged (INFO) and shown in
   Settings.
2. **The canvas `LayoutTransform` re-renders the whole world layer** whenever
   anything inside changes (this is why meters cost ~10 % of a core). Fixing it
   means scaling node positions/sizes/cables by hand. Not started; see
   `docs/BACKLOG.md`.
3. **VST3 needs a native host** (NAudio 3.3 has no VST at all): C++ component +
   Steinberg SDK + IPC. Deliberately deferred, see `docs/BACKLOG.md`.
4. The chain builder and the modifier node now share `ParameterEditor`, but the
   chain dialog has had no eye test with a drop-down effect yet.

## Conventions

- Comments explain *why*, not *what*. No emoji in code.
- Effect parameters are exposed through `AudioParameter` (`IsToggle`,
  `IsChoice`, `IsInteger`, `Logarithmic`); the UI builds them via
  `ParameterEditor.Create`.
- Effect type ids are stable strings (`gain`, `highpass`, `compressor`, …);
  presets depend on them. The high-pass parameter is named `Cutoff` on purpose.
- `main` and `work` are kept in sync; commit on `work`, merge into `main`.
- The user reviews screenshots, so keep the demo graph tidy (`--demo`).
