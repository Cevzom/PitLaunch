# Releasing PitLaunch

PitLaunch ships an installed, self-updating build and a portable ZIP. The installed build uses
Velopack delta packages from GitHub Releases; the portable build never auto-updates.

| Distribution | Public asset | Behavior |
|---|---|---|
| Installer | `PitLaunch-win-Setup.exe` | Installs without admin rights, adds shortcuts, and auto-updates |
| Portable | `PitLaunch-win-Portable.zip` | Extract and run; no install and no automatic updates |
| Stream Deck | `com.cevzom.pitlaunch.streamDeckPlugin` | Opens in Elgato Stream Deck and talks only to the local PitLaunch process |

## One-command preparation

From the repository root:

```powershell
.\tools\prepare-release.ps1
```

This operation:

1. checks that the project, app, and manifest versions match;
2. runs the Release build, self-test, and comparison-copy check;
3. creates the portable ZIP and Velopack installer/full/delta feed;
4. tests, validates, and packs the Stream Deck plugin;
5. copies the portable and plugin packages into `D:\outputs\releases`;
6. creates `SHA256SUMS.txt` and `artifacts\release-notes-<version>.md` with a SHA256 table.

The release directory deliberately keeps previous full and delta packages. Do not empty it: the
latest `releases.win.json` can refer to earlier packages and a delta needs its previous full package.

## Publish 1.0

Authenticate the GitHub CLI once with `gh auth login`, then:

```powershell
gh release create v1.0.0 (Get-ChildItem D:\outputs\releases\*).FullName `
  --title "PitLaunch 1.0" `
  --notes-file .\artifacts\release-notes-1.0.0.md `
  --latest

gh repo edit Cevzom/PitLaunch `
  --add-topic sim-racing `
  --add-topic wpf `
  --add-topic windows `
  --add-topic multi-monitor `
  --add-topic assetto-corsa `
  --add-topic dotnet `
  --add-topic stream-deck
```

Upload every file in the release directory. GitHub allows the same asset names on different
releases; the stable `releases/latest/download/...` README links therefore continue to work.

After publishing, test the installer link in a signed-out browser:

```text
https://github.com/Cevzom/PitLaunch/releases/latest/download/PitLaunch-win-Setup.exe
```

The response must resolve to the 1.0 asset. Also open the release page and compare the displayed
SHA256 table with the downloadable `SHA256SUMS.txt`.

## Version checklist

Keep these values aligned:

- `PitLaunch.csproj`: `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`, `Product`
- `app/AppInfo.cs`: `Version` and `ProductName`
- `app/app.manifest`: four-part assembly identity
- Stream Deck `package.json` and plugin `manifest.json`

Commit the exact source being packaged. Builds are not byte-reproducible, so always publish hashes
from the generated assets rather than copying values from an earlier build.

## Minimum-version policy

`docs/update-policy.json` can block a build that is too old to switch safely. It must only be raised
after the matching installer and update packages are public and verified. Missing, invalid, or
oversized policy documents fail open. Builds older than the first policy-aware version cannot be
retroactively forced; they must first take a normal update.

## Download statistics

```powershell
.\tools\release-stats.ps1
```

The per-release table shows the GitHub API total for installer downloads and the delta-download
live-install estimate side by side. These are public asset counts, not telemetry; they include
manual downloads, mirrors, bots, and the maintainer.

## SmartScreen

PitLaunch is not code-signed, so SmartScreen warns on first launch. SHA256 proves file integrity but
does not replace a signing certificate. Velopack supports signing parameters when a certificate is
available.
