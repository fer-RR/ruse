# Ruse

A node-based audio router for Windows. Route any application, system sound or
capture device to any output device, with an effect chain in between.

## What it does

- **Offline transcription** — a pass-through node streams the route to OpenAI Whisper (Whisper.net / whisper.cpp).
  The small English model downloads on first use and then runs without a network.
- **Offline translation** — a pass-through node translates speech into English with Whisper, or into Portuguese, Spanish, French, German, Italian, Japanese, Korean or Simplified Chinese with a local Qwen2.5 GGUF model. Audio and text stay on the PC.
- **Per-application capture** — pick a process (Edge, Discord, a game…) in the
  sidebar and drag it onto the canvas. Ruse captures it through the Windows
  process-loopback API, so nothing has to be reconfigured in the app.
- **File player** — add an audio file as a source node, with optional looping,
  and route it through the same effects and outputs.
- **Any output device** — WASAPI endpoints (speakers, headphones, Voicemeeter
  inputs) and **ASIO** drivers, including multichannel interfaces.
- **Effect chains** — 29 built-in audio effects plus waveform, dB meter,
  offline transcription and translation taps: gain, EQ shapes, a 10-band graphic EQ, compressor,
  limiter, gate, reverb, delay, chorus, flanger, phaser, tremolo, pitch shift,
  bitcrusher, noise suppression, AGC, comfort noise… with live parameters.
- **Saved chains** — build a chain of effects once, save it to the library and
  drop it anywhere as a single node.
- **File recording** — add a recorder output, choose WAV (16/24-bit or 32-bit
  float), MP3 or FLAC, and save the routed mix to disk.
- **VST3 plugins** — scan the standard VST3 folders and drop any effect into the
  graph. Parameters appear as normal node controls and the plugin's native
  editor opens in its own window. VST3 only; VST2 is not supported.
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
- **Minimize to tray** — minimizing hides Ruse in the notification area while
  audio keeps routing; click the tray icon to restore it or use its menu to exit.
- **Start routing on launch** — optionally start the restored graph after the
  window is ready. The compact Routing switch shows ON/OFF and glows green
  while running; turning it off stops the graph.
- **Undo/redo** for everything, including node moves and effect tweaks.
- **Low latency** — the whole path is about 20 ms on a modern machine.
- **Diagnostics** — an optional live diagnostics canvas node shows audio
  buffers/delays, CPU/GPU use, memory, FPS and JS heap. It is draggable,
  scalable and closable. A built-in self-test suite (`--selftest` and friends)
  exercises the engine without hardware.
- **Languages** — English, Portuguese (Brazil), Spanish, French, German,
  Italian, Japanese, Korean and Simplified Chinese; switchable live.

## Building

```bat
dotnet build src\Ruse.App\Ruse.App.csproj
```

Needs the .NET 10 SDK and Node.js 20+. The build installs the Web UI packages on
first use and compiles the React assets into the app output. The app itself is
`src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`.

Speech and translation models are downloaded from Settings > Speech &
translation. Whisper models run speech recognition; non-English translation also
needs one Qwen2.5 GGUF model (0.5B, 1.5B or 3B). Inference runs on the CPU in
the shipped build; CUDA/Vulkan GPU backends are possible but not included.

VST3 effects are discovered from the standard folders (`Program Files\Common
Files\VST3`, `Program Files (x86)\Common Files\VST3` and
`%LOCALAPPDATA%\Programs\Common\VST3`). Use **Refresh** in the toolbar (or the
rescan button in the library) after installing a plugin.

## Running

| Flag | What it does |
|---|---|
| *(none)* | The normal WebView2 workspace |
| `--web-test` | Tests the web bridge, undo/redo and chain persistence |
| `--demo` | A pre-built graph, handy for screenshots |
| `--live-demo --pid <pid>` | A graph wired to a running process |
| `--selftest` | The full self-test suite (writes `latency-selftest.txt`) |
| `--route-test --pid <pid>` | Captures a real process and reports the signal |
| `--delay-test --pid <pid>` | Measures the delay from the app's tap to the speakers |
| `--latency-test --pid <pid>` | Reports the capture backlog and drift over time |
| `--device-test --pid <pid>` | End-to-end run against a real output device |
| `--effects-test` | Checks every effect: parameters, output, preset round-trip |
| `--record-test` | Writes/reads every recording format and a live route |
| `--translation-text-test <model> <target> <text> <out>` | Checks one local translation model without a route |
| `--translation-e2e-test <speech> <translation> <target> <wav> <out>` | Runs a WAV through the speech-and-translation pipeline |
| `--vst3-test <plugin> <out>` | Loads a VST3, processes a tone and saves state |
| `--vst3-route-test <plugin> <out>` | Runs a VST3 through the real route renderer |
| `--vst3-editor-test <plugin> <out>` | Opens and closes a VST3 editor |
| `--vst3-scan-test` | Lists the VST3 effects found in the standard folders |
| `--sessions` | Lists the audio sessions and the process that owns each |

## Packaging

For a machine without .NET, publish self-contained:

```bat
dotnet publish src\Ruse.App\Ruse.App.csproj -c Release -r win-x64 --self-contained true -o ..\Ruse-publish
```

The publish target copies `WebUi/dist` beside the exe. Models, VST3 plugins and
ASIO drivers are not bundled; the target machine still needs the WebView2
Runtime. See `README-first-run.txt` in a packaged build.

## How it is put together

```
src/Ruse.Engine/
  Audio/          UI-independent engine
    AudioGraph.cs         owns sources, modifiers and outputs
    SourceNode.cs         a capture source (app, device, system, ASIO)
    OutputNode.cs         a render endpoint (WASAPI, ASIO or file)
    ModifierNode.cs       an effect chain on a route
    Vst3Processor.cs      VST3 plugin effect adapter
    GraphicEqualizerProcessor.cs  NAudio 10-band ISO equalizer wrapper
    (the effect processors and the preset model also live in Audio/)
  Diagnostics/    the log
src/Ruse.App/
  WebMainWindow     native WebView2 host
  WebShellController  serialized command/state bridge
  WebShellController.Diagnostics.cs  CPU/GPU/audio diagnostics snapshot
  GpuUsage.cs       Windows GPU Engine usage sampler
  TranscriptionService.cs  Whisper recognition workers
  TranslationService.cs    speech translation workers (Whisper + local GGUF)
  TranslationModels.cs     translation model catalog and downloads
  TranslationModelProbe.cs child-process GGUF load check
  StartupErrorDialog.xaml  native fallback when WebView2 cannot start
  WebUi/            React + TypeScript workspace
```

The engine always mixes at **48 kHz, stereo, 32-bit float**. Sources are
converted into that format, effects run in it, and outputs convert out of it —
WASAPI lets Windows do the conversion, ASIO drivers are resampled by Ruse when
the driver runs at another rate.

## Licences

NAudio is MIT licensed and ships the effect suite (including the 10-band
GraphicEqualizer) and the WASAPI/ASIO layers. Whisper.net, LLamaSharp and
OwnAudioVst are MIT; the VST 3 SDK is MIT. Translation models are Qwen2.5 GGUF
files under Apache-2.0. Ruse itself is a separate work. See
`THIRD-PARTY-NOTICES.md`.
