<div align="center">

<img src="assets/pitlaunch-logo-v3.png" alt="PitLaunch" width="96" />

# PitLaunch

### One PC. Desk and sim rig. One click between them.

<br>

[![Download](https://img.shields.io/badge/⬇_Download_for_Windows-2EA88A?style=for-the-badge&logoColor=white)](https://github.com/Cevzom/PitLaunch/releases/latest/download/PitLaunch-win-Setup.exe)

<sub>Windows 10 / 11 · no admin needed · updates itself</sub>

<br><br>

<img src="docs/images/setups.png" alt="PitLaunch" width="820" />

</div>

<br>

## What it does

Your rig and your desk want different screens, different sound, different windows.
PitLaunch remembers both and moves the whole machine between them.

|  |  |
|---|---|
| 🖥️ **Screens** | Which monitors, what resolution, refresh rate and layout |
| 🔊 **Sound** | Speakers, headset and mic, per setup |
| 🪟 **Windows** | Puts your windows back where they were |
| 🚀 **Apps** | Starts what you need, closes what you don't |
| 🎮 **Games** | Picks your installed games automatically — launch one, it switches |
| 🛞 **Hardware** | Tells you if the wheel or pedals aren't plugged in |
| ☕ **Stay awake** | No screen blanking mid-stint |
| ⌨️ **Hotkeys** | A shortcut per setup |

<br>

<div align="center">
<img src="docs/images/settings.png" alt="Settings" width="740" />
</div>

<br>

## Getting started

Run the installer — it takes a few seconds, then opens itself. No wizard.

Click **Create setup**, pick your screens and sound, and save it. Do that once for the desk
and once for the rig. Then switch from the app, the tray, a hotkey, or automatically when a
game starts.

> **SmartScreen will warn you** the first time — PitLaunch isn't code-signed yet.
> Choose *More info → Run anyway*. Every release lists a SHA256 so you can verify the file.

<br>

## Why not something else

|  | PitLaunch | DisplayMagician | Sim launchers |
|---|:---:|:---:|:---:|
| Screens | ✅ | ✅ | — |
| Sound | ✅ | ✅ | — |
| Window positions | ✅ | — | — |
| Launch apps | ✅ | partial | ✅ |
| Checks with Windows first | ✅ | — | — |
| Rolls back if it fails | ✅ | — | — |

Others do part of it. PitLaunch treats a setup as one thing — and asks Windows to validate
a display layout *before* applying it, so a bad switch can't leave you on a black screen.

<br>

## Updates

Installed copies patch themselves. An **Update available** marker appears in the sidebar and
downloads only what changed — usually **under 1 MB** instead of 76 MB.

Your setups live in `%APPDATA%\PitLaunch`, so updating never touches them.

<br>

## Command line

For a Stream Deck, a wheel button, or a shortcut:

```
PitLaunch.exe --profile "Sim Racing"
PitLaunch.exe --chooser
PitLaunch.exe --restore-displays
```

<br>

## More

**[User guide](docs/USER-GUIDE.md)** · **[Releasing](RELEASING.md)** · **[Design notes](DESIGN.md)**

Public beta, used daily on a three-monitor desk and a single-screen rig.
Found a bug? Send `%APPDATA%\PitLaunch\pitlaunch.log` and what you were doing.

<br>

<div align="center">
<sub>MIT licensed</sub>
</div>
