# Ruse 0.9.10

This release adds the advanced mixer, a more musical timeline, and fixes silent transcription failures on quiet sources.

## Mixer

- Added per-input level, pan, mute, solo, polarity invert, and alignment delay.
- Added master stereo width, auto-gain, and a constant-power A/B crossfader.
- Added an L/R summed-bus meter with held peaks and a latching clip indicator.
- Mixer settings now survive presets, undo/redo, node duplication, cable insertion, and bridge removal.
- Live mixer changes update without rebuilding the audio graph; delay changes rebuild only when needed.

## Timeline

- Added named markers and regions, with seek, rename, and delete actions.
- Added time-signature controls and a bar/beat ruler with bar-beat playhead position.
- Bar snapping, metronome accents, and count-in now follow the time signature.
- Added multi-clip selection with copy, cut, paste, and batch delete.
- Added timeline context actions for markers, regions, and clipboard operations.
- Timeline undo now preserves generator track IDs and clip loop state.
- Timeline preset data now includes markers, regions, and time signature.

## Transcription

- Fixed a bug where quiet online-video audio showed input level but never produced text.
- The voice detector no longer lets its noise floor drift upward while speech is present.
- Added a full-chunk fallback for audible speech that never crosses the voice threshold.
- Quiet chunks are normalized before Whisper for both transcription and translation.
- Added a quiet-speech regression test and a `--transcription-test` diagnostic mode.

## Reliability and packaging

- Native passthrough still auto-disables when an output gains a second route and can be restored safely.
- Package output uses the self-contained layout with the required Whisper and VST3 native libraries.
- Locale checks cover the new mixer and timeline strings.

## Validation

- 18/18 regression tests pass.
- UI tests pass.
- Medium multilingual Whisper tests pass on normal, mixed-background, quiet, and very quiet speech.
- `whisper-medium.en` transcription passes.
- TypeScript, locale, and production Vite builds pass.
