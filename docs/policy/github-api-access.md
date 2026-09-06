# GitHub API Access Policy

Symphony treats GitHub REST `core` and GitHub GraphQL as separate 5,000 point hourly budgets. Neither is the free alternative to the other.

Reads that decide dispatch, phase, review, directive, or merge-gate state must be complete, or must surface that they are incomplete. REST collections must page to completion unless the caller explicitly treats a full page as truncated. GraphQL connections that are consumed in full must request `totalCount` and re-read wider when a narrow page is short.

Steady-state reads must be modelled against both budgets before moving load between transports. The runtime also records response header budget readings and REST call-site attribution from the calls it is already making; `gh api rate_limit` is not authoritative evidence for Symphony's own burn.

Prefer conditional REST requests for resources that usually do not change between polls. A `304 Not Modified` response may replay the stored representation only after GitHub confirms the validator, and it must be recorded as an uncharged call-site observation.
