# Backlog - ideas and decisions

Nothing here is started unless noted. Keep this list short and current.

## Before new feature work

- Commit the uncommitted WebView2/engine/transcriber migration. The worktree is
  far ahead of `9269ff1`; do not reset or clean it.
- Run the full regression and `--web-test` after committing.

## Transcriber

- End-to-end test the Settings model Download button.
- Add model download progress, cancel, delete, and checksum validation.
- Share one Vosk `Model` across sessions instead of loading it per node.
- Add a retry action when a model download or load fails.
- Decide how multiple routes feeding one transcriber tap should behave.
- Add punctuation/capitalization and transcript export (.txt/.srt).
- Test real microphone and non-English speech, not only synthesized English.

## Audio engine

- Per-node output device switching without deleting/re-adding the node.
- VST3/CLAP hosting. Needs a native C++ host, Steinberg SDK acceptance, IPC,
  and an HWND plugin editor. Not started.
- Sidechain and MIDI-trigger support.
- A mixer view beside the node graph (channel strips with fader/pan/mute).
- Optional GPU inference for Whisper if a chunked, higher-accuracy mode is
  added later. Current real-time node uses Vosk.

## UI / platform

- Investigate why WPF renders at software tier 0 on this machine. D3D9, D3D11
  and DXGI work; driver and October 2026 update are suspects.
- Decide whether to keep the legacy WPF shell long-term or reduce it to a
  fallback/test harness.
- Add a model-management summary to diagnostics (disk use, installed languages).
- Add a compact "what changed" or release note surface for presets/models.
