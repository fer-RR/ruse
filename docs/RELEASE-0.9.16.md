# Ruse 0.9.16

This release finishes the post-0.9.15 reliability and quality-of-life pass.

## File player and media

- Fixed **Choose file**, **Analyze** and **Loop** on file player nodes.
- Media & Samples supports Ctrl/Shift selection, checkboxes and multi-file drag.
- Drop multiple files onto the canvas or timeline; timeline clips are placed
  back-to-back.
- Added batch canvas/timeline actions and a selection count/clear control.
- Library sections start expanded and remember their collapse state.

## Reliability

- Fixed Silero VAD double-dispose and reduced its per-frame allocations.
- Vosk partials now run only for English speech models and reuse a frame buffer.
- Translation memory uses a debounced append journal with atomic compaction.
- Unchanged transcript text is no longer resent in every telemetry tick.
- TTS/Vosk/Piper extraction is cancellable and reserves extraction space.
- Closing the timeline now stops playback and recording.
- Settings only republish the graph when a graph-visible model changes.
- `vad_speech.wav` is now included in published builds.

## Storage

- Downloadable models moved to `%LOCALAPPDATA%\Ruse\models`.
- Existing Roaming model folders are moved or merged on startup.
- Shortcut creation is available from Settings > Help.

## Known issue

- The VST3 isolated-host test still times out with the installed plugins; the
  normal VST3 route test passes.
- OPUS-MT KV-cache decoding remains blocked on the current pinned decoder model,
  which does not expose past key/value inputs.

## Validation

- Debug and Release builds: 0 warnings.
- UI tests: 35 passed.
- Regression suite: 18 passed.
- Packaged `--web-test`: PASS.
- DSP self-test: 35 checks.
- Transcription, Kokoro TTS, Supertonic batch TTS, OPUS translation and the
  Whisper/OPUS translation pipeline passed live checks.
