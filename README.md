# Ruse

Ruse is a node-based audio router for Windows. Route applications, system
audio, microphones, audio files, or test signals through a live effect chain to
speakers, headphones, ASIO interfaces, virtual buses, or a recorder.

Download the latest Windows build from [Releases](https://github.com/fer-RR/ruse/releases).

## Highlights

- **Flexible sources**: per-application capture, system loopback, WASAPI
  microphones, ASIO inputs, virtual buses, file playback, and a test tone.
- **Flexible destinations**: WASAPI and ASIO outputs, virtual cables, and WAV,
  MP3, or FLAC recording.
- **51 built-in effects** including EQ, dynamics, reverb, delay, modulation,
  pitch shift, auto-tune, and an experimental formant-preserving TD-PSOLA
  auto-tune.
- **Vocal note detection**: a monophonic note detector with a key output for the Auto-tune nodes.
- **Network audio**: Ruse-to-Ruse UDP PCM input and output nodes for LAN or VPN routing.
- **Analysis taps**: spectrum, spectrogram, dB meter, loudness/true-peak meter,
  and diagnostics.
- **VST3 support**: isolated plugin hosting by default, so a crashing plugin
  does not take down Ruse.
- **Mixing and routing**: sidechain inputs, per-send gain, Pre/Post sends,
  mute/solo, mixer dock, saved chains, and frame templates.
- **Workflow**: undo/redo, MIDI learn, presets, start with Windows, close to
  tray, automatic device refresh, and nine UI languages.
- **Speech tools**: offline transcription with Whisper, local translation with
  Qwen2.5 or optional OPUS-MT models, and offline text to speech with
  Supertonic 3 or Kokoro. Text can be typed, loaded from a file, or taken from a
  transcriber or translator node.
- **Low-latency engine**: 48 kHz, stereo, 32-bit float, with click-free gain and
  parameter changes and a built-in diagnostics node.

## Requirements

- Windows 10 or 11, x64.
- Per-application capture requires Windows 11 or Windows Server 2022
  (build 20348 or newer).
- The packaged build needs the Microsoft Edge WebView2 Runtime.
- Building needs the .NET 10 SDK and Node.js 20 or newer.

## Build

```powershell
dotnet build src\Ruse.App\Ruse.App.csproj
```

The build installs the Web UI packages on first use and compiles the React
assets into the app output.

The app is written to:

```text
src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe
```

## Test

```powershell
powershell -ExecutionPolicy Bypass -File scripts\regress-ruse.ps1 -Build
```

Useful checks:

```powershell
Ruse.exe --effects-test
Ruse.exe --web-test
Ruse.exe --selftest
```

`--selftest` writes `latency-selftest.txt`. Add `-ProcessId <pid>` to the
regression script for live graph/device tests, or `-Vst3Plugin <path>` for the
isolated VST3 test.

## Package

Build a self-contained Windows package for machines without .NET:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package-ruse.ps1
```

The script writes `Ruse-<version>-win-x64.zip` and its `.sha256` file. Models,
VST3 plugins, and ASIO drivers are not bundled.

## Documentation

- [Handoff and project state](docs/STATE.md)
- [Backlog](docs/BACKLOG.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

## License

Ruse is MIT licensed. See [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). NAudio, Whisper.net,
LLamaSharp, OwnAudioVst, and the VST 3 SDK are MIT licensed. Bundled Qwen2.5
models are Apache-2.0.
