namespace Symphony.Core.Models;

// Snapshot used for idempotent escalation publishing: the issue's current state
// plus whether the given marker string already appears in any comment body
// (check-before-post). Url is the issue's html url when the tracker returned one.
public sealed record IssueCommentMarkerSnapshot(
    string IssueId,
    string State,
    string? Url,
    bool MarkerFound);
