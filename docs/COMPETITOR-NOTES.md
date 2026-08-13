# DisplayMagician and SimLauncher notes

Research checked 13 August 2026 against each project's own website and GitHub repository. These
notes are for honest comparison copy and launch-thread replies, not attack copy.

The short launch comparison shared by the README and website lives in
[`comparison.json`](comparison.json). Run `tools/verify-comparison.ps1` before release; it fails if
either surface changes the wording. PitLaunch behavior was verified locally. Competitor strengths
are summaries of official documentation and are deliberately not presented as local hardware tests.

## Short positioning

- **DisplayMagician** is the display specialist. Its strongest differentiator is deep display-mode
  coverage: NVIDIA Surround, AMD Eyefinity, cloned/extended layouts, USB and wireless displays,
  DPI scaling, refresh rates, and HDR. A Game Shortcut can select a display profile and audio
  device, pre-start helper programs, launch a game, and optionally restore the previous display
  profile afterwards.
- **SimLauncher** is the app-stack specialist. Its strongest differentiator is a polished catalog
  of 27 racing/flight sims and known companion apps, with per-game profiles, drag-to-reorder launch
  sequencing, delays, custom app slots, and one-click close/relaunch controls.
- **PitLaunch** should be positioned as the whole-PC setup switcher. It captures Desk and Sim
  Racing states, can react when a game was launched normally outside PitLaunch, and moves Windows
  display topology, default and per-app audio, windows, apps, power/HDR, controller readiness,
  Discord behavior, and control surfaces together. Its safety story is preflight validation,
  emergency display recovery, and restart-safe Undo.

## Source-backed comparison

| Capability | PitLaunch | DisplayMagician | SimLauncher |
|---|---|---|---|
| Saved Windows display layouts | Yes | Yes; its core specialty | Not advertised |
| NVIDIA Surround / AMD Eyefinity management | No dedicated vendor API; restores Windows-visible topology | Yes | Not advertised |
| Setup-level playback and microphone selection | Yes | Yes | Not advertised |
| Restore normal state after a game | Displays, audio, apps and game overrides | Optional previous display-profile rollback | Closes tracked companion apps; no display-state rollback advertised |
| Window-position capture and restore | Yes | Not advertised | Not advertised |
| Per-game companion applications | Yes | Pre-start programs | Yes; core specialty |
| Ordered launch and configurable delays | Yes | Not clearly advertised | Yes; core specialty |
| Detect a game launched normally outside the tool | Yes | Not advertised; documented flow starts the game through a Game Shortcut | Not advertised; documented flow starts the sim stack from a profile |
| Per-app output routing and volume | Yes, best effort where Windows supports routing | Shortcut-level audio device | Not advertised |
| Discord output, microphone, volume and game overrides | Yes | Not advertised | Not advertised |
| Controller presence/readiness check | Yes | Not advertised | Not advertised |
| Native Stream Deck actions | Yes | Not advertised | Not advertised |
| Open source / local configuration | MIT / local | GPL / local | GPL v3 / local, no telemetry |

“Not advertised” means the capability was not found in the official sources reviewed; it should
not be rewritten as “impossible” or “broken.”

## Launch-thread replies

### “Isn't this just DisplayMagician?”

> DisplayMagician is the closest comparison and is much deeper if your main need is vendor-specific
> display modes such as NVIDIA Surround or AMD Eyefinity. PitLaunch is aimed at the whole Desk ↔ Rig
> transition: Windows-visible displays, sound and microphone, window positions, app sequencing,
> power/HDR, controller checks, Discord, automatic game detection, Stream Deck, and Undo in one
> setup. If DisplayMagician already solves your rig perfectly, there is no reason to pretend it
> does not—it is a mature open-source option.

### “How is this different from SimLauncher?”

> SimLauncher is stronger as a dedicated sim/app launcher: it has a large built-in sim catalog and
> a polished ordered companion-app workflow. PitLaunch does not require you to launch the game from
> PitLaunch. It can notice a game started through Steam or another launcher, switch the PC's display,
> audio and session hardware state, layer on game-specific apps/Discord, and return to the normal
> Desk state when the game closes.

### “Why not use both?”

> You can. For example, DisplayMagician may remain the better choice for a vendor-specific Surround
> layout, while SimLauncher may remain the preferred front end for a large game library. PitLaunch's
> value is reducing the number of separate steps when the desired setup is representable through
> Windows display topology. Test with your actual GPU, dock and monitor combination before replacing
> a workflow that is already reliable.

## Sources

- DisplayMagician official site and feature list:
  https://displaymagician.littlebitbig.com/
- DisplayMagician official repository:
  https://github.com/terrymacdonald/DisplayMagician
- SimLauncher official site and feature list:
  https://simlauncher.com/
- SimLauncher official repository:
  https://github.com/Stashpeak/SimLauncher
