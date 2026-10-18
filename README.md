# One Click Switcher

**English** | [Русский](README.ru.md)

A simple utility for Windows 11: toggles and actions in one click from the taskbar or the desktop.
No tray icon and no background processes: the program starts on click, does its job and exits.

## What it can do

- Bluetooth and Wi-Fi: turn on or off
- Microphone (all microphones at once) and sound (the default output device): mute or unmute
- Lock the computer and sleep
- Shut down and restart with a 5-second countdown, a sound and a cancel option (button, Esc, click on the window or click the icon again)
- Status: a small panel showing the state of all toggles for 5 seconds; clicking the shortcut again closes it
- Interface in Russian and English, light and dark theme (follows your Windows settings)

## How it works

It is a single `OneClickSwitcher.exe`, with one shortcut per function. Each shortcut starts the same program with a parameter, for example `--toggle bluetooth`, `--action lock` or `--status`. Pin the shortcuts you need to the taskbar or put them on the desktop.

Tip: for the best look, align the taskbar to the left (Settings, Personalization, Taskbar, Taskbar behaviors).

Double-click protection: repeated launches of the same function within about half a second are ignored.

## Install

- **Microsoft Store:** coming soon.
- **From source:** see "Build" below.

## Privacy

The app collects no data, has no analytics and does not use the network.
Full policy: <https://oneclickswitcher.github.io/OneClickSwitcher/>

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Self-contained single-file build for Windows x64:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

A Microsoft Store package (MSIX) is built with `store/build-msix.ps1`.

## Known limitations

- A Bluetooth headset that connects after the click keeps its microphone on.
- On some HDMI monitors the countdown sounds may be inaudible, especially the first note.

## Support

Questions and problem reports: OneClickSwitcher@gmail.com, or open an Issue in this repository.

## Third-party components

Uses the [NAudio](https://github.com/naudio/NAudio) library (MIT license). The project's own license has not been chosen yet.
