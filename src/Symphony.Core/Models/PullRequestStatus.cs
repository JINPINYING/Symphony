namespace Symphony.Core.Models;

// Live pull-request facts for phase orchestration (M4): the exact head the
// verify/review gates bind to, plus CI rollup state. ChecksState is GitHub's
// statusCheckRollup ("SUCCESS", "PENDING", "FAILURE", ...) or null when the PR
// has no checks.
public sealed record PullRequestStatus(
    int Number,
    string State,
    bool IsDraft,
    string HeadSha,
    string? ChecksState,
    string? Mergeable);
