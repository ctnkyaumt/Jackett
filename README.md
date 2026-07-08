# Jackett (nodriver fork)

> A Windows-focused fork of [Jackett](https://github.com/Jackett/Jackett) that **replaces FlareSolverr with an embedded, self-managed [nodriver](https://github.com/ultrafunkamsterdam/nodriver) Cloudflare solver.**

## About this fork

Upstream Jackett relies on a separately-installed **FlareSolverr** to get past Cloudflare
"Just a moment..." challenges. On Windows, FlareSolverr (built on undetected-chromedriver) is
reliably detected by Cloudflare and fails to solve many trackers (1337x, KickassTorrents, …).

This fork removes that pain point:

- **Embedded nodriver solver** — instead of FlareSolverr, Jackett ships and manages a small solver
  built on [nodriver](https://github.com/ultrafunkamsterdam/nodriver) (undetected-chromedriver's
  successor: pure Chrome DevTools Protocol, no chromedriver, no Selenium). It passes Cloudflare
  challenges FlareSolverr can't, in a few seconds.
- **Zero setup** — no separate FlareSolverr install, no Docker. Jackett starts the solver
  automatically and downloads a portable Chromium once on first use. The browser runs off-screen,
  so nothing pops up on your screen.
- **Auto-updating** — "Check for updates" updates both Jackett and the bundled solver, and the fork
  auto-syncs from upstream Jackett every 2 days.
- **qBittorrent API-key sync button** — one click copies Jackett's API key straight into your
  qBittorrent config (see below).

Compatibility: Jackett still speaks the FlareSolverr `/v1` protocol internally (via
`FlareSolverrSharp`), so existing indexer settings and the solver URL config keep working — only the
*engine* behind them changed. Leave the solver URL blank to use the embedded nodriver solver.

> **This fork targets Windows x64 only.** For Linux/macOS/Docker, use
> [upstream Jackett](https://github.com/Jackett/Jackett).

---

## What is Jackett?

Jackett is a proxy server that translates queries from apps (Sonarr, Radarr, Prowlarr, qBittorrent,
etc.) into tracker-specific requests, parses the results, and returns them in a standard
[Torznab](https://torznab.github.io/spec-1.3-draft/index.html)/TorrentPotato format — one place to
maintain indexer scraping for all your apps. See the
[upstream tracker list](https://github.com/Jackett/Jackett#supported-trackers) for supported sites.

---

## Installation (Windows)

**Prerequisites:** Windows 10 (1607+), administrator privileges.

### Installer (recommended)
1. Download **Jackett.Installer.Windows.exe** from the [latest release](https://github.com/ctnkyaumt/Jackett/releases/latest).
2. Run it and allow it to make changes.
3. Optionally check "Install as Windows Service" and "Launch Jackett".
4. Open `http://127.0.0.1:9117` (or use the tray icon).

### Manual
1. Download **Jackett.Binaries.Windows.zip** from the [latest release](https://github.com/ctnkyaumt/Jackett/releases/latest).
2. Extract (e.g. to `C:\ProgramData\Jackett`).
3. Run `JackettConsole.exe`, then open `http://127.0.0.1:9117`.

---

## Cloudflare solving (nodriver)

Nothing to configure. When an indexer needs a Cloudflare bypass, Jackett launches the embedded solver
on `http://127.0.0.1:8191` on demand (and restarts it if it dies). nodriver drives a Chrome/Chromium
off-screen to solve the challenge and returns the page and `cf_clearance` cookie to Jackett.

**Browser:** the solver reuses a Chrome/Chromium already on your system rather than downloading one
when it can. It looks for, in order:

1. an explicit override — the `JACKETT_CHROME_PATH` env var, or a `chrome_path.txt` file (one line,
   full path to `chrome.exe`) in `%ProgramData%\Jackett\nodriver`;
2. a real **Google Chrome** or **Chromium** in the standard install locations;
3. only if none is found, a portable Chromium is downloaded once (~290 MB) to
   `%ProgramData%\Jackett\nodriver\chromium`.

> Using a custom Chromium at a non-standard path? Point it there with `chrome_path.txt`.

**Python:** the solver is a small Python service. It prefers a Python already installed on your system
(set up once in a venv), and falls back to a self-contained embeddable Python bundled with Jackett if
there's no usable system Python. The solver source is in [`nodriver/`](nodriver/) and ships inside the
install at `%ProgramData%\Jackett\nodriver`.

> Cloudflare bypass is an arms race and isn't 100% on every attempt, but nodriver solves the common
> hard trackers (1337x, KickassTorrents, …) that FlareSolverr fails on Windows.

---

## qBittorrent API-key sync

Next to the API key in the dashboard header are two buttons:

- **Copy API Key** — copies Jackett's API key to your clipboard.
- **Sync API Key to qBittorrent** — writes Jackett's API key directly into your qBittorrent
  configuration, so qBittorrent's search plugin can reach Jackett without any manual copy-paste. A
  notification confirms success.

---

## Credits

- **[Jackett](https://github.com/Jackett/Jackett)** — the upstream project this fork is built on. All
  the indexer definitions and the core proxy engine are theirs.
- **[nodriver](https://github.com/ultrafunkamsterdam/nodriver)** by ultrafunkamsterdam — the
  Chrome-automation engine that makes the Cloudflare solving work.
- **[FlareSolverr](https://github.com/FlareSolverr/FlareSolverr)** — whose `/v1` API this fork's
  solver implements for drop-in compatibility.

This is an unofficial fork for personal use. For upstream support, cross-platform builds, and full
documentation, see the [original Jackett repository](https://github.com/Jackett/Jackett).
