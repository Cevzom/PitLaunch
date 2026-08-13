# PitLaunch final feature inventory

## Setup and navigation

- Interactive first-run guide that uses the real setup controls, with Back and Skip.
- Unlimited locally stored setups, classified as Desk or Sim Racing.
- Sim-rig variants for single, dual, triple, quad, ultrawide, and VR layouts.
- Task-based sidebar: Setups, Games, Integrations, and Settings.
- Independent save boundaries so editing games, Discord, or apps cannot overwrite unrelated data.

## Displays and windows

- Capture enabled monitors, primary display, position, resolution, refresh rate, rotation, and scaling.
- Visual monitor selection and custom arrangement during setup.
- Validate display plans before saving and again before applying.
- Retry a valid layout with default refresh rates if Windows rejects the exact saved rates.
- Roll back to the previous display topology when an apply fails.
- Learn outgoing window positions and restore normal top-level windows on the next setup.
- Restore every connected display from the app, tray, command line, Stream Deck, or the emergency
  `Ctrl+Alt+Shift+F12` shortcut.

## Audio and Discord

- Setup-specific default playback, communications, and microphone devices.
- Per-application output-device preference and live session volume.
- Setup-level Discord launch, output, Windows communications microphone, and volume.
- Optional Discord mute/deafen keybind actions on game entry and exit.
- Per-game Discord overrides with automatic restoration of normal setup behavior.
- Missing endpoints are warnings rather than switch-stopping crashes.

## Applications and games

- Start apps on setup activation and optionally close them on deactivation.
- Arguments, hidden startup, graceful close, and optional forced close.
- Ordered launch, post-launch delay, readiness wait, and readiness timeout.
- Installed-game discovery for Steam, Epic, Xbox, EA app, Ubisoft Connect, Battle.net, and iRacing.
- Manual process-name entry and running-process suggestions.
- Automatic setup activation when a saved game process appears.
- Configurable detection interval and exit grace period for launcher hand-offs and short restarts.
- Child-process tracking so launcher behavior is less likely to return to Desk too early.
- Game-specific output, volume, extra apps, close-on-exit behavior, and Discord overrides.
- Normal setup audio/apps/Discord are restored when the game session ends.

## Rig session and hardware

- Per-setup Windows power plan.
- Per-setup HDR preference on supported displays.
- Per-setup sleep prevention that keeps both the system and display awake during long stints and
  releases the request when leaving the setup or exiting PitLaunch.
- Expected wheel, pedal, controller, and button-box list.
- Readiness report for displays, audio, apps, controllers, power plan, and HDR before switching.

## Controls and integrations

- Switch from the main app, system tray, setup hotkey, global Desk ↔ Rig hotkey, or command line.
- Manual switches respect Confirm before switching; game detection, hotkey, CLI, and Stream Deck
  activations never raise an unreachable confirmation dialog.
- Native Stream Deck actions for activating a setup, toggling Desk/Rig, status, and display recovery.
- One-click Stream Deck plugin installation from the Integrations page.
- Stream Deck Mobile uses the same plugin and actions without separate PitLaunch pairing.
- Private current-user Windows pipe; no local web server, open port, account, or API key.

## Safety, recovery, and lifecycle

- Restart-safe Undo restores the pre-switch display, audio, HDR, power plan, and keep-awake state.
- Missing hardware or applications are skipped and reported instead of crashing the whole switch.
- Optional confirmation for manual setup switches, enabled by default.
- Global minimum-version policy can require unsafe old builds to update before any activation path.
- Small in-place updates for installed copies, with portable-copy fallback to the installer page.
- Start with Windows, optional startup chooser, silent tray startup, and repairable delayed startup task.
- Single-instance command forwarding for profile, toggle, undo, chooser, capture, and display recovery.

## Data, diagnostics, and privacy

- Local JSON setup storage outside the installation directory, plus automatic backup recovery.
- Rotating local activity log.
- Privacy-sanitized support-bundle export that excludes raw setup names, paths, arguments, device
  identifiers, controller names, window titles, game processes, tokens, and email addresses.
- No PitLaunch account is required, and Discord integration never requests an account token.
- Self-contained 64-bit Windows 10/11 build with no separate .NET installation required.

## Deliberately not automated

- **Windows Focus Assist / Do not disturb:** Microsoft documents APIs for detecting Focus sessions,
  but not a supported desktop API for starting/stopping Do not disturb while preserving the user's
  previous state across Windows 10 and 11. PitLaunch does not write undocumented Quiet Hours registry
  values. Sleep prevention is implemented through the supported `SetThreadExecutionState` API.
