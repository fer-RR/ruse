# Ruse 0.9.18

This release focuses on reliability, custom storage locations and interface
polish.

## Reliability

- Bounded VST3 control-pipe waits and moved parameter writes off the UI thread.
- Added automatic VST3 bypass when an isolated or guarded in-process plugin
  stops responding.
- Made control API shutdown asynchronous and non-blocking.
- Moved graph/tap disposal, routing start/stop, device/session scans, support
  bundle creation and timeline bounce/freeze preparation off the UI thread.
- Moved speech-session teardown waits off the UI thread.

## Storage and settings

- Added a **Change folder** button next to the Data, Models and User folders in
  Settings > Info > System.
- Folder changes use a confirmation dialog and can copy existing files to the
  new location. Old files are left in place.
- Storage locations persist in `%APPDATA%\Ruse\storage.json` and survive
  restarts.
- Added a theme selector to the welcome screen.

## Fixes

- File-player analysis results now remain visible when the node is moved.
- Folder/path buttons keep the existing compact icon style and wrap correctly.
- Removed the Receiver theme remnants and cleaned up theme/canvas rendering.
- Fixed the RUSE logotype typeface and removed the circular dot.

## Validation

- Debug build: 0 warnings.
- UI tests: 52 passed.
- Locale checks: 1133 entries in each language.
- Regression suite: 19 passed.
