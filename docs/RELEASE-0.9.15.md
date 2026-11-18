# Ruse 0.9.15

This release brings the media library into the main library, adds audio file
analysis, and includes a broad reliability and workflow pass.

## Media and files

- **Media & Samples** is now one library section with the main library search.
- Add folders, add samples, sort, refresh and open folders from the section header.
- Remove a folder with confirmation; files on disk are never deleted.
- Lazy waveform previews, inline rename, reveal in Explorer and drag-to-timeline.
- Drop files or library items on an output node to create a connected file player.
- File players can analyze format, levels, LUFS, clipping, DC, balance and more.

## Editing

- Adaptive undo limits keep large graphs and timelines within a memory budget.
- Band-pass and all-pass filters added; the effect catalog now has 63 effects.
- Frames can be dragged as a group; a contextual tip introduces frame dragging.
- Timeline header actions fit on one line; zoom controls sit beside Close.
- Follow Windows, reduced-motion and full-motion modes.

## Reliability

- Fixed stale WebView assets causing a blank window after updates.
- Fixed session deletion, automation races, TTS lifetime, single-instance launch,
  time-signature snapping, group fader flooding and several shutdown races.
- Settings writes are safer and normal shutdown no longer loses media folders.
- New file-analysis and waveform readers are bounded and cancellable.

## Welcome

- **Add desktop shortcut** is now on the welcome screen next to
  **Explore on my own**.

## Validation

- Debug and Release builds complete with no warnings.
- UI tests: 35 passed.
- Regression suite: 18 passed.
- DSP self-test: 34 checks.
- Locale files: 997 entries each.
