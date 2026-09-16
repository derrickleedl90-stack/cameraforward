# Screen Zoom for Windows 11

A small native Windows desktop app that freezes the entire desktop, zooms around a fixed point, and prevents ordinary keyboard input and mouse clicks from reaching the applications underneath while active.

## Run

1. Copy or extract this entire folder onto a Windows 11 PC (x64, or ARM64 with x64 emulation).
2. Double-click **Run Screen Zoom.vbs**. It compiles `bin\ScreenZoom.exe` using the .NET Framework compiler on your PC and opens it. No administrator rights, Python, Node.js, or Visual Studio are needed.
3. Startup is invisible: no window, status bar, tray icon, or console. Put your pointer over the detail you want to enlarge, press **Ctrl + Alt + Z**, release the keys, and wait one second.
4. The desktop freezes and starts at 100% with no text or status overlay. Use the wheel or the plus/minus keys to change zoom.
5. Press **Esc** to unlock. The app stays silently available for another session. Press **Ctrl + Alt + Q** after unlocking to exit completely.

The `.vbs` launcher hides the build console; the `.cmd` launcher is available for troubleshooting if Windows Script Host is disabled. Only errors display a dialog. Exit the previous version before rebuilding.

After the first build, you can launch `bin\ScreenZoom.exe` directly. If the compiler is missing, enable/repair .NET Framework 4.8 in Windows. Extract the ZIP first; do not run the launcher from inside the ZIP viewer.

## Controls

| Control | Action |
| --- | --- |
| Ctrl + Alt + Z | Start capture from another app |
| Wheel up / down | Zoom in / out |
| + / - (including numeric keypad) | Zoom in / out |
| Esc | Unlock immediately |
| Ctrl + Alt + Q | Exit the app while unlocked |

Zoom ranges from 100% to 800% in 3 percentage-point increments: 100%, 103%, 106%, 109%, etc. The upper limit is clamped to 800%. Returning to 100% keeps the view locked; use Esc to unlock. Moving the pointer does not move the magnified image. To choose a new focus point, unlock and start again. The app waits for all keys and mouse buttons to be released before locking.

## Behavior and limits

- **This version freezes a screenshot.** Videos, clocks, and animations in the displayed image stop updating. The underlying programs continue running. This is a viewing/input lock, not a Windows session lock.
- The screenshot stays in memory and is discarded on unlock. Nothing is saved or sent over the network.
- All monitors are captured as one desktop and magnified around the initial pointer location. Mixed-DPI displays and negative monitor coordinates are accounted for, but need real-device verification.
- An opaque full-desktop window intercepts clicks. A temporary keyboard hook consumes ordinary keys while locked, allowing only zoom controls and Esc to affect this app.
- Windows secure screens and protected video are outside this app's control. Protected content may appear black. **Ctrl + Alt + Delete remains a Windows escape route.** This is not kiosk or security software; other topmost/elevated windows and system gestures may interrupt it.
- It unlocks if another window takes focus or the display layout changes. Hooks are removed on exit; Windows also removes them when the process terminates.
- The app does not register itself to start with Windows and makes no system settings changes. Close Windows Magnifier before use to avoid stacking zoom effects.

## Build and verification

Run `build.cmd` on Windows to compile. The source uses C# 5 and Windows Forms from .NET Framework so a separate SDK is not required.

The source and zoom geometry were checked on macOS. **The executable has not been compiled or run on Windows in this environment.** Complete these checks on Windows before relying on it:

1. Build and launch; verify no window or console appears. Activate with Ctrl + Alt + Z over Notepad and verify there is no status overlay.
2. Zoom through 100%–800%, test wheel and both sets of plus/minus keys, and confirm movement never pans the image.
3. While locked, type letters, click, right-click, press Tab, Alt + Tab, and Windows. Confirm ordinary input does not affect Notepad. Esc must restore control.
4. Confirm 100% stays locked and repeated unlock/restart cycles work.
5. Test two monitors, including one left of the primary, and Windows scaling at 100%, 150%, and 200%.
6. Disconnect a monitor and switch to the Windows security screen during zoom; verify the app releases the lock safely.
7. Unlock and exit using Ctrl + Alt + Q, or end the process, and confirm normal keyboard/mouse input is restored.

Implementation references: [Microsoft keyboard hook documentation](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) and [DPI awareness contexts](https://learn.microsoft.com/en-us/windows/win32/hidpi/dpi-awareness-context).
