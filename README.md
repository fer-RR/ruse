# Ruse — Windows Audio Router

**Send audio anywhere on Windows.** Ruse is a free, open-source audio router with a visual node graph. Capture audio from apps, the whole PC or microphones, shape it with effects, and send it to speakers, headphones, virtual cables, another PC or a recorder.

[Download for Windows](https://github.com/fer-RR/ruse/releases/latest) · [Features](#features) · [Quick start](#quick-start) · [Common setups](#common-setups) · [Build from source](#build-from-source)

[![Latest release](https://img.shields.io/github/v/release/fer-RR/ruse?label=latest&sort=semver)](https://github.com/fer-RR/ruse/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![Platform: Windows 10 and 11](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue.svg)](#requirements)

Ruse is a desktop audio router for Windows 10 and 11. It gives you one place to capture, process, monitor, record and forward sound. Instead of stacking several utilities, you build a routing graph: drag a source and a destination onto the canvas, connect them with a cable, and add effects in between.

It is **not** a DAW, and it does **not** install a kernel driver. ASIO support is temporarily unavailable in this build. It works with the audio devices you already have, including virtual cables such as VB-CABLE and Voicemeeter.

Audio is processed locally. Ruse does not upload your audio. Optional transcription, translation and text-to-speech models run on your machine after download.

## What you can do with Ruse

- **Send one app to two devices.** Hear game audio in headphones while sending a copy to a virtual cable for streaming.
- **Keep voice and game audio separate.** Route Discord, a game, a microphone and music to different devices with independent levels.
- **Clean up a microphone.** Add high-pass, EQ, compression, noise reduction, de-esser, gate and limiter before the mic reaches Discord, OBS or a call.
- **Build a monitor mix.** Use the mixer, sends, sidechain ducker and native-safe processors for a small live or streaming setup.
- **Make audio easier to hear.** Use the hearing profile for per-ear EQ/gain, lip-sync delay, loudness matching and a safety limiter.
- **Import a real audiogram.** Load a CSV or JSON hearing test, preview PTA4/severity, and generate a per-ear starting profile.
- **Record the final mix.** Record WAV, FLAC or MP3 from any route, or render a timeline mix offline.
- **Send audio to another PC.** Use Ruse-to-Ruse UDP audio over your local network.
- **Process with VST3.** Host VST3 effects in an isolated process so a plugin crash does not take down the whole app.
- **Analyze the signal.** View spectrum, spectrogram, waveform, dB, loudness and true-peak meters.
- **Automate the graph.** Use the optional local HTTP/WebSocket control API, MIDI learn and keyboard shortcuts.

## Features

### Routing

- Per-app audio capture, system loopback, microphones and line inputs.
- WASAPI outputs with low-latency shared mode where the driver supports it.
- Existing virtual cables and virtual buses.
- File playback, test-tone generator, network input and network output.
- Direct source-to-device routes and **native passthrough** without sample-rate conversion.
- Sidechain inputs, per-send gain, mute/solo and an advanced mixer with per-input level/pan/polarity/delay, A/B crossfade, width, auto-gain and bus metering.

### Processing

- **63 built-in effects**, including EQ, filters, dynamics, hearing profiles, gates, saturation, pitch, auto-tune, vocoder, delays, reverbs, convolution and character effects.
- VST3 hosting in an isolated child process.
- Effect chains, saved chains, frame templates, per-effect bypass and mixer groups with independent gain/mute/solo.
- Freeze a file or sample source through its routed effects into a rendered proxy, then bypass the original route. Generated proxies are removed when their source node is deleted or its file is replaced.
- Timeline tracks, clips, fades, gain, pitch, stretch, automation, markers, regions, time signatures, multi-select clipboard editing, normalize/reverse/consolidate and offline bounce/freeze.
- Consolidate renders raw track audio (clip gain, fades and pitch) without insert effects; use bounce or freeze for full graph rendering.

### Speech

- Shared 16 kHz speech tap with VAD endpointing and pre-roll.
- Silero VAD with an energy fallback for low-power or missing-model cases.
- Optional Vosk English partial transcripts while Whisper produces the final transcript; install the 40 MB model from Settings > Speech & translation > Live speech.
- Kokoro 82M and Piper are selectable streaming TTS backends. Kokoro uses the MIT KokoroSharp wrapper; Piper runs as an optional external engine because its maintained runtime is GPL-3.0.
- TTS barge-in/ducking when new speech begins.
- Clause-level TTS streaming with lag-aware catch-up and stale-speech skipping.
- Local Whisper transcription, translation and TTS.

### Output and recording

- Speakers, headphones and virtual cables.
- WAV (16/24-bit, 32-bit float), FLAC and MP3 recording.
- Timeline bounce and freeze with full graph rendering: sends, sidechains, link gains and latency alignment. VST3 effects are skipped in the offline path.
- Ruse-to-Ruse UDP audio for a trusted local network.
- SHA-256 verified release packages and an optional in-app updater.

### Extras

- Optional local Whisper transcription, translation and text-to-speech.
- Local control API for scripts and automation, off by default.
- Five UI themes: Dark, Light, Blueprint, Studio '74 and Receiver.
- Keyboard shortcuts, an in-app motion setting that can override Windows' reduce-motion preference and screen-reader labels.
- English, German, Spanish, French, Italian, Japanese, Korean, Portuguese (Brazil) and Chinese (Simplified) UI.

## How it works

```mermaid
flowchart LR
    A[Apps] --> G[Ruse routing graph]
    B[System audio] --> G
    C[Microphone] --> G
    D[Files / test tone] --> G
    E[Network input] --> G
    G --> F[EQ · dynamics · filters · VST3 · meters]
    F --> H[Speakers / headphones]
    F --> I[Virtual cable]
    F --> J[WAV / FLAC / MP3 recorder]
    F --> K[Network output]
```

Each node is a source, processor or destination. Cables carry audio. You can insert effects directly on a cable, create sidechain routes, and save a chain or frame as a template.

## Download and install

1. Open the [latest release](https://github.com/fer-RR/ruse/releases/latest).
2. Download `Ruse-<version>-win-x64.zip`.
3. Extract the ZIP and run `Ruse.exe`.
4. Optionally verify the download with the published `.sha256` file.

The package is self-contained and includes the .NET runtime. It does not include VST3 plugins or speech/translation model files.

## Requirements

- **OS:** Windows 10 or 11, x64.
- **Per-app capture:** Windows 11 or Windows Server 2022.
- **Runtime:** Microsoft Edge WebView2 Runtime. Windows 11 includes it; most Windows 10 devices have it, and Ruse links to the installer if it is missing.
- **Build tools:** .NET 10 SDK and Node.js 20 or newer.
- **Network audio:** Ruse-to-Ruse UDP PCM on the same trusted LAN. It is not encrypted.
- **Models and drivers:** Whisper, translation, TTS and VST3 plugins are installed separately.

## Quick start

1. **Add a source.** Drag an app, system audio, microphone, file, tone or network input from the Library onto the canvas.
2. **Add a destination.** Drag speakers, headphones, a virtual cable, a recorder or a network output onto the canvas.
3. **Connect them.** Drag from the source's **OUT** port to the destination's **IN** port.
4. **Add processing (optional).** Drag an effect onto the cable or between two nodes.
5. **Start routing.** Press the power button in the toolbar. Open the **Mixer** to adjust levels and the **Timeline** for clips and automation.

The first-run tour and the `?` help buttons explain the main workflows.

## Common setups

| Goal | Suggested route |
| --- | --- |
| Hear a game and send it to Discord | Game app → High-pass/EQ → Headphones and VB-CABLE |
| Use a microphone in Discord and OBS | Microphone → High-pass → Compressor → Limiter → VB-CABLE |
| Record a stream mix | Apps + microphone → Mixer → File recorder |
| Send audio to a second PC | Source → Network output; Network input → speakers on the other PC |
| Keep music out of a call | Music app → Headphones only; microphone → call output |
| Add a VST3 effect | Source → VST3 node → destination; open the plugin editor in the node |
| Make TV or games easier to hear | App or system audio -> Hearing profile -> Headphones |

## Folders

Ruse keeps user content together in `Documents\Ruse`:

- `Presets` — saved routing presets (`.ruse.json`).
- `Recordings` — recorder and timeline recordings.
- `Samples` — imported samples and files used by the file player and timeline.
- `Exports` — timeline bounces, consolidated clips and transcript exports.

Settings, session, effect chains, frame templates, logs and downloaded models
stay in `%APPDATA%\Ruse`. Settings > Info shows these locations and opens the
user folder. Existing `Music\Ruse Samples` and `Music\Ruse Recordings` folders
are still read so older presets and recordings keep working.

## Audiogram import

The **Hearing profile** node can import a real audiogram from CSV or JSON. Values are treated as air-conduction thresholds in **dB HL**. Ruse interpolates them onto its 8 hearing bands, previews PTA4/severity and the applied gains, and generates a conservative half-gain starting profile. Adjust it by ear after import; it is not a clinical fitting.

CSV:

```csv
frequency,left,right
250,30,20
500,40,30
1000,45,35
2000,50,40
4000,55,45
8000,60,50
```

JSON:

```json
{
  "unit": "db_hl",
  "frequencies": [250, 500, 1000, 2000, 4000, 8000],
  "left": [30, 40, 45, 50, 55, 60],
  "right": [20, 30, 35, 40, 45, 50]
}
```

OpenHear audiogram JSON (`right_ear`/`left_ear` with `thresholds`) is also supported.

## Build from source

```powershell
dotnet build src\Ruse.App\Ruse.App.csproj
```

Output: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`

The build also compiles the React UI in `src\Ruse.App\WebUi`.

## Test

```powershell
powershell -ExecutionPolicy Bypass -File scripts\regress-ruse.ps1 -Build
```

Individual checks: `Ruse.exe --effects-test`, `--dsp-test`, `--web-test`, `--selftest`.

## Package a release

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package-ruse.ps1
```

This writes `Ruse-<version>-win-x64.zip` and its SHA-256 file to the package folder.

## Documentation

- [Backlog](docs/BACKLOG.md)
- [Control API](docs/CONTROL-API.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

## Contributing

Issues and pull requests are welcome. Before opening a PR, run the regression script above and describe the audio device, driver and Windows version used for testing. Real-device results are especially useful for WASAPI, VST3 and low-latency work.

## License

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
