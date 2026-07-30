<div align="center">

<img src="assets/pitlaunch-logo-v3.png" alt="PitLaunch" width="108" />

# PitLaunch

**One PC. A desk and a sim rig. One click between them.**

PitLaunch captures which monitors, sound devices, windows and apps belong to each way you
use your computer, then puts the whole machine into that state on demand.

[![version](https://img.shields.io/badge/version-0.9.4--beta-2EA88A?style=flat-square)](https://github.com/Cevzom/PitLaunch/releases)
[![platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square&logo=windows&logoColor=white)](#install)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)
[![updates](https://img.shields.io/badge/updates-delta%20~0.2%20MB-4C8DD9?style=flat-square)](#updates)
[![admin](https://img.shields.io/badge/admin%20rights-not%20required-2EA88A?style=flat-square)](#install)

<img src="docs/images/setups.png" alt="PitLaunch setups view" width="820" />

</div>

---

## The problem

A sim rig and a desk rarely want the same thing. The rig wants its own screen, the wheel's
audio, the game already running. The desk wants three monitors, headphones, and the windows
where you left them. Doing that by hand means Display Settings, Sound Settings, and dragging
windows around — every single time.

PitLaunch does it in one action, and it captures the state from the machine instead of asking
you to type in resolutions and device names.

## What it does

| | |
|---|---|
| 🖥️ **Displays** | Full topology through the Windows CCD API — which screens are on, resolution, refresh rate, position, and which one is primary. |
| 🔊 **Sound** | Playback, communications and microphone defaults, switched per setup and editable afterwards. |
| 🪟 **Windows** | Remembers where your windows were and puts them back, learning as you leave each setup. |
| 🚀 **Apps** | Starts what a setup needs and can close what it doesn't. |
| 🎮 **Game detection** | Pick a game; launching it switches to that setup automatically, and quitting switches back. |
| ⌨️ **Hotkeys** | A global shortcut per setup. Press the keys to record it, no syntax to memorise. |
| 🛟 **Safety** | Every layout is validated with Windows before it is applied, with rollback if it is refused — plus `Ctrl+Alt+Shift+F12` to turn every connected monitor back on. |

<div align="center">
<img src="docs/images/settings.png" alt="PitLaunch settings" width="760" />
</div>

## Install

Download **`PitLaunch-Setup.exe`** from the [latest release](https://github.com/Cevzom/PitLaunch/releases)
and run it. No administrator rights are needed; it installs for the current user and adds
Start Menu and Desktop shortcuts.

There is also a portable build if you prefer a copy that touches nothing else on the machine —
extract it anywhere and run `PitLaunch.exe`. The trade-off is updates: a portable copy has to
be downloaded again in full each time.

> PitLaunch is not code-signed yet, so Windows SmartScreen shows a warning the first time.
> Choose **More info → Run anyway**.

## Updates

Installed copies patch themselves. PitLaunch checks in the background, shows a quiet
**Update available** marker in the sidebar, and downloads **only the files that changed** —
typically around **0.2 MB instead of the 72 MB** a full download would cost.

Your setups live in `%APPDATA%\PitLaunch`, outside the install folder, so updating or
uninstalling never touches them.

## Command line

Drive PitLaunch from a Stream Deck, a wheel button, or a shortcut:

```cmd
PitLaunch.exe --profile "Sim Racing"    :: switch to a setup
PitLaunch.exe --capture "Desk"          :: capture the current state as a setup
PitLaunch.exe --chooser                 :: show the pick-a-setup screen
PitLaunch.exe --background              :: start quietly in the tray
PitLaunch.exe --restore-displays        :: emergency: enable every connected monitor
PitLaunch.exe --exit
```

Only one copy runs; later launches hand their command to the running one.

## Documentation

- **[User guide](docs/USER-GUIDE.md)** — full walkthrough, in English and Hebrew
- **[Releasing](RELEASING.md)** — cutting a release and publishing updates
- **[Product notes](PRODUCT.md)** · **[Design notes](DESIGN.md)**

## Status

Public beta. It has been used daily on a three-monitor desk / single-screen rig, and ships a
16-check self-test (`PitLaunch.exe --self-test`) covering display capture and restore
validation, audio fallback, profile recovery, and startup registration.

Bug reports are welcome — please include `%APPDATA%\PitLaunch\pitlaunch.log` and say which
monitor or audio device was involved.
