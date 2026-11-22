# Ruse 0.9.20

This release hardens storage, model downloads, offline rendering and updates,
and adds MIDI-driven VST3 instruments.

## Storage folders

- Folder migration now checks for conflicts before copying anything and aborts
  if an existing file would be overwritten.
- Copy failures abort the location change and remove files copied during the
  failed attempt instead of leaving a half-populated folder.
- Storage changes are blocked while downloads, recordings, timeline renders or
  source freezes are active, including downloads that have not reported progress
  yet.
- Storage targets may not overlap the app folder, a drive root, or another
  configured storage folder.
- UI and control-API mutations are paused while the storage transition runs.
- Missing custom folders fall back to the default locations at startup and show
  a warning in Settings.
- Choosing the default folder clears a stale custom path and refreshes the
  fallback warning.
- Added regression coverage for successful migration, conflict rollback, partial
  copy cleanup and overlapping targets.

## CPU cache and graph path

- Reused one small scratch set per compiled graph instead of allocating five
  block buffers for every modifier and two buffers for every output.
- Added a vectorized add path for source and output summing.
- Added a graph-render benchmark mode (`--bench --graph`) for before/after
  measurements. The shared-scratch pass reduced graph working-set allocations
  substantially; measured throughput change was within benchmark noise.

## Model downloads

- Model deletion now waits for an active download/install to release its gate
  instead of deleting files underneath it.
- Deletes cancel an active download where possible and fail with a clear busy
  message if the download does not stop in time.
- Downloads re-check cancellation immediately before replacing an installed
  model, so a cancelled install cannot overwrite or remove a ready model.
- Deferred Vosk/Kokoro deletion was removed so an old session cannot delete a
  freshly reinstalled model.
- TTS generation is stopped before its model is deleted, and a new install is
  blocked while deletion is pending.
- Applied to Whisper, GGUF translation, OPUS-MT, Kokoro/Piper and Vosk models.

## VST3 MIDI

- MIDI notes and control changes can be routed to individual VST3 nodes with a
  per-node MIDI input toggle.
- MIDI events travel with the audio block through shared memory in the isolated
  VST3 host, adding no extra round-trip or buffer.
- Node gain and mute can be learned directly from MIDI, with bindings saved in
  presets and sessions.
- MIDI clock is converted to tempo, and start/continue/stop drive both VST3
  transport state and Ruse routing. Tempo/transport changes use the existing
  block boundary.
- VST3 instruments are listed in the plugin catalog and added as no-input
  modifier nodes. They receive MIDI and render into the normal graph.
- Isolated-host MIDI events stay queued until an audio block is actually
  processed, so dropped render blocks cannot lose a note.
- Stale MIDI events expire after 250 ms; note-off and sustain-off events are
  still delivered so bypassed nodes cannot leave stuck notes.
- The MIDI queue holds 4096 events and reports overflow in the log.
- VST3 instruments are detected at runtime, so plugins misreported by the fast
  scanner still enable their MIDI input after loading.
- The isolated VST3 host no longer exits after a few seconds without audio.
- Isolated VST3 shutdown no longer blocks on a stuck control pipe.
- Added a `Ruse Test MIDI` pseudo-device that emits clock, notes, CC and
  transport messages for testing without hardware.
- MIDI-only VST3 plugins remain unsupported because the native host does not
  expose MIDI output events.
- VST3 sidechain buses remain blocked by the current native host wrapper: it
  exposes one flat stereo buffer and no auxiliary input-bus activation.

## Offline timeline isolation

- Timeline bounce and freeze continue to render from an immutable project
  snapshot, so edits made during preparation or rendering do not change the
  offline result.
- Overlapping freezes/bounces now use a shared reference-counted preparation
  gate, and control-API node changes are blocked while an offline graph is being
  cloned.
- Added a regression check that mutates the live project after taking an offline
  snapshot and verifies the snapshot is unchanged.

## Updates

- The update helper only accepts the restart path `target\Ruse.exe`, rejects
  unsafe install/staging roots and reparse points, and validates the staged
  package version against the release.
- Update extraction uses the safe ZIP extractor to reject path traversal.
- Published SHA-256 values must be 64 hex characters, and the release asset name
  must match the expected Windows x64 package for that version.
- Release lookup selects the exact expected package instead of the first
  Windows ZIP in the asset list.

## Reliability

- Support-bundle contents are now built on the UI thread and only ZIP writing
  runs on a worker, avoiding concurrent reads of the live graph.
- Support report and bundle export no longer read mutable graph state from a
  background thread.

## Validation

- Debug build: 0 warnings.
- UI tests: 52 passed.
- Locale checks: 1133 entries in each language.
- Regression suite: 19 passed, including the expanded `storage folder migration`
  checks and `timeline offline snapshot isolation`.
- Update helper negative test: rejected a restart path outside the install
  folder and left the target untouched.
- VST3 instrument smoke test: Dexed, Surge XT, TAL-NoiseMaker, OB-Xd and Odin2
  loaded and rendered a MIDI C4 note through the isolated host.
- Isolated idle test: Dexed still rendered after the host sat idle for 5 s.
