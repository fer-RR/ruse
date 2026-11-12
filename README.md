# Ruse

Ruse is a node-based audio router for Windows. Drag apps, system audio, microphones, ASIO inputs, files or a test tone onto the graph, add effects, and send the result to speakers, headphones, ASIO, virtual cables or a recorder.

Everything runs locally. Ruse does not upload audio.

## What it does

- Per-app capture, system loopback, WASAPI and ASIO inputs, virtual buses, file playback and a test tone.
- WASAPI and ASIO outputs, virtual cables, and WAV/MP3/FLAC recording.
- 61 effects, including EQ, dynamics, filters, pitch, auto-tune, vocoder, delays, reverb and character effects.
- Native passthrough for direct source-to-device or source-to-recorder routes. Native-safe gain/shaping effects and analysis taps can run in the chain without converting the sample rate.
- Spectrum, spectrogram, waveform, dB and loudness meters.
- VST3 hosting in an isolated process.
- Sidechains, per-send gain, mixer, saved chains, frame templates, MIDI learn and undo/redo.
- Offline transcription, translation and text to speech.
- A local HTTP/WebSocket control API, off by default.
- Manual or scheduled update checks against GitHub releases, SHA-256 verified.

## Requirements

- Windows 10 or 11, x64.
- Per-app capture needs Windows 11 or Windows Server 2022.
- The packaged build needs the Microsoft Edge WebView2 Runtime.
- Building needs the .NET 10 SDK and Node.js 20 or newer.

## Build

```powershell
dotnet build src\Ruse.App\Ruse.App.csproj
```

Output: `src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe`

## Test

```powershell
powershell -ExecutionPolicy Bypass -File scripts\regress-ruse.ps1 -Build
```

Individual checks: `Ruse.exe --effects-test`, `--dsp-test`, `--web-test`, `--selftest`.

## Package

```powershell
powershell -ExecutionPolicy Bypass -File scripts\package-ruse.ps1
```

This writes `Ruse-<version>-win-x64.zip` and its SHA256 file. Models, VST3 plugins and ASIO drivers are not bundled.

## Docs

- [State and handoff](docs/STATE.md)
- [Backlog](docs/BACKLOG.md)
- [Control API](docs/CONTROL-API.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

## License

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
