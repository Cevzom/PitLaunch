<div align="center">

<img src="assets/pitlaunch-mark.png" alt="PitLaunch" width="88" />

# PitLaunch

### One PC. Desk and sim rig. One click between them.

<br>

[![Download](https://img.shields.io/badge/Download_for_Windows-20B8F0?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/Cevzom/PitLaunch/releases/latest/download/PitLaunch-win-Setup.exe)

<sub>Windows 10 / 11 &nbsp;·&nbsp; no admin needed &nbsp;·&nbsp; updates itself &nbsp;·&nbsp; MIT</sub>

<br><br>

<img src="docs/images/setups.png" alt="PitLaunch setups" width="860" />

</div>

<br>

## What it does

Your rig and your desk want different screens, different sound, different windows.
PitLaunch remembers both and moves the whole machine between them.

|  |  |
|---|---|
| **Screens** | Which monitors, what resolution, refresh rate and layout |
| **Sound** | Speakers, headset and mic, per setup |
| **Windows** | Puts your windows back where they were |
| **Apps** | Starts what you need, closes what you don't |
| **Games** | Finds your installed games, launch one and it switches |
| **Hardware** | Tells you if the wheel or pedals aren't plugged in |
| **Stay awake** | No screen blanking mid-stint |
| **Hotkeys** | A shortcut per setup |

<br>

## A setup, in full

Every setup is captured from what the machine is actually doing — not typed in by hand.
Open one and you see exactly what it will restore.

<div align="center">
<img src="docs/images/profile.png" alt="A saved setup" width="860" />
</div>

<br>

## Getting started

Run the installer. It takes a few seconds, then opens itself. No wizard.

Click **Create setup**, pick your screens and sound, and save it. Do that once for the desk
and once for the rig. Then switch from the app, the tray, a hotkey, or automatically when a
game starts.

> **SmartScreen will warn you** the first time, because PitLaunch isn't code-signed yet.
> Choose *More info → Run anyway*. Every release lists a SHA256 so you can verify the file.

<br>

## Updates

Installed copies patch themselves. An **Update available** marker appears in the sidebar, and
installing downloads only the files that changed — usually **well under 1 MB** instead of the
full 76 MB.

Already running an older version? Nothing to re-download by hand:

**Settings → Updates → Check for updates → Install and restart.**

Your setups live in `%APPDATA%\PitLaunch`, outside the install folder, so updating never
touches them.

<div align="center">
<img src="docs/images/settings.png" alt="Settings" width="860" />
</div>

<br>

## Why not something else

|  | PitLaunch | DisplayMagician | Sim launchers |
|---|:---:|:---:|:---:|
| Screens | yes | yes | no |
| Sound | yes | yes | no |
| Window positions | yes | no | no |
| Launch apps | yes | partial | yes |
| Checks with Windows first | yes | no | no |
| Rolls back if it fails | yes | no | no |

Others do part of it. PitLaunch treats a setup as one thing, and asks Windows to validate a
display layout *before* applying it, so a bad switch can't leave you on a black screen.

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

**[User guide](docs/USER-GUIDE.md)** &nbsp;·&nbsp; **[Releasing](RELEASING.md)** &nbsp;·&nbsp; **[Design notes](DESIGN.md)**

Public beta, used daily on a three-monitor desk and a single-screen rig.
Found a bug? Send `%APPDATA%\PitLaunch\pitlaunch.log` and what you were doing.

<br>

<div align="center">
<sub>MIT licensed &nbsp;·&nbsp; UI set in <a href="https://fonts.google.com/specimen/Cabin">Cabin</a> and <a href="https://fonts.google.com/specimen/Space+Grotesk">Space Grotesk</a> (OFL)</sub>
</div>
