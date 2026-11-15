# Ruse 0.9.7

This release adds source freezing, diagnostics readouts, and a collection of workflow fixes.

## Highlights

- **Freeze effects for file and sample sources.** Render a source through its routed effect chain into a proxy file, bypass the original route, and keep playing from the frozen copy.
- **Frozen proxy cleanup.** Generated files are tracked, removed when their proxy node is deleted or replaced, protected while referenced by other nodes or timeline clips, and restored from the app-owned trash when undoing a removal.
- **More diagnostics.** With Diagnostics enabled, source nodes show estimated capture latency plus queue backlog, output nodes show estimated output latency, and the status bar shows the current window size.
- **Timeline onboarding.** The timeline animation now appears the first time the Timeline is opened instead of inside the welcome tour.
- **Compact workspace toolbar.** Routing, mixer, timeline, and workspace actions use less vertical space while preserving larger touch targets on touch devices.

## Fixes

- Fixed Native passthrough remaining stuck enabled when its route could no longer be validated. Disabling now clears stale native flags, restores normal routing, and reports restart errors.
- Freeze is unavailable for VST3, sidechain, or shared routes, and repeated clicks cannot start duplicate renders.
- Freeze renders preserve source gain without applying output or group gain twice.
- The timeline intro focuses its action and can be dismissed with Escape.
- Updated README for the current theme and freeze feature set.

## Validation

- 18/18 regression tests pass.
- Web self-tests, including freeze cleanup and Native passthrough toggling, pass.
- UI build, locale checks, typecheck, and 5/5 tests pass.
- Clean Debug and Release builds.

Package SHA-256: `e3bf51206c46cb5117e69b972d3a6e628347ac5c35ee11ea698100d48d401aa6`
