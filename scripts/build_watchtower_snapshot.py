#!/usr/bin/env python3
"""Build a self-contained, publishable snapshot of the live Watchtower page.

WHY THIS EXISTS
---------------
The Watchtower is one design with two surfaces, and the split is physical rather
than architectural: the engine's page is bound to loopback so it can never reach
a phone, and a published artifact is a snapshot so it can never be live.

PR #19 ("one Watchtower") moved the owner-facing answer into the engine so that
publishing would become a COPY rather than an authoring step -- the old artifact
went stale whenever nobody was around to hand-write it. This script is that copy
step. It takes the engine's own renderer and its own live state and freezes them
into a single file that can be published as-is.

Nothing here re-describes the system. If the wording on the published page is
wrong, the fix belongs in the engine, not in this script.

WHAT IT PRODUCES
----------------
One HTML fragment (no <!doctype>/<html>/<head>/<body> -- the artifact host wraps
it) containing:
  * the engine's stylesheet and markup, inlined
  * one frozen capture of every endpoint the renderer reads
  * a shim that answers the renderer's fetches from that capture, and stubs the
    renderer's timers except the one that keeps the capture ageing on screen
  * a banner stamping the capture time, so the page cannot pretend to be live

HOW IT FAILS
------------
Loudly, and before writing anything. The renderer and this script are coupled --
that is the point, they ship together -- so the build asserts the couplings it
depends on and aborts when one no longer holds. A failed build is a correct
outcome: it means the page changed and the copy step needs a look. Shipping a
silently broken snapshot would be the actual failure.

USAGE
-----
    python scripts/build_watchtower_snapshot.py --out watchtower-snapshot.html

    --base-url   engine origin to capture from (default http://127.0.0.1:43123)
    --wwwroot    renderer source (default: located relative to this script)
    --check      run the coupling assertions and exit; writes nothing
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

DEFAULT_BASE_URL = "http://127.0.0.1:43123"
REQUEST_TIMEOUT_SECONDS = 15

# The engine renders dark and commits to it; the artifact host paints a light
# ground behind the page. dashboard.css sets background-IMAGE gradients on body
# but no background-COLOR, so without this the gradients composite over the
# host's cream and the page reads wrong. Matches <meta name="theme-color">.
SNAPSHOT_GROUND = "#041319"

# Endpoints the renderer reads. Captured verbatim; served back by the shim.
# `text` endpoints are returned as text/plain -- /api/v1/health deliberately
# returns a bare word, not JSON, and fetchHealth() reads it with .text().
READ_ENDPOINTS = (
    ("/api/v1/health", "text"),
    ("/api/v1/runtime", "json"),
    ("/api/v1/state", "json"),
    ("/api/v1/state?raw=true", "json"),
    ("/api/v1/workflow", "json"),
)

# Endpoints that CHANGE something. A snapshot must never appear to accept one:
# POST /refresh poking a machine the viewer is not on, or PUT /workflow editing
# the contract, would both be worse than an honest refusal.
# /api/v1/actions/directive posts a directive comment to the tracker on the
# owner's behalf. It is the one control the live page can genuinely ACT with, so
# a snapshot must refuse it rather than let a phone post into an issue from a
# frozen page.
CONTROL_ENDPOINTS = (
    "/api/v1/refresh",
    "/api/v1/workflow",
    "/api/v1/actions/directive",
)

# Cap on per-issue detail requests, so a large backlog cannot turn one build
# into hundreds of calls against the engine.
MAX_ISSUE_DETAILS = 60

# Chrome that must not appear on a published copy, and the token proving the
# renderer still emits it. A control that looks live but is inert is worse than
# an absent one -- and if the renderer renames these, the build must fail rather
# than quietly ship a page with a working-looking Refresh button.
#   #workflow-editor           writes the workflow contract back (PUT)
#   .wt-switch                 the auto-refresh toggle
#   [data-action=refresh]      "Refresh now", in the header and in the
#                              staleness banner -- the banner's copy is
#                              reachable here because that banner keeps running
#   [data-action=post-directive]  posts a directive comment to the tracker,
#                              the one control on the page that acts outward
SNAPSHOT_HIDDEN = (
    ("#workflow-editor", 'id="workflow-editor"'),
    (".wt-switch", 'class="wt-switch"'),
    ('[data-action="refresh"]', 'data-action="refresh"'),
    ('[data-action="post-directive"]', 'data-action="post-directive"'),
)

# The renderer's timers, one entry each, and what a snapshot must do with them.
#
# The shim used to stub setInterval globally, which was safe only while the
# refresh loop was the renderer's ONLY timer. It stopped being so, and the
# check aborted every build for two days rather than guess - correctly, because
# the two timers must NOT share a fate:
#
#   * the refresh loop has nothing to refresh from in a static capture, and
#     re-rendering frozen data forever would look live on the viewer's phone;
#   * the view-age repaint is the honesty mechanism the whole design rests on.
#     A capture that stops ageing on screen stops being a capture and starts
#     being a claim about now.
#
# So they are named rather than counted. A THIRD timer matches nothing here, the
# build aborts the way this one did, and someone decides which of the two it is.
# That abort is the feature: a timer nobody classified is a timer nobody
# reasoned about, and this file cannot reason about it for them.
#
# `callback` is the function NAME the shim matches at runtime, and is required
# for a timer that keeps running. Anything the shim does not recognise is
# stubbed, so an unclassified timer fails safe as well as failing loud.
SNAPSHOT_TIMERS = (
    {
        "name": "view-age repaint",
        "callback": "renderViewAge",
        "site": r"window\.setInterval\(\s*renderViewAge\s*,",
        "keep_running": True,
    },
    {
        "name": "auto-refresh loop",
        "callback": None,
        "site": r"refreshHandle\s*=\s*window\.setInterval\(",
        "keep_running": False,
    },
)


class BuildError(RuntimeError):
    """A coupling this script depends on no longer holds."""


# ---------------------------------------------------------------------------
# capture
# ---------------------------------------------------------------------------

def fetch(base_url: str, path: str) -> tuple[int, str]:
    url = base_url.rstrip("/") + path
    request = urllib.request.Request(url, headers={"Cache-Control": "no-store"})
    try:
        with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            return response.status, response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", errors="replace")
    except urllib.error.URLError as error:
        raise BuildError(
            f"Could not reach the engine at {url}: {error.reason}. "
            "The snapshot is a copy of live state, so the engine must be running."
        ) from error


def capture_reads(base_url: str) -> dict[str, dict]:
    captured: dict[str, dict] = {}
    for path, kind in READ_ENDPOINTS:
        status, body = fetch(base_url, path)
        if status != 200:
            raise BuildError(
                f"{path} returned {status}, so this capture would freeze an error "
                f"into the published page. Body: {body[:200]}"
            )
        if kind == "json":
            try:
                json.loads(body)
            except json.JSONDecodeError as error:
                raise BuildError(f"{path} did not return valid JSON: {error}") from error
        captured[path] = {"status": status, "body": body, "kind": kind}
    return captured


def issue_identifiers(state_json: str) -> list[str]:
    """Issue identifiers the page can navigate to without another live call."""
    state = json.loads(state_json)
    found: list[str] = []

    def collect(entries) -> None:
        for entry in entries or []:
            identifier = (entry or {}).get("issue_identifier")
            if identifier and identifier not in found:
                found.append(identifier)

    collect(state.get("running"))
    collect(state.get("retrying"))
    collect((state.get("tracked") or {}).get("recently_updated"))
    collect(state.get("staff"))
    return found[:MAX_ISSUE_DETAILS]


def capture_issue_details(base_url: str, identifiers: list[str]) -> dict[str, dict]:
    captured: dict[str, dict] = {}
    for identifier in identifiers:
        path = "/api/v1/" + urllib.parse.quote(identifier, safe="")
        status, body = fetch(base_url, path)
        # A detail that 404s is not a build failure -- the issue may have been
        # closed between the state read and this one. Freeze what came back.
        captured[path] = {"status": status, "body": body, "kind": "json"}
    return captured


def attention_summary(captured: dict[str, dict]) -> dict:
    """The engine's own 'does this need me?' verdict, for the publisher to act on.

    Deliberately a passthrough. The publisher decides whether to notify, but it
    must not decide whether something is WRONG -- that judgement lives in
    OwnerAttentionSummary so both surfaces answer identically.
    """
    state = json.loads(captured["/api/v1/state"]["body"])
    attention = state.get("attention") or {}
    return {
        "level": attention.get("level"),
        "headline": attention.get("headline"),
        "detail": attention.get("detail"),
        "items": attention.get("items") or [],
        "generated_at": state.get("generated_at"),
    }


# ---------------------------------------------------------------------------
# coupling assertions -- see "HOW IT FAILS"
# ---------------------------------------------------------------------------

def assert_couplings(index_html: str, dashboard_js: str) -> list[str]:
    """Verify every assumption the shim makes. Returns the checks that passed."""
    passed: list[str] = []

    # 1. The shim decides per timer whether it may keep running against frozen
    #    data. Every call site must therefore be one of the timers named in
    #    SNAPSHOT_TIMERS -- an unnamed one is an unmade decision.
    for timer in SNAPSHOT_TIMERS:
        if timer["keep_running"] and not timer["callback"]:
            raise BuildError(
                f"The {timer['name']} timer is meant to keep running in a snapshot, "
                "but declares no callback name for the shim to match on. Name the "
                "function in dashboard.js and put it in SNAPSHOT_TIMERS."
            )
        hits = len(re.findall(timer["site"], dashboard_js))
        if hits != 1:
            raise BuildError(
                f"dashboard.js should contain exactly one {timer['name']} timer, but "
                f"{hits} call sites match it. The shim identifies timers one by one, "
                "so update SNAPSHOT_TIMERS to match what the renderer now does."
            )

    total = len(re.findall(r"\bsetInterval\s*\(", dashboard_js))
    if total != len(SNAPSHOT_TIMERS):
        known = "; ".join(
            f"{timer['name']} ({'kept running' if timer['keep_running'] else 'stubbed'})"
            for timer in SNAPSHOT_TIMERS
        )
        raise BuildError(
            f"dashboard.js calls setInterval {total} times, but the snapshot knows "
            f"what to do with {len(SNAPSHOT_TIMERS)} of them: {known}. Decide whether "
            "the new timer must keep running against a frozen capture or be stubbed, "
            "then add it to SNAPSHOT_TIMERS. A timer nobody classified is a timer "
            "nobody reasoned about, and this build will not guess."
        )
    passed.append(
        f"all {total} setInterval call sites named and classified ("
        + ", ".join(
            f"{timer['name']}: {'live' if timer['keep_running'] else 'stubbed'}"
            for timer in SNAPSHOT_TIMERS
        )
        + ")"
    )

    # 2. Every API path the renderer names must be answerable from the capture,
    #    or the published page shows an error where content should be.
    named_paths = set(re.findall(r"[\"'`](/api/v1/[^\"'`]*)[\"'`]", dashboard_js))
    known = {path for path, _ in READ_ENDPOINTS} | set(CONTROL_ENDPOINTS)
    # Template literals for issue detail arrive as `/api/v1/${...}` -- the shim
    # matches those by prefix, so treat any templated path as covered.
    unknown = {
        path for path in named_paths
        if path not in known and "${" not in path
    }
    if unknown:
        raise BuildError(
            "dashboard.js reads API paths the snapshot does not capture: "
            + ", ".join(sorted(unknown))
            + ". Add them to READ_ENDPOINTS (or CONTROL_ENDPOINTS if they mutate)."
        )
    passed.append(f"all {len(named_paths)} API paths in the renderer are covered")

    # 3. The artifact CSP blocks every external host except Google Fonts. The
    #    engine page must therefore stay entirely self-hosted.
    external = re.findall(r"(?:href|src)\s*=\s*[\"'](https?://[^\"']+)", index_html)
    if external:
        raise BuildError(
            "index.html now loads external assets, which the artifact CSP blocks: "
            + ", ".join(external)
        )
    passed.append("no external asset references")

    # 4. The inlining below depends on these exact local asset references.
    for asset in ("/assets/dashboard.css", "/assets/dashboard.js"):
        if asset not in index_html:
            raise BuildError(
                f"index.html no longer references {asset}; the inliner cannot find "
                "what to inline."
            )
    passed.append("both local assets referenced as expected")

    # 5. The snapshot hides live controls by selector. If the renderer renames
    #    one, the rule stops matching and a published page grows a Refresh button
    #    that looks operable and is not -- so the rename must break the build.
    markup = index_html + dashboard_js
    missing = [selector for selector, token in SNAPSHOT_HIDDEN if token not in markup]
    if missing:
        raise BuildError(
            "The snapshot hides these live controls, but the renderer no longer emits "
            "them under the expected names: " + ", ".join(missing)
            + ". Update SNAPSHOT_HIDDEN so the published copy stays read-only."
        )
    passed.append(f"all {len(SNAPSHOT_HIDDEN)} live controls still hideable by selector")

    return passed


# ---------------------------------------------------------------------------
# assemble
# ---------------------------------------------------------------------------

def extract(index_html: str) -> tuple[str, str]:
    """Return (inline styles from <head>, body inner HTML)."""
    head_match = re.search(r"<head\b[^>]*>(.*?)</head>", index_html, re.S | re.I)
    body_match = re.search(r"<body\b[^>]*>(.*?)</body>", index_html, re.S | re.I)
    if not head_match or not body_match:
        raise BuildError("index.html is not the expected <head>/<body> document.")

    inline_styles = "\n".join(
        match.group(1)
        for match in re.finditer(r"<style\b[^>]*>(.*?)</style>", head_match.group(1), re.S | re.I)
    )

    body = body_match.group(1)
    # Drop the script tag; its contents are inlined separately, after the shim.
    body = re.sub(r"<script\b[^>]*>.*?</script>", "", body, flags=re.S | re.I)
    return inline_styles, body


def render(
    *,
    dashboard_css: str,
    inline_styles: str,
    body: str,
    dashboard_js: str,
    captured: dict[str, dict],
    captured_at: dt.datetime,
) -> str:
    captured_utc = captured_at.strftime("%Y-%m-%dT%H:%M:%SZ")
    # Eastern is the owner's reading timezone; UTC stays the durable record.
    eastern = captured_at - dt.timedelta(hours=4)
    stamp = f"{eastern.strftime('%H:%M')} ET &middot; {captured_at.strftime('%H:%M')} UTC"

    payload = json.dumps(captured, separators=(",", ":"))
    # </script> inside a JSON string would close the tag early.
    payload = payload.replace("</", "<\\/")

    hidden_rules = ",".join(selector for selector, _ in SNAPSHOT_HIDDEN) + "{display:none}"

    # Callback names the shim lets through. Everything else is stubbed.
    live_timers = json.dumps(
        [timer["callback"] for timer in SNAPSHOT_TIMERS if timer["keep_running"]])

    return f"""<title>Symphony Watchtower</title>
<style>
{dashboard_css}
</style>
<style>
{inline_styles}
</style>
<style>
/* --- snapshot-only ------------------------------------------------------
   The engine's page commits to a dark ground. dashboard.css sets background
   gradients on body but no background-COLOR, and the artifact host paints its
   own ground underneath, so the colour has to be stated here or the gradients
   composite over the host's. -------------------------------------------- */
body{{background-color:{SNAPSHOT_GROUND}}}
.snapshot-bar{{position:sticky;top:0;z-index:60;display:flex;flex-wrap:wrap;align-items:center;
  gap:8px 14px;padding:9px 18px;background:#02090d;border-bottom:1px solid rgba(148,163,184,.22);
  font-family:Aptos,Segoe UI,sans-serif;font-size:12.5px;color:#CBD5E1}}
.snapshot-bar b{{color:#F1F5F9;font-weight:600;letter-spacing:.02em}}
.snapshot-age{{font-variant-numeric:tabular-nums;padding:2px 9px;border-radius:999px;
  border:1px solid rgba(94,234,190,.5);color:#8FEDD0;white-space:nowrap}}
.snapshot-age.aging{{border-color:rgba(242,186,96,.6);color:#FFCE86}}
.snapshot-age.stale{{border-color:rgba(242,146,146,.65);color:#FFAFAF}}
.snapshot-note{{color:#94A3B8}}
/* A snapshot is read-only, so every control that acts on the engine is removed
   rather than left looking operable. See SNAPSHOT_HIDDEN in the builder. */
{hidden_rules}
</style>

<div class="snapshot-bar">
  <b>Snapshot</b>
  <span class="snapshot-age" id="snapshot-age" data-utc="{captured_utc}">captured just now</span>
  <span class="snapshot-note">taken {stamp} &mdash; a frozen copy of the live Watchtower, read-only. Controls and the workflow editor are inactive here.</span>
</div>
{body}
<script>
/* ---------------------------------------------------------------------------
   Snapshot shim. Installed as a classic script so it runs before the deferred
   module below. It answers the renderer's own fetches from a frozen capture,
   so the renderer needs no knowledge that it is being published.
   --------------------------------------------------------------------------- */
(function () {{
  "use strict";
  var CAPTURED = {payload};

  function respond(entry) {{
    var headers = {{ "content-type": entry.kind === "json" ? "application/json" : "text/plain" }};
    return Promise.resolve(new Response(entry.body, {{ status: entry.status, headers: headers }}));
  }}

  function refuse(message) {{
    return Promise.resolve(new Response(
      JSON.stringify({{ error: {{ message: message }} }}),
      {{ status: 503, headers: {{ "content-type": "application/json" }} }}
    ));
  }}

  var nativeFetch = window.fetch.bind(window);

  window.fetch = function (input, options) {{
    var url = typeof input === "string" ? input : (input && input.url) || "";
    var method = ((options && options.method) || (input && input.method) || "GET").toUpperCase();
    var path = url.replace(/^https?:\\/\\/[^/]+/, "");

    if (path.indexOf("/api/v1/") !== 0) {{
      return nativeFetch(input, options);
    }}

    if (method !== "GET") {{
      return refuse(
        "This is a published snapshot of the Watchtower, not the live engine. " +
        "Controls are inactive here -- run them on the machine itself."
      );
    }}

    if (Object.prototype.hasOwnProperty.call(CAPTURED, path)) {{
      return respond(CAPTURED[path]);
    }}

    return refuse("Not captured in this snapshot: " + path + ".");
  }};

  /* The renderer has more than one timer and they must not share a fate.
     The 15-second refresh loop has nothing to refresh from here, so it is
     stubbed. The view-age repaint must keep running: it is what makes this
     capture visibly age, and a capture that stops ageing on screen starts
     reading as a claim about now.

     Matched by callback NAME, which the build asserts still exists. Anything
     unrecognised is stubbed, so a timer added without classifying it is inert
     here rather than accidentally live -- though the build aborts first. */
  var LIVE_TIMERS = {live_timers};
  var nativeSetInterval = window.setInterval.bind(window);

  window.setInterval = function (handler) {{
    var name = typeof handler === "function" ? handler.name : "";
    if (LIVE_TIMERS.indexOf(name) !== -1) {{
      return nativeSetInterval.apply(null, arguments);
    }}
    return 0;
  }};

  /* The honesty stamp: a snapshot that hides its own age is worse than no page. */
  document.addEventListener("DOMContentLoaded", function () {{
    var el = document.getElementById("snapshot-age");
    if (!el) return;
    var t0 = Date.parse(el.dataset.utc);
    if (isNaN(t0)) return;
    (function tick() {{
      var m = Math.floor((Date.now() - t0) / 60000);
      if (m < 0) m = 0;
      var text = m < 1 ? "captured just now"
        : m < 60 ? "captured " + m + " min ago"
        : "captured " + Math.floor(m / 60) + "h " + (m % 60) + "m ago";
      el.className = "snapshot-age" + (m >= 45 ? " stale" : m >= 20 ? " aging" : "");
      el.textContent = text;
      window.setTimeout(tick, 15000);
    }})();
  }});
}})();
</script>
<script type="module">
{dashboard_js}
</script>
"""


# ---------------------------------------------------------------------------
# entry point
# ---------------------------------------------------------------------------

def locate_wwwroot(explicit: str | None) -> Path:
    if explicit:
        path = Path(explicit)
    else:
        path = Path(__file__).resolve().parent.parent / "src" / "Symphony.Host" / "wwwroot"
    if not (path / "index.html").is_file():
        raise BuildError(f"No index.html under {path}; pass --wwwroot.")
    return path


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL)
    parser.add_argument("--wwwroot", default=None)
    parser.add_argument("--out", default=None)
    parser.add_argument("--check", action="store_true",
                        help="run the coupling assertions and exit; writes nothing")
    args = parser.parse_args(argv)

    if not args.check and not args.out:
        parser.error("--out is required unless --check is given")

    try:
        wwwroot = locate_wwwroot(args.wwwroot)
        index_html = (wwwroot / "index.html").read_text(encoding="utf-8-sig")
        dashboard_css = (wwwroot / "assets" / "dashboard.css").read_text(encoding="utf-8-sig")
        dashboard_js = (wwwroot / "assets" / "dashboard.js").read_text(encoding="utf-8-sig")

        for check in assert_couplings(index_html, dashboard_js):
            print(f"  ok  {check}")

        if args.check:
            print("Couplings hold; the snapshot builder is in step with the renderer.")
            return 0

        captured = capture_reads(args.base_url)
        identifiers = issue_identifiers(captured["/api/v1/state"]["body"])
        captured.update(capture_issue_details(args.base_url, identifiers))
        print(f"  ok  captured {len(READ_ENDPOINTS)} endpoints "
              f"and {len(identifiers)} issue details")

        inline_styles, body = extract(index_html)
        html = render(
            dashboard_css=dashboard_css,
            inline_styles=inline_styles,
            body=body,
            dashboard_js=dashboard_js,
            captured=captured,
            captured_at=dt.datetime.now(dt.timezone.utc).replace(tzinfo=None, microsecond=0),
        )

        out = Path(args.out)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(html, encoding="utf-8")
        print(f"Wrote {out} ({len(html.encode('utf-8')) / 1024:.0f} KB).")

        # The publisher needs to decide whether to notify, and the engine has
        # already made that judgement in OwnerAttentionSummary. Printing it here
        # keeps publishing to a single command -- the publisher never has to form
        # its own opinion about whether something is wrong, which is the whole
        # reason the answer was moved into the engine.
        print(json.dumps({"attention": attention_summary(captured)}, indent=1))
        return 0

    except BuildError as error:
        print(f"BUILD FAILED: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
