# Ruse

A node-based audio router for Windows. Route any application, system sound or
capture device to any output device, with an effect chain in between.

## What it does

- **Offline transcription** â€” a pass-through node streams the route to OpenAI Whisper (Whisper.net / whisper.cpp).
  It shows sentence cues with timestamps and exports SRT, VTT or plain text.
  The small English model downloads on first use and then runs without a network.
- **Offline translation** â€” a pass-through node translates speech into English with Whisper, or into Portuguese, Spanish, French, German, Italian, Japanese, Korean or Simplified Chinese with a local Qwen2.5 GGUF model. Optional OPUS-MT ONNX models can replace Qwen for supported pairs. It also exports timed SRT/VTT cues. Audio and text stay on the PC.
- **Per-application capture** â€” pick a process (Edge, Discord, a gameâ€¦) in the
  sidebar and drag it onto the canvas. Ruse captures it through the Windows
  process-loopback API on Windows 11 or Windows Server 2022 (build 20348+), so
  nothing has to be reconfigured. On Windows 10 the Applications list is hidden
  with a banner; use **System audio** or route the app to a virtual cable.
- **File player** â€” add an audio file as a source node, with position/seek,
  play/pause, optional looping and a direct per-node output selector,
  and route it through the same effects and outputs.
- **Clean conversion** â€” band-limited windowed-sinc resampling for non-48 kHz
  sources, proper 5.1/7.1-to-stereo downmixing, and smoothed gain, pan, filter
  and volume changes to avoid zipper noise.
- **Any output device** â€” WASAPI endpoints (speakers, headphones, Voicemeeter
  inputs) and **ASIO** drivers, including multichannel interfaces.
- **Effect chains** â€” 48 built-in audio effects and analysis taps, including dB meter, spectrum, spectrogram,
  sidechain ducker, loudness/true-peak meter, parametric EQ, convolution reverb, polarity, vibrato, ring mod, Mid/Side, exciter,
  offline transcription and translation taps: gain, EQ shapes, a 10-band graphic EQ with a draggable response curve, compressor,
  multiband compressor, limiter, gate, reverb, delay, chorus, flanger, phaser,
  tremolo, pitch shift (standard and high quality),
  bitcrusher, noise suppression, AGC, comfort noiseâ€¦ with live parameters.
- **Saved chains** â€” build a chain of effects once, save it to the library and
  drop it anywhere as a single node.
- **Frame templates** â€” draw a group around nodes, save it as a template and
  insert the whole wired group later without clearing the canvas. Use the frame
  header's template button; templates appear in the library under Processing.
- **Mixer dock** — a resizable right-side mixer with source and output strips,
  insert chips, per-send gain and Pre/Post routing, a master strip, dB meters,
  mute/solo, colors and rename. Strips can be horizontal or vertical (`Ctrl+M`;
  library `Ctrl+L`).
- **Sidechain routing** — wire a source or effect output to the amber KEY input
  on a sidechain ducker. The key controls gain reduction and never enters the
  audible mix.
- **MIDI learn** â€” enable a MIDI input in Settings, click the MIDI badge on an
  effect parameter, then move a CC or press a note. CCs map the full range;
  notes act as a held trigger. Bindings save with the session.
- **Clip guard** â€” protect every output from hard clipping with a fast limiter
  and soft knee. It is on by default; loud transients show a GUARD badge and
  Settings > Behavior can turn it off.
- **Audio engine settings** — Settings > Audio uses automatic timing by default,
  with manual block size (2.5/5/7.5/10 ms), device buffer (Low/Balanced/Safe/
  Manual) and engine cushion (Auto 2 blocks/Manual) when needed.
- **Automatic device refresh** — new WASAPI playback/capture devices appear in
  the library within about two seconds; ASIO changes still need a manual refresh.
- **File recording** â€” add a recorder output, choose WAV (16/24-bit or 32-bit
  float), MP3 or 24-bit FLAC, and save the routed mix to disk. File I/O and
  encoding run on a background writer so the audio thread never blocks on disk;
  16-bit WAV uses TPDF dither, and transport seeks, loops and stale-source skips
  crossfade to avoid clicks.
- **VST3 plugins** â€” scan the standard VST3 folders and drop any effect into the
  graph. Parameters appear as normal node controls and the plugin's native
  editor opens in its own window. Plugins run in a separate host process by
  default, so a crashing plugin cannot close Ruse; the host pipes are restricted
  to the current user with a handshake, and a watchdog also restarts a hung host
  and reapplies its parameters. Turn this off in Settings > Plugins if you need
  the old in-process behavior. VST3 only; VST2 is not supported.
- **Live signal feedback** â€” glowing nodes and matching library items, small
  marching packets along active cables, spectrum and spectrogram taps and a dedicated compact
  dB meter node (vertical by default, horizontal from its menu). The glow can be
  disabled in Settings.
- **Live recording feedback** â€” each recorder shows NO FILE / ARMED / REC /
  SAVED / ERR, a global REC timer appears in the title bar, and an output that
  clips gets its own CLIP badge.
- **Presets** â€” the whole graph (nodes, cables, effect settings) saves to a
  `.ruse.json` file.
- **Start with Windows** â€” an option in Settings adds or removes Ruse from the
  per-user startup apps.
- **Close to tray** â€” with the option on, closing the window hides Ruse in the
  notification area while audio keeps routing; minimizing just minimizes. Click
  the tray icon to restore it or use its menu to exit.
- **Start routing on launch** â€” optionally start the restored graph after the
  window is ready. The compact Routing switch shows ON/OFF and glows green
  while running; turning it off stops the graph.
- **Undo/redo** for everything, including node moves and effect tweaks.
- **Low latency** - the software path adds about 28 ms steady (~38 ms peak) on a modern machine; the full source-to-speaker figure includes capture backlog.
- **Diagnostics** â€” an optional live diagnostics canvas node shows audio
  buffers/delays, source fill rate, output ring fill, underruns/skips per
  minute, latency mean/jitter, estimated clock drift, engine block, output
  cushion, endpoint/render latency, CPU/GPU use, memory, FPS and JS heap. It is
  draggable, scalable and closable. A built-in self-test suite (`--selftest`
  and friends) exercises the engine without hardware. The offline
  `--transparency-test` harness checks nulling, 44.1/96k resampling and 5.1
  downmix, and writes reference/degraded WAVs for an optional external ViSQOL run.
- **Languages** â€” English, Portuguese (Brazil), Spanish, French, German,
  Italian, Japanese, Korean and Simplified Chinese; switchable live.

## Building

```bat
dotnet build src\Ruse.App\Ruse.App.csproj
```

Needs the .NET 10 SDK and Node.js 20+. The build installs the Web UI packages on
first use and compiles the React assets into the app output. The app itself is
`src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`.

The Web UI translation files are validated with `npm run check-locales` in
`src\Ruse.App\WebUi`; `npm test` runs the UI unit tests.

For a repeatable check, run
`powershell -ExecutionPolicy Bypass -File scripts\regress-ruse.ps1 -Build`.
Add `-ProcessId <pid>` for live graph/device tests and `-Vst3Plugin <path>` for
the isolated VST3 test.

Speech and translation models are downloaded from Settings > Speech &
translation. Whisper models run speech recognition. Translation uses Qwen2.5
GGUF by default (0.5B, 1.5B or 3B); Settings > Speech & translation >
Translation engine can switch to optional OPUS-MT ONNX models, downloaded per
language pair. OPUS-MT covers English with German, Spanish, French, Italian,
Portuguese and Chinese, plus those languages and Japanese/Korean into English;
other pairs pivot through English. English to Japanese or Korean falls back to Qwen when installed.
Inference runs on the CPU by default; enable GPU acceleration to use a
Vulkan-capable GPU. It falls back to the CPU when no Vulkan device is available.

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
| --transparency-test [outdir] | Offline null/SRC/downmix checks; writes WAVs for optional ViSQOL |
| `--monitor-test --pid <pid> [--seconds N] [--output-match NAME]` | Live source/ring/drift/under-run click monitor |
| `--translation-text-test <model> <target> <text> <out>` | Checks one local Qwen model without a route |
| `--opus-test <source> <target> <text> <out>` | Runs one installed OPUS-MT pair, including an English pivot |
| `--opus-download-test <pair> <out>` | Downloads and verifies one OPUS-MT pair |
| `--translation-e2e-test <speech> <translation> <target> <wav> <out>` | Runs a WAV through the speech-and-translation pipeline (add `--opus`) |
| `--vst3-test <plugin> <out>` | Loads a VST3, processes a tone and saves state |
| `--vst3-isolated-test <plugin> <out>` | Runs a VST3 in the isolated host process |
| `--vst3-bench-test <plugin> <out> [--blocks N]` | Measures per-block isolated vs in-process VST3 cost |
| `--cable-test <input.wav> <wet.wav> <result.txt> [--seconds N] [--render-match NAME] [--capture-match NAME]` | Runs a virtual-cable input/output loop with effects and rnnoise |
| `--vst3-route-test <plugin> <out>` | Runs a VST3 through the real route renderer |
| `--vst3-editor-test <plugin> <out>` | Opens and closes a VST3 editor |
| `--vst3-scan-test` | Lists the VST3 effects found in the standard folders |
| `--sessions` | Lists the audio sessions and the process that owns each |

## Packaging

For a machine without .NET, build the self-contained package:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package-ruse.ps1
```

The script publishes, copies `WebUi/dist`, moves satellite resources under
`Ruse\Localization\<culture>`, adds README/LICENSE and writes the `.zip` and
`.sha256` files. Models, OPUS-MT ONNX files, VST3 plugins and ASIO drivers are
not bundled; the target machine still needs the WebView2 Runtime.

## How it is put together

```
src/Ruse.Engine/
  Audio/          UI-independent engine
    AudioGraph.cs         owns sources, modifiers and outputs
    SourceNode.cs         a capture source (app, device, system, ASIO)
    OutputNode.cs         a render endpoint (WASAPI, ASIO or file)
    ModifierNode.cs       an effect chain on a route
    Vst3Processor.cs      VST3 plugin effect adapter
    IsolatedVst3Processor.cs  out-of-process VST3 proxy
    Vst3HostRunner.cs     child-process plugin host
    FftAnalyzer.cs        radix-2 FFT for the analysis taps
    SpectrumProcessor.cs  smoothed log-frequency spectrum tap
    SpectrogramProcessor.cs  scrolling FFT history tap
    GraphicEqualizerProcessor.cs  NAudio 10-band ISO equalizer wrapper
    (the effect processors and the preset model also live in Audio/)
  Diagnostics/    the log
src/Ruse.App/
  WebMainWindow     native WebView2 host
  WebShellController  serialized command/state bridge
  WebShellController.Diagnostics.cs  CPU/GPU/audio diagnostics snapshot
  GpuUsage.cs       Windows GPU Engine usage sampler
  TranscriptionService.cs  Whisper recognition workers
  TranslationService.cs    speech translation workers (Whisper + Qwen/OPUS)
  TranslationModels.cs     Qwen catalog and downloads
  OpusTranslationModels.cs OPUS-MT catalog and downloads
  OpusMtEngine.cs          ONNX Runtime encoder/decoder loop
  TranslationModelProbe.cs child-process GGUF load check
  StartupErrorDialog.xaml  native fallback when WebView2 cannot start
  WebUi/            React + TypeScript workspace
```

The engine always mixes at **48 kHz, stereo, 32-bit float**. Sources are
converted into that format, effects run in it, and outputs convert out of it â€”
WASAPI lets Windows do the conversion, ASIO drivers are resampled by Ruse when
the driver runs at another rate.

## Licences

NAudio is MIT licensed and ships the effect suite (including the 10-band
GraphicEqualizer) and the WASAPI/ASIO layers. Whisper.net, LLamaSharp and
OwnAudioVst are MIT; the VST 3 SDK is MIT. Translation models are Qwen2.5 GGUF
files under Apache-2.0. Ruse itself is MIT licensed; see `LICENSE` and
`THIRD-PARTY-NOTICES.md`.
