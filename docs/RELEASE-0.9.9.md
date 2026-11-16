# Ruse 0.9.9

This release focuses on timeline reliability, clip pitch, native passthrough safety, and theme polish.

## Timeline

- Timeline colors now follow the active theme while staying darker than the main interface.
- Blueprint nodes use a light-blue face with a white border and dark text.
- Track and automation labels are pinned in a DOM overlay instead of redrawing the canvas during panning.
- Track names and linked track-node names stay synchronized, including undo/redo.
- Added compact Play, Pause, and Stop icons. Stop returns the playhead to the start.
- Timeline playback no longer starts automatically when Ruse opens.
- Added a clip context menu for Split, Duplicate, Crossfade, and Clip pitch automation.
- Right-click no longer deletes clips; Delete/Backspace remains the removal shortcut.

## Clip pitch and automation

- Added per-clip pitch control in semitones.
- Clip pitch preserves duration using overlap-add granular pitch shifting.
- Clip pitch can be automated over time from the track automation menu.
- Static clip pitch and live automation are stored separately, so playback no longer overwrites the clip's base pitch.
- Automation lanes are taller, with larger points, wider hit areas, hover cursors, and a live value readout.

## Native passthrough

- Connecting another route to an output automatically disables native passthrough for that output.
- Native passthrough cables now use a distinct green dashed appearance with a lock label.
- Cables revert to the regular appearance immediately when native passthrough is disabled.

## Reliability

- Timeline tracks now recover from older sessions whose saved source IDs no longer matched their node IDs.
- Restored source nodes keep their saved IDs, preventing future timeline source mismatches.
- Preset source repair skips generator tracks and avoids attaching the wrong source by title.
- Removed realtime allocations from clip-pitch automation rendering.
- Removed obsolete clip-removal UI and locale strings.

## Validation

- 18/18 regression tests pass.
- Web self-tests, including clip pitch duration, automation overrides, track-name undo sync, and native passthrough auto-disable, pass.
- UI locale checks, TypeScript checks, and production Vite build pass.
