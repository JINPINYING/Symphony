namespace Symphony.Core.Models;

public sealed record NormalizedIssue(
    string Id,
    string Identifier,
    string Title,
    string? Description,
    int? Priority,
    string State,
    string? BranchName,
    string? Url,
    string? Milestone,
    IReadOnlyList<string> Labels,
    IReadOnlyList<PullRequestRef> PullRequests,
    IReadOnlyList<BlockerRef> BlockedBy,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    // "owner/repo" - which tracked repository this issue came from. Trailing with
    // a default so existing construction sites keep compiling; empty means the
    // primary repository, which is what a single-repository install has always
    // meant. Everything downstream that must talk to GitHub about this issue
    // rebuilds its query from here, because Identifier ("#115") is unique only
    // within a repository and Id alone cannot say which one that is.
    string Repository = "",
    // True when the tracker could read the issue but not the three fields only
    // GraphQL can express - linked branches, blockers, and closing pull request
    // references. The scan itself runs on REST and never blocks on GraphQL, so a
    // GraphQL exhaustion now costs detail rather than dispatch; this flag is how
    // the caller knows the empty lists mean "not fetched" and not "none exist",
    // and keeps what it already knew instead of acting on the absence.
    //
    // LAST and defaulted, like the two above: this record is deserialized from
    // snapshots written before the field existed, and inserting it anywhere else
    // would silently shift every positional construction by one.
    bool EnrichmentDegraded = false);
