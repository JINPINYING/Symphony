# scripts

Tooling that has to ship with the engine because it depends on the engine's own
internals. Nothing here is part of the running service.

## `build_watchtower_snapshot.py`

Freezes the live Watchtower into one self-contained HTML file that can be
published for a phone.

```
python scripts/build_watchtower_snapshot.py --out watchtower-snapshot.html
python scripts/build_watchtower_snapshot.py --check   # assertions only
```

It also prints the engine's own `attention` verdict on stdout, so the publisher
can decide whether to notify without forming its own opinion about whether
anything is wrong. That judgement lives in `OwnerAttentionSummary`; keeping it
there is why both surfaces answer identically.

**Why it lives here and not in the control-plane repo.** It inlines
`wwwroot/index.html`, `dashboard.css` and `dashboard.js`, and it answers the
renderer's own `fetch` calls from a frozen capture. That is a tight coupling to
the renderer, deliberately: PR #19 made publishing a *copy* of the engine's page
rather than a hand-written second page, and a copy step that lived in another
repository would drift from the thing it copies. Here it moves with it.

**It is designed to fail loudly.** `--check` asserts the couplings the shim
depends on — that the refresh loop is still the only `setInterval`, that every
API path the renderer names is captured, that no external assets crept in, and
that the live controls it hides are still named what it expects. If you change
the dashboard and this build fails, the failure is the point: read what it says
and update the builder. A snapshot that publishes anyway would be a page that
looks right and is not.

**What the snapshot is not.** It is read-only by construction: control endpoints
answer `503` with an explanation, and the workflow editor and refresh controls
are removed rather than left looking operable. It stamps its own capture time and
ages that stamp on screen, because a status page that hides its age is worse than
no status page.
