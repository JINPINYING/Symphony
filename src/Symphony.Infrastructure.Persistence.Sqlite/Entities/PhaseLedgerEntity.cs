namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

// M4 durable phase ledger — one row per issue moving through the routine loop
// (implementation -> verify -> review -> [one repair -> final_review] -> ready).
// This is the PLATFORM-15 contract ported into Symphony: exact-head review
// provenance, a ledger-derived repair count, and a recorded rejected head so a
// final review can never run against unchanged rejected code (WAIT_FOR_REPAIR).
// The ledger is required state, never a fail-open default: phase decisions load
// it and refuse to act when it is missing or inconsistent.
public sealed class PhaseLedgerEntity
{
    public string IssueId { get; set; } = string.Empty;
    public string IssueIdentifier { get; set; } = string.Empty;

    // "owner/repo" the PR below lives in. Empty means the primary repository.
    // Without it the orchestrator cannot know which repository to ask about
    // PR #122, and two repositories can each have one.
    public string Repository { get; set; } = string.Empty;

    // awaiting_verify | awaiting_review | reviewing | wait_for_repair |
    // awaiting_final_review | final_reviewing | ready | escalated
    public string Stage { get; set; } = string.Empty;

    public int PrNumber { get; set; }
    public string? HeadSha { get; set; }

    // Vendor that implemented (codex|claude); the reviewer is always the other.
    public string ImplementerRunner { get; set; } = string.Empty;

    public int RepairCount { get; set; }
    public string? RejectedHeadSha { get; set; }
    public string? LastVerdict { get; set; }
    public string? LastVerdictHeadSha { get; set; }

    // A transient runner refusal the phase is waiting out rather than escalating
    // (ADCP#29). A vendor that is out of quota has not violated anything and there
    // is no directive that buys it credits, so the phase holds where it is until
    // the reset the refusal named and then asks again.
    //
    // Durable on the ledger rather than in memory, for the same reason the
    // candidate-scan pause is durable: restarting is exactly what a person does
    // when the board says something needs them, and a restart must not cancel a
    // wait whose clock belongs to the vendor's account rather than to this process.
    public DateTimeOffset? HoldUntilUtc { get; set; }

    // When the CURRENT run of holds began - not when the latest one was renewed.
    // A quota window that keeps being renewed for longer than a window can
    // plausibly last is an account problem, and that is the only thing here that
    // is genuinely the owner's.
    public DateTimeOffset? HoldSinceUtc { get; set; }
    public string? HoldReason { get; set; }
    public string? HoldRunner { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
