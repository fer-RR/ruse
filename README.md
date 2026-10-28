# Ruse

A node-based audio router for Windows. Route any application, system sound or
capture device to any output device, with an effect chain in between.

## What it does

- **Offline transcription** — a pass-through node streams the route to OpenAI Whisper (Whisper.net / whisper.cpp).
  The small English model downloads on first use and then runs without a network.
- **Per-application capture** — pick a process (Edge, Discord, a game…) in the
  sidebar and drag it onto the canvas. Ruse captures it through the Windows
  process-loopback API, so nothing has to be reconfigured in the app.
- **Any output device** — WASAPI endpoints (speakers, headphones, Voicemeeter
  inputs) and **ASIO** drivers, including multichannel interfaces.
- **Effect chains** — 29 built-in audio effects plus waveform, dB meter and
  offline transcription taps: gain, EQ shapes, a 10-band graphic EQ, compressor,
  limiter, gate, reverb, delay, chorus, flanger, phaser, tremolo, pitch shift,
  bitcrusher, noise suppression, AGC, comfort noise… with live parameters.
- **Saved chains** — build a chain of effects once, save it to the library and
  drop it anywhere as a single node.
- **File recording** — add a recorder output, choose WAV (16/24-bit or 32-bit
  float), MP3 or FLAC, and save the routed mix to disk.
- **Live signal feedback** — glowing nodes and matching library items, small
  marching packets along active cables, a waveform tap and a dedicated compact
  dB meter node (vertical by default, horizontal from its menu). The glow can be
  disabled in Settings.
- **Live recording feedback** — each recorder shows NO FILE / ARMED / REC /
  SAVED / ERR, a global REC timer appears in the title bar, and an output that
  clips gets its own CLIP badge.
- **Presets** — the whole graph (nodes, cables, effect settings) saves to a
  `.ruse.json` file.
- **Start with Windows** — an option in Settings adds or removes Ruse from the
  per-user startup apps.
- **Start routing on launch** — optionally start the restored graph after the
  window is ready. The compact Routing switch shows ON/OFF and glows green
  while running; turning it off stops the graph.
- **Undo/redo** for everything, including node moves and effect tweaks.
- **Low latency** — the whole path is about 20 ms on a modern machine.
- **Diagnostics** — an optional live diagnostics canvas node shows audio
  buffers/delays, CPU/GPU use, memory, FPS and JS heap. It is draggable,
  scalable and closable. A built-in self-test suite (`--selftest` and friends)
  exercises the engine without hardware.

## Building

```bat
dotnet build src\Ruse.App\Ruse.App.csproj
```

Needs the .NET 10 SDK and Node.js 20+. The build installs the Web UI packages on
first use and compiles the React assets into the app output. The app itself is
`src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`.

## Running

| Flag | What it does |
|---|---|
| *(none)* | The normal WebView2 workspace |
| `--legacy-ui` | The previous WPF workspace |
| `--web-test` | Tests the web bridge, undo/redo and chain persistence |
| `--demo` | A pre-built graph, handy for screenshots |
| `--live-demo --pid <pid>` | A graph wired to a running process |
| `--selftest` | The full self-test suite (writes `selftest.txt`) |
| `--route-test --pid <pid>` | Captures a real process and reports the signal |
| `--delay-test --pid <pid>` | Measures the delay from the app's tap to the speakers |
| `--latency-test --pid <pid>` | Reports the capture backlog and drift over time |
| `--device-test --pid <pid>` | End-to-end run against a real output device |
| `--perf-test --pid <pid>` | CPU and allocation profile of the whole app |
| `--effects-test` | Checks every effect: parameters, output, preset round-trip |
| `--record-test` | Writes/reads every recording format and a live route |
| `--feedback-bench` | Measures the per-tick cost of the live UI feedback |
| `--sessions` | Lists the audio sessions and the process that owns each |

## How it is put together

```
src/Ruse.Engine/
  Audio/          UI-independent engine
    AudioGraph.cs         owns sources, modifiers and outputs
    SourceNode.cs         a capture source (app, device, system, ASIO)
    OutputNode.cs         a render endpoint (WASAPI, ASIO or file)
    ModifierNode.cs       an effect chain on a route
    GraphicEqualizerProcessor.cs  NAudio 10-band ISO equalizer wrapper
    (the effect processors and the preset model also live in Audio/)
  Diagnostics/    the log
src/Ruse.App/
  WebMainWindow     native WebView2 host
  WebShellController  serialized command/state bridge
  WebShellController.Diagnostics.cs  CPU/GPU/audio diagnostics snapshot
  GpuUsage.cs       Windows GPU Engine usage sampler
  WebUi/            React + TypeScript workspace
  MainWindow*.cs    legacy WPF workspace (--legacy-ui and UI tests)
  Ui/               legacy dialogs
```

The engine always mixes at **48 kHz, stereo, 32-bit float**. Sources are
converted into that format, effects run in it, and outputs convert out of it —
WASAPI lets Windows do the conversion, ASIO drivers are resampled by Ruse when
the driver runs at another rate.

## Licences

NAudio is MIT licensed and ships the effect suite (including the 10-band
GraphicEqualizer) and the WASAPI/ASIO layers. Ruse itself is a separate work.
