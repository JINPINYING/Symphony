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
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
