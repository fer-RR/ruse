# Ruse — Send audio anywhere on Windows

**English** · [Português (Brasil)](README.pt-BR.md)

A free, open-source audio router with a visual canvas. Capture sound from apps,
the whole PC or a microphone, shape it with effects, and send it to speakers,
headphones, virtual cables, another computer or a recorder — all from one place.

[Download for Windows](https://github.com/fer-RR/ruse/releases/latest) · [Features](#features) · [Quick start](#quick-start) · [Common setups](#common-setups) · [Build from source](#build-from-source)

[![Latest release](https://img.shields.io/github/v/release/fer-RR/ruse?label=latest&sort=semver)](https://github.com/fer-RR/ruse/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![Platform: Windows 10 and 11](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue.svg)](#requirements)

Ruse is a desktop audio router for Windows 10 and 11. Build a routing graph by
dragging sources, effects and destinations onto a canvas and connecting them
with cables. It is **not** a DAW, it does **not** install a kernel driver, and
ASIO is temporarily unavailable in this build. It uses the audio devices you
already have, including virtual cables such as VB-CABLE and Voicemeeter.

Everything runs on your PC. Ruse never uploads your audio. Optional
transcription, translation and text-to-speech models also run locally after you
download them.

## What you can do with Ruse

- **Hear one app in two places.** Listen to a game in your headphones while
  sending a copy to a virtual cable for streaming or recording.
- **Keep voice and game audio separate.** Route Discord, a game, a microphone
  and music to different devices with independent levels.
- **Make a microphone sound better.** Add EQ, compression, noise reduction, a
  de-esser, a gate and a limiter before the mic reaches Discord, OBS or a call.
- **Build a monitor mix.** Use the mixer, sends, sidechain ducking and
  per-route processing for a small live or streaming setup.
- **Make audio easier to hear.** Use the hearing profile for per-ear EQ and gain,
  lip-sync delay, loudness matching and a safety limiter.
- **Record the final mix.** Save WAV, FLAC or MP3 from any route, or render the
  timeline offline.
- **Send audio to another PC.** Use a Ruse-to-Ruse link over your local network.
- **Use your favourite plugins.** Host VST3 effects and instruments safely.
- **See what is happening.** Watch spectrum, spectrogram, waveform, level,
  loudness and true-peak meters.
- **Automate it.** Control the graph with MIDI, keyboard shortcuts or the
  optional local control API.

## Features

### Routing

- Capture per-app audio, system audio, microphones and line inputs.
- Send to speakers, headphones and existing virtual cables.
- Files, test tones, a metronome, a sound generator and network input as sources.
- Direct source-to-device routes, plus native passthrough for untouched audio.
- Add effects between any two nodes, or drop them straight onto a cable.
- Drop a node on top of another node and Ruse connects them for you.

### Effects and plugins

- **63 built-in effects**, including EQ and filters, dynamics, gates,
  saturation, pitch and auto-tune, vocoder, delays, reverbs, convolution and
  character processors.
- VST3 effects **and instruments**, hosted in a separate process so a plugin
  crash cannot close Ruse.
- Save and load plugin presets, and open plugin editors on top of the app.
- Effect chains, saved chains and reusable frame templates.
- Mixer groups with their own gain, mute and solo.

### Mixer and timeline

- Big mixer with level, pan, polarity, delay, A/B crossfade, width, auto-gain
  and per-bus meters.
- Sends, sidechain key level, polarity and mute.
- Timeline with tracks, clips, fades, gain, pitch, stretch, automation, markers
  and regions.
- Multi-select editing, normalize, reverse, consolidate and offline bounce or
  freeze.

### Speech, when you want it

- Local Whisper transcription and translation.
- Live partial transcripts with Vosk, running alongside Whisper.
- Text to speech with Kokoro or Piper, with barge-in and ducking.
- Choose incoming network text as a translation or speech source.

### Output and recording

- WAV (16/24-bit or 32-bit float), FLAC and MP3 recording.
- Timeline bounce and freeze with the full routing graph.
- Ruse-to-Ruse UDP audio on a trusted local network.
- Verified release packages and an optional in-app updater.

### Extras

- Five themes: Dark, Light, Blueprint, Studio '74 and Vesper.
- Keyboard shortcuts, a motion setting that can override Windows, and
  screen-reader labels.
- English, German, Spanish, French, Italian, Japanese, Korean, Portuguese
  (Brazil) and Chinese (Simplified) interface.
- Optional local control API for scripts and automation.

## Download and install

1. Open the [latest release](https://github.com/fer-RR/ruse/releases/latest).
2. Download `Ruse-<version>-win-x64.zip`.
3. Extract the ZIP and run `Ruse.exe`.
4. Optionally verify the download with the published `.sha256` file.

The package is self-contained and includes the .NET runtime. It does not include
VST3 plugins or speech/translation model files.

## Requirements

- **System:** Windows 10 or 11, 64-bit.
- **Per-app capture:** Windows 11 or Windows Server 2022.
- **Runtime:** Microsoft Edge WebView2 Runtime. Windows 11 includes it; most
  Windows 10 devices have it, and Ruse links to the installer if it is missing.
- **Building from source:** .NET 10 SDK and Node.js 20 or newer.
- **Models and drivers:** Whisper, translation, text-to-speech and VST3 plugins
  are installed separately.
- **Network link:** same trusted LAN, unencrypted UDP audio and TCP text.

## Quick start

1. **Add a source.** Drag an app, system audio, a microphone, a file or a tone
   from the library onto the canvas.
2. **Add a destination.** Drag speakers, headphones, a virtual cable, a recorder
   or a network link onto the canvas.
3. **Connect.** Drag from the source's **OUT** port to the destination's **IN**
   port — or simply drop one node on top of the other.
4. **Add processing (optional).** Drop an effect onto a cable or between nodes.
5. **Start routing.** Press the power button in the toolbar. Open the **Mixer**
   for levels and the **Timeline** for clips and automation.

The first-run tour and the `?` help buttons explain the main workflows.

## Common setups

| Goal | Suggested route |
| --- | --- |
| Hear a game and send it to Discord | Game → EQ → Headphones and VB-CABLE |
| Use a microphone in Discord and OBS | Microphone → High-pass → Compressor → Limiter → VB-CABLE |
| Record a stream mix | Apps + microphone → Mixer → File recorder |
| Send audio and text to a second PC | Source → Network link here; Network link → speakers there |
| Keep music out of a call | Music → Headphones only; microphone → call output |
| Add a VST3 effect | Source → VST3 node → destination |
| Make TV or games easier to hear | App or system audio → Hearing profile → Headphones |

## Your files

Ruse keeps user content together in `Documents\Ruse`:

- `Presets` — saved routing presets.
- `Recordings` — recorder and timeline recordings.
- `Samples` — imported samples and media.
- `Exports` — timeline bounces, consolidated clips and transcript exports.
- `Notes` — saved text notes.
- `Support` — exported support bundles.

Settings, sessions, chains, templates, logs and downloaded models stay in
`%APPDATA%\Ruse` and `%LOCALAPPDATA%\Ruse`. Settings > Info shows these locations
and opens your user folder. Older `Music\Ruse Samples` and `Music\Ruse
Recordings` folders are still read so existing presets keep working.

## Hearing profile and audiograms

The **Hearing profile** node can import a real audiogram from CSV or JSON. Values
are treated as air-conduction thresholds in **dB HL**. Ruse fits them onto its
hearing bands, previews PTA4/severity and the applied gains, and builds a
conservative half-gain starting profile. Adjust it by ear afterwards — it is not
a clinical fitting.

```csv
frequency,left,right
250,30,20
500,40,30
1000,45,35
2000,50,40
4000,55,45
8000,60,50
```

OpenHear audiogram JSON is supported too.

## Build from source

```powershell
dotnet build src\Ruse.App\Ruse.App.csproj
```

Output: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`

The build also compiles the React interface in `src\Ruse.App\WebUi`.

## Test and package

```powershell
powershell -ExecutionPolicy Bypass -File scripts\regress-ruse.ps1 -Build
powershell -ExecutionPolicy Bypass -File scripts\package-ruse.ps1
```

Individual checks: `Ruse.exe --effects-test`, `--dsp-test`, `--web-test`,
`--chat-test`, `--selftest`.

Third-party licence audit:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\audit-licenses.ps1
```

## Documentation

- [Backlog](docs/BACKLOG.md)
- [Control API](docs/CONTROL-API.md)
- [Release notes](docs/RELEASE-0.9.22.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

## Contributing

Issues and pull requests are welcome. Before opening a PR, run the regression
script above and describe the audio device, driver and Windows version you used.
Real-device results are especially useful for routing, VST3 and low-latency work.

## License

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
