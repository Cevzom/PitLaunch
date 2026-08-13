# PitLaunch website

The product page people land on to download PitLaunch. Static HTML, CSS and JavaScript — no
build step, no framework, no tracking.

```
web/
├── public/          everything served
│   ├── index.html
│   ├── styles.css
│   ├── main.js
│   ├── favicon.ico
│   └── img/
├── Caddyfile        server config (compression, cache, security headers)
├── Dockerfile       caddy:2-alpine
└── package.json     dev-server convenience only
```

## Run it locally

```
cd web
npm run dev
```

Then open <http://localhost:4321>. There is nothing to compile; edit a file and refresh.

To check exactly what Railway will serve, build the container instead:

```
npm run docker:build && npm run docker:run
```

## Deploy to Railway

The site deploys from the `web/` directory of this repo.

1. **New Project → Deploy from GitHub repo → `Cevzom/PitLaunch`**
2. In **Settings → Source**, set **Root Directory** to `web`
3. Railway detects the `Dockerfile` and builds it. No start command needed — Caddy reads
   `$PORT`, which Railway injects
4. **Settings → Networking → Generate Domain** for a `*.up.railway.app` URL

Or from the CLI, inside `web/`:

```
railway link
railway up
```

### Custom domain

**Settings → Networking → Custom Domain**, enter the domain, then add the **CNAME and TXT**
records Railway shows you at your registrar. Both are required — the CNAME routes traffic, the
TXT proves ownership. Railway issues the TLS certificate automatically.

## Content that goes stale

Three things are pinned to the GitHub release and will need attention if that changes:

- The download buttons point at
  `releases/latest/download/PitLaunch-win-Setup.exe` and `...-win-Portable.zip`. These follow
  `latest` automatically, so a new release needs no change here — but the **asset filenames must
  stay the same**, or the buttons 404.
- The version label fetches `releases/latest` from the GitHub API at runtime and degrades to the
  static text "PitLaunch 1.0" if that call fails. Nothing breaks offline.
- The comparison wording is defined in `docs/comparison.json`. Run
  `tools/verify-comparison.ps1`; CI also rejects a README/website mismatch.

## Notes

- The only third-party request is Google Fonts (Archivo + Cabin), matching the app's typefaces.
  Self-host them if you want the page fully independent — the TTFs are in `assets/fonts`, though
  they would need converting to woff2 first, since the TTFs are ~180 KB each.
- `prefers-reduced-motion` is honoured: reveals, the shine and the hero loop all stop.
- The hero animation is CSS transforms only, so it stays smooth on a phone.
