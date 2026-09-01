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
    string Repository = "");
