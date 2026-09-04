#!/usr/bin/env python3
"""Tests for the Watchtower snapshot builder's coupling assertions.

WHY THESE EXIST
---------------
The builder is designed to abort when the renderer moves under it, and that
abort is load-bearing: it is the only thing standing between a changed page and
a published copy that looks right and is not.

It is also the thing that stopped publishing for two days. `renderStalenessBanner`
arrived as a second `setInterval`, the check refused to guess what it was for,
and the unattended publisher had nothing to publish from then on. Nobody noticed
until the owner read a stale page and took its stale badge for a dead engine.

So both halves are asserted here: that the couplings hold against the renderer
as it actually ships, and that they still break when they should. A guard that
has quietly stopped guarding is worse than no guard, and the only way to know
which one this is, is to make it fail on purpose.

    python -m unittest discover -s scripts -p "test_*.py"
"""

from __future__ import annotations

import datetime as dt
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import build_watchtower_snapshot as builder  # noqa: E402


WWWROOT = Path(__file__).resolve().parent.parent / "src" / "Symphony.Host" / "wwwroot"


def read_renderer() -> tuple[str, str]:
    index_html = (WWWROOT / "index.html").read_text(encoding="utf-8-sig")
    dashboard_js = (WWWROOT / "assets" / "dashboard.js").read_text(encoding="utf-8-sig")
    return index_html, dashboard_js


class CouplingsHoldTests(unittest.TestCase):
    """The state the repository must be in for a snapshot to be publishable."""

    def test_check_passes_against_the_shipped_renderer(self):
        index_html, dashboard_js = read_renderer()
        passed = builder.assert_couplings(index_html, dashboard_js)
        self.assertTrue(passed)

    def test_every_timer_that_keeps_running_names_the_function_the_shim_matches(self):
        # The shim identifies live timers by callback name at runtime. A timer
        # declared live with no name would be silently stubbed instead.
        _, dashboard_js = read_renderer()
        for timer in builder.SNAPSHOT_TIMERS:
            if timer["keep_running"]:
                self.assertTrue(timer["callback"], timer["name"])
                self.assertIn(f"function {timer['callback']}(", dashboard_js)


class TheAbortIsTheFeatureTests(unittest.TestCase):
    """Changes that must break the build rather than ship."""

    def test_a_third_timer_aborts_the_build(self):
        index_html, dashboard_js = read_renderer()
        mutated = dashboard_js + "\nwindow.setInterval(someNewTimer, 1000);\n"

        with self.assertRaises(builder.BuildError) as caught:
            builder.assert_couplings(index_html, mutated)

        message = str(caught.exception)
        self.assertIn("setInterval 3 times", message)
        # It must name what it already knows, so the reader can see which
        # decision is missing rather than only that a number changed.
        self.assertIn("view-age repaint", message)
        self.assertIn("auto-refresh loop", message)

    def test_losing_the_view_age_timer_aborts_the_build(self):
        # The capture must keep ageing on screen. If the renderer stops driving
        # that, the snapshot's honesty mechanism is gone and the build must say
        # so rather than publish a page frozen at "captured just now".
        index_html, dashboard_js = read_renderer()
        mutated = dashboard_js.replace("window.setInterval(renderViewAge, 5000);", "")

        with self.assertRaises(builder.BuildError) as caught:
            builder.assert_couplings(index_html, mutated)

        self.assertIn("view-age repaint", str(caught.exception))

    def test_renaming_the_refresh_loop_aborts_the_build(self):
        index_html, dashboard_js = read_renderer()
        mutated = dashboard_js.replace(
            "refreshHandle = window.setInterval(", "pollHandle = window.setInterval(")

        with self.assertRaises(builder.BuildError) as caught:
            builder.assert_couplings(index_html, mutated)

        self.assertIn("auto-refresh loop", str(caught.exception))

    def test_an_uncaptured_api_path_aborts_the_build(self):
        index_html, dashboard_js = read_renderer()
        mutated = dashboard_js + '\nfetch("/api/v1/something-new");\n'

        with self.assertRaises(builder.BuildError) as caught:
            builder.assert_couplings(index_html, mutated)

        self.assertIn("/api/v1/something-new", str(caught.exception))

    def test_renaming_a_live_control_aborts_the_build(self):
        # A control the snapshot can no longer hide is a control that ships
        # looking operable, which is worse than not shipping the page.
        index_html, dashboard_js = read_renderer()
        mutated = dashboard_js.replace('data-action="post-directive"', 'data-action="send-directive"')

        with self.assertRaises(builder.BuildError) as caught:
            builder.assert_couplings(index_html, mutated)

        self.assertIn("post-directive", str(caught.exception))


class ShimTests(unittest.TestCase):
    """What the generated page actually does with the renderer's timers."""

    def build_page(self) -> str:
        index_html, dashboard_js = read_renderer()
        inline_styles, body = builder.extract(index_html)
        return builder.render(
            dashboard_css="",
            inline_styles=inline_styles,
            body=body,
            dashboard_js=dashboard_js,
            captured={"/api/v1/health": {"status": 200, "body": "ok", "kind": "text"}},
            captured_at=dt.datetime(2026, 9, 3, 17, 33, 0),
        )

    def test_the_shim_keeps_the_view_age_timer_and_stubs_the_others(self):
        page = self.build_page()

        # Not a blanket stub any more: that is what could not distinguish the
        # two timers, and it would freeze the capture's age on screen.
        self.assertNotIn("window.setInterval = function () { return 0; }", page)
        self.assertIn('var LIVE_TIMERS = ["renderViewAge"]', page)
        self.assertIn("LIVE_TIMERS.indexOf(name) !== -1", page)
        self.assertIn("nativeSetInterval", page)

    def test_the_shim_stamps_the_capture_time(self):
        page = self.build_page()
        self.assertIn('data-utc="2026-09-03T17:33:00Z"', page)

    def test_live_controls_are_hidden_by_selector(self):
        page = self.build_page()
        for selector, _ in builder.SNAPSHOT_HIDDEN:
            self.assertIn(selector, page)
        self.assertIn("{display:none}", page)


if __name__ == "__main__":
    unittest.main()
