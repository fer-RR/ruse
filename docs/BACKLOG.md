# Backlog — ideas that need a decision, a licence or a bigger machine

Written during the overnight work session. Nothing here is started; it is the
list of things that are worth doing but that need the owner to choose.

## Plugins (VST3 / CLAP)

NAudio 3.3 dropped VST support entirely (the packages are now only
`NAudio.Core`, `NAudio.Wasapi`, `NAudio.WinMM`, `NAudio.Midi` and
`NAudio.Asio`), so hosting a VST3 plugin needs a **native C++ component**:

- the VST3 SDK from Steinberg (free, but the licence has to be accepted, and
  the SDK may not be redistributed — a product built on it may need the signed
  agreement),
- a small host process that loads the plugin, negotiates buses and exchanges
  audio with Ruse over a pipe,
- plugin-window embedding (`IPlugView` → an HWND parented to a WPF host window).

Toolchain check: Visual Studio Build Tools 2026 are installed (so MSVC is
available), but there is no cmake and no VST3 SDK on the machine.

**Interim, already done:** 28 built-in effects, including a biquad EQ, a
pitch shifter, a transient shaper and comfort noise, which covers most of what
people reach for a plugin for.

## Replacing the layout transform — done

The canvas world used a `LayoutTransform`, which forced WPF to re-layout and
re-render the whole world layer whenever anything inside changed (meter bars,
status text). It now uses a `RenderTransform` on `GraphLayer`, with the world
and host sizes kept separately in `UpdateGraphExtent`. No further work needed.

## A custom window frame — done

The standard title bar is replaced with a brushed-panel chrome modelled on the
90s hi-fi separates Posy films: a VFD-style "RUSE / AR-01" plate, a seven-bar
level display and square window keys. It uses `WindowChrome`, so Aero snap,
resize borders and taskbar behaviour still work; a `WM_GETMINMAXINFO` hook
keeps a maximised window inside the monitor work area. Covered by `--uitest`
and by screenshots at normal and maximised size.

## Other things worth doing

- **Per-node output device switching** — change a node's device without
  deleting and re-adding it.
- **VST-style sidechain / MIDI-trigger** on the effects.
- **A real mixer view** beside the node graph (channel strips with faders,
  pan and mute per output).
- **The WPF render tier is still 0** on this machine: the graphics stack keeps
  WPF in software mode (D3D9, D3D11 and DXGI all work; the driver is clean;
  the October 2026 cumulative update and the .NET 10.0.11/12/13 runtimes were
  all ruled out). The remaining suspects are the NVIDIA driver rollback and the
  cumulative update itself.
