# Releasing PitLaunch

Two distributions are built from the same source:

| | Portable ZIP (`build.ps1`) | Installer (`build-installer.ps1`) |
|---|---|---|
| Output | `outputs\PitLaunch-Beta-<version>.zip` | `outputs\releases\` |
| Given to a user | one 161 MB exe they extract | `PitLaunch-win-Setup.exe` |
| Updates | download the whole thing again | **downloads only what changed (~0.2 MB)** |
| Shortcuts / uninstall | none, they manage the folder | Start Menu + Desktop, listed in Add or Remove Programs |

Use the installer for anyone you expect to send updates to. Keep the ZIP for people who
want a copy that touches nothing on their machine.

## Where updates come from

Already configured in `app\AppInfo.cs`:

```csharp
public const string UpdateFeedUrl = "https://github.com/Cevzom/PitLaunch";
```

Installed copies read the repository's **Releases**, so publishing an update means creating a
GitHub Release and attaching the files from `outputs\releases\`. Other feeds work too if you
ever move: any HTTPS folder serving the files, or a plain folder / network share path.

`PITLAUNCH_UPDATE_URL` overrides it at runtime, which is how the update flow gets tested
against a local folder without publishing anything.

### Publishing a GitHub Release

Attach **every file** from `outputs\releases\` to a release tagged with the version, e.g.
`0.9.4-beta.1`. Velopack reads `RELEASES` / `releases.win.json` from the assets to work out
what to download.

With the GitHub CLI:

```powershell
gh release create 0.9.4-beta.1 (Get-ChildItem outputs\releases\*).FullName --title "PitLaunch 0.9.4 beta" --prerelease
```

Keep previous releases published — a delta patches against the version before it.

## Cutting a release

1. Bump the version in **two** places, which must agree:
   - `PitLaunch.csproj` → `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>`
   - `app\AppInfo.cs` → `Version`
2. Build:
   ```powershell
   .\build-installer.ps1
   ```
3. Upload **the whole contents** of `outputs\releases\` to the feed.

Keep the older `-full.nupkg` files in place. A delta is a patch against the previous
version, so removing the old package removes the ability to patch from it, and the
`RELEASES` index lists every version.

The script prints the delta size next to the full size, e.g.

```
Delta update is 0.2 MB vs 72.2 MB full (0.3% of a fresh download).
```

If it says "First release - no delta yet", there was no previous version in the folder to
patch from — expected the first time, a warning sign after that (usually an emptied
output folder).

## What a user experiences

- **Installing:** run `PitLaunch-win-Setup.exe`. No admin prompt; it installs to
  `%LocalAppData%\PitLaunch` and adds Start Menu and Desktop shortcuts.
- **Updating:** Settings → Updates → **Check for updates** → **Install and restart**.
  PitLaunch downloads only the changed files and restarts into the new version.
- **Uninstalling:** Add or Remove Programs, like any other app.

Profiles live in `%APPDATA%\PitLaunch\profiles.json`, outside the install folder, so
updating and uninstalling never touch a user's setups.

## Seeing how many people run it

```powershell
.\tools\release-stats.ps1
```

Reads the public download counts on your releases. The useful one is **update downloads**:
only an installed copy that is actually running fetches a delta, so the delta count of your
newest release is a close read on how many live installs there were when it shipped. Fresh
installs appear as Setup.exe downloads, portable users as zip downloads.

It measures copies in the wild, not how often anyone opens the app, and it counts mirrors and
bots too. Nothing is sent from anyone's machine to produce these numbers.

## Verified

Most recently for **0.9.8-beta.1**, against the real public feed rather than a local folder:
an existing **0.9.7** install found the release on its own within seconds, showed the sidebar
**Update available** marker, and patched with a **550 KB delta instead of the 77 MB installer**
(5 of 468 files changed) before restarting into 0.9.8.

Earlier, for 0.9.2: built 0.9.1, installed it, built 0.9.2, and updated through the app's own
UI — a **234 KB delta**, then restarted running 0.9.2. Uninstall removed the shortcuts and
registry entries.

### Two things to get right when packing

- **Commit before you pack.** A self-contained publish embeds the current commit in
  `InformationalVersion`; packing first means the shipped build reports the *previous* commit.
- **Check new assets are not ignored.** `.gitignore` is a whitelist, so a new folder under
  `assets\` is excluded by default and `git status` stays silent about it. Confirm with
  `git check-ignore -v <path>` — the embedded fonts were nearly shipped missing this way, which
  would have made the app fall back to a system font on every other machine.

## Notes

- Builds are **not** byte-reproducible: a self-contained publish embeds a timestamp, so
  the same source rebuilt gives a different SHA256. Always hash the file you are actually
  shipping rather than quoting an earlier number.
- Nothing is code-signed, so SmartScreen warns on first run. Signing the Setup.exe with a
  certificate is the only thing that removes that warning; `vpk pack` accepts signing
  parameters when you have one.
