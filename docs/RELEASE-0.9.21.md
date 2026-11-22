# Ruse 0.9.21

This is a reliability hotfix for startup on machines without the Visual C++
runtime, settings that fail to save after a window-position issue, and false
clock-drift detection when a source pauses.

## Windows runtime

- Release packages now include the app-local Visual C++ runtime DLLs required by
  Whisper, llama.cpp and the VST3 host.
- Clean or older Windows installs no longer fail to load native inference
  libraries when the Microsoft redistributable is missing.
- The package includes the Microsoft VC++ redistributable notice.

## Settings

- Settings containing non-finite window coordinates now serialize and load
  correctly instead of failing every save.
- A failed window-position save no longer prevents later UI changes from being
  persisted.

## Support diagnostics

- The device report now lists render and capture endpoints independently.
- A missing default input or output no longer aborts the entire device report.
- Support reports now show the available devices even when Windows has no
  default endpoint configured.

## Clock drift

- Source clock-drift estimation now resets after a 250 ms packet gap and when a
  source starts or changes format.
- A paused source no longer produces a large false drift value that engages
  unnecessary resampling or contributes to starvation diagnostics.

## Validation

- Release build: 0 warnings, 0 errors.
- Regression suite: 19/19 passed.
- Hotfix package includes 10 app-local Visual C++ runtime files.
