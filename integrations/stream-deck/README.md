# PitLaunch for Stream Deck

Switch your PC between desk and sim-racing setups from a physical button, and pull your monitors
back when a switch goes wrong.

Everything happens on this machine. The plugin talks to PitLaunch over a local Windows named pipe.
There is no account, no website, no telemetry, and nothing listening on a network port.

## Actions

| Action | What it does |
|---|---|
| **Switch setup** | Activates one saved PitLaunch setup. Shows **green** while that setup is the active one, and an error indicator if the switch fails. Pick the setup from a dropdown in the property inspector. |
| **Toggle Desk / Rig** | One-button switch to the opposite Desk or Sim Racing setup. It needs no button settings and shows the currently active setup name. |
| **Restore displays** | Emergency button. Turns every connected monitor back on. Takes no configuration, on purpose: it has to work when you cannot read the screen. |

If PitLaunch is not running when you press a button, the plugin starts it (minimised to the tray)
and retries the connection for a few seconds before giving up.

## Requirements

- Windows 10 or 11
- Stream Deck software **6.5** or newer
- [PitLaunch](https://github.com/Cevzom/PitLaunch) installed, with the integration pipe
  (`PitLaunch.Integration.v1`) available — see [Protocol](#protocol)
- Node.js 20+ **to build**. Not needed to run: Stream Deck ships its own Node runtime.

No administrator access is required at any point.

## Build

```
cd integrations/stream-deck
npm install
npm run build
```

`npm run build` produces two bundles: the plugin itself at
`com.cevzom.pitlaunch.sdPlugin/bin/plugin.js`, and a test-only bundle of the PitLaunch client.

## Test

```
npm test
```

Builds first, then runs the suite with Node's built-in test runner. The tests stand up a **real
Windows named pipe** speaking the real protocol and drive the actual client against it, so the
socket path, the newline framing, and request/response correlation are all genuinely exercised.
No Stream Deck hardware or software is involved.

After building the desktop app, the optional end-to-end smoke test launches the **real PitLaunch
executable** in an isolated data directory and verifies the production pipe server:

```
npm run test:e2e
```

Set `PITLAUNCH_E2E_EXE` if the executable is somewhere other than the normal Debug output folder.

## Package

```
npm run validate
npm run pack
```

`validate` runs Elgato's official checks against the `.sdPlugin` folder. `pack` writes
`dist/com.cevzom.pitlaunch.streamDeckPlugin`, which is the file you give someone to install.

## Install

**From the package:** double-click `com.cevzom.pitlaunch.streamDeckPlugin`. The Stream Deck app
installs it and the actions appear under a **PitLaunch** category.

**For development**, link the folder in place so a rebuild is picked up without reinstalling:

```
npm run link
npm run watch
```

## Protocol

The plugin is the client. **PitLaunch owns the server side.** The contract lives in
[`src/pitlaunch/protocol.ts`](src/pitlaunch/protocol.ts) and is summarised here.

- **Transport:** Windows named pipe, `\\.\pipe\PitLaunch.Integration.v1`, duplex
- **Framing:** one compact JSON object per line, UTF-8, no BOM, `\n` terminated
- **Correlation:** every request carries a unique `id`; the reply repeats it. Replies may arrive
  out of order, and the plugin sends concurrent requests, so they must be correlated by `id`

Request:

```json
{"protocol":"PitLaunch.Integration.v1","version":1,"id":"7f3a...","method":"profile.activate","params":{"profileId":"8f14e45f-..."}}
```

Success:

```json
{"protocol":"PitLaunch.Integration.v1","version":1,"id":"7f3a...","ok":true,"result":{"profileId":"8f14e45f-...","complete":true}}
```

Failure:

```json
{"protocol":"PitLaunch.Integration.v1","version":1,"id":"7f3a...","ok":false,"error":{"code":"PROFILE_NOT_FOUND","message":"No setup with that id."}}
```

### Methods

| Method | Params | Result |
|---|---|---|
| `profiles.list` | none | `{ profiles: [{ id, name, kind?, isActive? }] }` |
| `profile.activate` | `{ profileId }` | `{ profileId, complete?, message? }` |
| `profile.toggle` | none | `{ profileId, profileName?, complete?, message? }` |
| `status.get` | none | `{ activeProfileId: string \| null, appVersion?, busy? }` |
| `displays.restore` | none | `{ restored: boolean, message? }` |

### Error codes

`INVALID_REQUEST`, `UNSUPPORTED_VERSION`, `UNSUPPORTED_METHOD`, `PROFILE_NOT_FOUND`,
`SWITCH_FAILED`, `BUSY`, `INTERNAL_ERROR`.

### Notifications (optional)

If PitLaunch pushes these, buttons update immediately:

```json
{"protocol":"PitLaunch.Integration.v1","version":1,"event":"profiles.changed"}
```

`profiles.changed` and `status.changed` are supported. They are **optional** — the plugin also
polls `status.get` and `profiles.list` every 4 seconds and diffs the result, so a server that
never notifies still works, just with a short delay.

## Notes for whoever implements the PitLaunch side

- **Ids must be the permanent `Profile.Id` Guid**, never the name. The plugin stores the id in the
  button's settings; renaming a setup must not break it. Send the canonical lowercase hyphenated
  form; the plugin compares case-insensitively regardless.
- **The pipe must accept multiple concurrent connections.** A deck page can hold several PitLaunch
  buttons, and Stream Deck Mobile adds more. The existing single-instance pipe
  (`PitLaunch.ProfileManager.v2`) is one-way and capped at one server instance — do not model this
  one on it.
- **Keep the connection open.** The plugin connects once and reuses the socket; it does not
  reconnect per request.
- **Current-user-only pipe, no elevation.** Both sides run as the same user; the desktop server
  rejects connections from other signed-in Windows users and never opens a network port.
- **`activate` should not block on the confirmation dialog.** A Stream Deck press is a deliberate
  act, so the desktop server routes it through the same no-modal policy as a hotkey.
- **Report partial failures.** If a setup applies but a monitor or audio device was missing, return
  `ok: true` with `complete: false` and a message. The button then shows a warning rather than a
  tick, which is the honest signal.

## Gotchas

- **Never write `manifest.json` with PowerShell's `Set-Content -Encoding utf8`.** On Windows
  PowerShell 5.1 that writes a UTF-8 **BOM**, and `streamdeck pack` fails with
  `Failed to parse JSON file`. Confusingly, `streamdeck validate` passes anyway, so the manifest
  looks fine right up until you package it. Use `[System.IO.File]::WriteAllText` with
  `UTF8Encoding($false)`, or just edit it in a normal editor.
- **`FontSize` in the manifest is a number, not a string.** `"12"` fails validation; `12` passes.
- The SDK is **2.x**, which uses standard TC39 decorators. `experimentalDecorators` must stay
  **off** in `tsconfig.json` or the `@action` decorator will not type-check. SDK 1.x is deprecated
  and is not Marketplace-compatible.

## Known limits

- Only the Keypad controller is declared. Dials and touch strips are not implemented.
- The active indicator relies on `status.get`; with no notifications, it can lag by up to the
  4-second poll interval.
- Icons are generated by [`tools/generate-icons.ps1`](tools/generate-icons.ps1) and are functional
  rather than designed. Re-run it after changing any size or colour.
