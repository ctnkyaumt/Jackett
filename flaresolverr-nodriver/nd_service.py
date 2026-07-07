"""
FlareSolverr-compatible challenge solver backed by nodriver.

Speaks a subset of the FlareSolverr v1 API (enough for Jackett's FlareSolverrSharp:
request.get / request.post + sessions no-ops) but uses nodriver
(https://github.com/ultrafunkamsterdam/nodriver) instead of undetected-chromedriver.
nodriver drives Chrome purely over the DevTools protocol (no chromedriver, no Selenium),
which passes Cloudflare challenges that undetected-chromedriver is detected on - notably on
Windows, where FlareSolverr can only use stock Chromium + CDP JS patches.

The browser runs HEADED (Cloudflare detects headless) but with its window pushed far
off-screen, so it solves challenges while staying invisible to the user.

Configuration (environment variables):
  ND_CHROME  - full path to a Chrome/Chromium chrome.exe (required)
  PORT       - listen port (default 8191)
  HOST       - listen host (default 127.0.0.1)
  LOG_LEVEL  - "debug" for verbose logging
"""
import asyncio
import logging
import os
import sys
import time

from aiohttp import web
import nodriver as uc

VERSION = "nodriver-1.0"

CHROME = os.environ.get("ND_CHROME", "")
PORT = int(os.environ.get("PORT", "8191"))
HOST = os.environ.get("HOST", "127.0.0.1")

logging.basicConfig(
    level=logging.DEBUG if os.environ.get("LOG_LEVEL", "").lower() == "debug" else logging.INFO,
    format="%(asctime)s %(levelname)-8s %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
    handlers=[logging.StreamHandler(sys.stdout)],
)
log = logging.getLogger("nd-flaresolverr")

# Titles/markers that indicate a Cloudflare (or similar) interstitial is still showing.
CHALLENGE_MARKERS = [
    "just a moment", "bir dakika", "checking your browser", "attention required",
    "cloudflare", "verifying you are", "un momento", "einen moment", "moment...",
    "ddos-guard", "please wait",
]

# One browser at a time - each request gets a fresh browser, like FlareSolverr sessions.
_solve_lock = asyncio.Lock()


def _is_challenge(text):
    t = (text or "").lower()
    return any(m in t for m in CHALLENGE_MARKERS)


async def _solve(url, max_timeout_ms):
    async with _solve_lock:
        browser = await uc.start(
            browser_executable_path=CHROME,
            headless=False,  # headless is detected by Cloudflare; run headed but off-screen
            browser_args=[
                "--no-sandbox",
                "--disable-gpu",
                "--window-position=-32000,-32000",
                "--window-size=1920,1080",
            ],
        )
        try:
            deadline = time.time() + max(5.0, max_timeout_ms / 1000.0)
            page = await browser.get(url)
            html, title = "", ""
            while time.time() < deadline:
                await asyncio.sleep(1.5)
                try:
                    title = await page.evaluate("document.title") or ""
                    html = await page.get_content()
                except Exception:
                    pass
                if title and not _is_challenge(title) and not _is_challenge(html[:4000]):
                    break

            try:
                ua = await page.evaluate("navigator.userAgent")
            except Exception:
                ua = ""

            cookies = []
            try:
                for c in await browser.cookies.get_all():
                    cookies.append({
                        "name": c.name,
                        "value": c.value,
                        "domain": c.domain,
                        "path": c.path,
                        "expires": float(getattr(c, "expires", -1) or -1),
                        "httpOnly": bool(getattr(c, "http_only", False)),
                        "secure": bool(getattr(c, "secure", False)),
                    })
            except Exception:
                log.debug("failed reading cookies", exc_info=True)

            solved = bool(html) and not _is_challenge(html[:4000])
            solution = {
                "url": url,
                "status": 200,
                "headers": {},
                "response": html,
                "cookies": cookies,
                "userAgent": ua,
            }
            return solution, solved
        finally:
            try:
                browser.stop()
            except Exception:
                pass


async def handle_v1(request):
    t0 = int(time.time() * 1000)
    try:
        body = await request.json()
    except Exception:
        body = {}
    cmd = body.get("cmd", "")

    if cmd in ("request.get", "request.post"):
        url = body.get("url")
        max_timeout = int(body.get("maxTimeout", 60000) or 60000)
        log.info("Solving %s (maxTimeout=%dms)", url, max_timeout)
        try:
            solution, solved = await _solve(url, max_timeout)
            log.info("%s -> %s", url, "solved" if solved else "NOT solved")
            return web.json_response({
                "status": "ok" if solved else "error",
                "message": "Challenge solved!" if solved else "Challenge not solved before timeout",
                "startTimestamp": t0,
                "endTimestamp": int(time.time() * 1000),
                "version": VERSION,
                "solution": solution,
            })
        except Exception as e:
            log.error("solve error for %s: %s", url, e, exc_info=True)
            return web.json_response({
                "status": "error",
                "message": f"Error: {e}",
                "startTimestamp": t0,
                "endTimestamp": int(time.time() * 1000),
                "version": VERSION,
                "solution": {},
            })

    if cmd.startswith("sessions."):
        # Jackett rarely uses sessions; acknowledge without maintaining any.
        return web.json_response({"status": "ok", "message": "", "session": "nodriver", "sessions": []})

    return web.json_response({"status": "ok", "message": "nodriver FlareSolverr", "version": VERSION})


async def handle_root(request):
    return web.json_response({"msg": "FlareSolverr is ready!", "version": VERSION})


def main():
    if not CHROME or not os.path.exists(CHROME):
        log.error("ND_CHROME is not set or does not exist: %r", CHROME)
        sys.exit(1)
    app = web.Application(client_max_size=1024 ** 3)
    app.router.add_get("/", handle_root)
    app.router.add_post("/v1", handle_v1)
    log.info("nodriver FlareSolverr %s starting on http://%s:%d (chrome=%s)", VERSION, HOST, PORT, CHROME)
    web.run_app(app, host=HOST, port=PORT, print=None)


if __name__ == "__main__":
    main()
