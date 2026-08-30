namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

// Durable exactly-once ledger for command-center directives (M3). One row per
// consumed directive comment; the comment id is the natural key. A directive
// with a row here is never acted on again, whatever the ack comment's fate.
public sealed class DirectiveLogEntity
{
    public string CommentId { get; set; } = string.Empty;
    public string IssueId { get; set; } = string.Empty;
    public string IssueIdentifier { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? Phase { get; set; }

    // consumed_dispatched | consumed_closed | consumed_invalid | consumed_already_acked
    public string Outcome { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public DateTimeOffset ConsumedAtUtc { get; set; }
}
