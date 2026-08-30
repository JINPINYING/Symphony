namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

public sealed class RunEntity
{
    public string Id { get; set; } = string.Empty;
    public string IssueId { get; set; } = string.Empty;
    public string IssueIdentifier { get; set; } = string.Empty;
    public string OwnerInstanceId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;

    // Durable workflow phase for this run (see RunPhaseNames). Lets restart-time
    // reconciliation distinguish an unfinished IMPLEMENTATION from later
    // VERIFY/REVIEW/FINAL_REVIEW work so an existing PR is never reimplemented.
    public string Phase { get; set; } = "implementation";

    // Which agent runner executes this run (M4): codex or claude. Stall detection
    // and dashboards read it — a slower runner gets a wider stall window.
    public string Runner { get; set; } = "codex";
    public int? CurrentRetryAttempt { get; set; }
    public string? WorkspacePath { get; set; }
    public string? SessionId { get; set; }
    public string? RequestedStopReason { get; set; }
    public bool CleanupWorkspaceOnStop { get; set; }
    public string? LastEvent { get; set; }
    public string? LastMessage { get; set; }

    // When the needs_command_center escalation for this run was durably published
    // as a GitHub comment (M1). Null while the escalation is still pending
    // publication; the tick loop retries until set.
    public DateTimeOffset? EscalationPostedAtUtc { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? LastEventAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int TurnCount { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public int LastReportedInputTokens { get; set; }
    public int LastReportedOutputTokens { get; set; }
    public int LastReportedTotalTokens { get; set; }
    public List<RunAttemptEntity> Attempts { get; set; } = [];
}
