# AirBubble Lid Watcher

Keeps AirBubble working when the laptop lid is closed or opened.

## Why
When the lid closes, Windows turns off the built-in screen and reshuffles the displays.
AirBubble (v1.7.0, and PC Viewer before it) doesn't handle that: it loses a virtual screen's capture
("no DXGI output for \\.\DISPLAY6") and never re-arranges the AR desktop, leaving only the
raw side-by-side 3D desktop in the glasses (taskbar in one eye, tray icons in the other).

Windows itself is set up correctly (lid close action = Do nothing, no sleep timeouts),
so the laptop stays awake. The problem is only in AirBubble.

## What it does
`lid-watcher.ps1` checks every 2 seconds whether the built-in screen (`LGD0764`) is active.
When that changes and the glasses (`MRG3132`) are plugged in, it:

1. Quits AirBubble cleanly with `AirBubble.exe --quit` (force-ends it after 10 s if needed;
   the `--guard` helper is left alone).
2. Waits 4 seconds for the helper to settle the desktop.
3. Starts AirBubble again, which restores the saved screens, 3D mode and Bubble.

It runs on both lid close and lid open. Recovery takes about 8 seconds plus a
2-second hold-still calibration. Tested 2026-11-14: lid close recovered Main screen + Screen 2 in 3D.

It finds `%LOCALAPPDATA%\Programs\AirBubble\AirBubble.exe`, or the pre-1.7 `XREAL PC Viewer\XrealPcViewer.exe` if AirBubble isn't installed.

## Install
A shortcut in the Startup folder (`shell:startup`, "AirBubble Lid Watcher") runs:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "<path to repo>\LidWatcher\lid-watcher.ps1"
```

Only one copy runs at a time (named mutex `Local\XrealLidWatcher`).

## Log
`%APPDATA%\AirBubble\lid-watcher.log`

## Turn off
Delete the "AirBubble Lid Watcher" shortcut from `shell:startup` and end the hidden
`powershell.exe` running `lid-watcher.ps1`.

## Proper fix (TODO)
This is a workaround. The real fix belongs in the viewer: handle `WM_DISPLAYCHANGE` /
topology changes in `ViewerApp.cs` / `DisplayConfig.cs` by re-running the AR desktop
arrangement and re-opening Desktop Duplication for each screen. Then this watcher can go.
