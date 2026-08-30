using Symphony.Core.Models;

namespace Symphony.Core.Abstractions;

public interface ITrackerClient
{
    Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(
        TrackerQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(
        TrackerQuery query,
        IReadOnlyList<string> states,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(
        TrackerQuery query,
        IReadOnlyList<string> issueIds,
        CancellationToken cancellationToken = default);

    Task<GitHubGraphQlExecutionResult> ExecuteGitHubGraphQlAsync(
        TrackerQuery query,
        string graphQlDocument,
        string? variablesJson,
        CancellationToken cancellationToken = default);

    Task<IssueCommentMarkerSnapshot?> FetchIssueCommentMarkerAsync(
        TrackerQuery query,
        string issueId,
        string marker,
        CancellationToken cancellationToken = default);

    Task<string?> PostIssueCommentAsync(
        TrackerQuery query,
        string issueId,
        string body,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NormalizedIssueComment>> FetchIssueCommentsAsync(
        TrackerQuery query,
        string issueId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByIdsAsync(
        TrackerQuery query,
        IReadOnlyList<string> issueIds,
        CancellationToken cancellationToken = default);

    Task CloseIssueAsync(
        TrackerQuery query,
        string issueId,
        CancellationToken cancellationToken = default);

    Task<PullRequestStatus?> FetchPullRequestStatusAsync(
        TrackerQuery query,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    // Finds the newest OPEN pull request whose head branch is exactly
    // headRefName. Symphony creates the branch itself, so this is more reliable
    // than issue->PR linkage, which depends on closing keywords in the PR body
    // and on include_pull_requests being enabled.
    Task<PullRequestStatus?> FetchOpenPullRequestByHeadBranchAsync(
        TrackerQuery query,
        string headRefName,
        CancellationToken cancellationToken = default);
}
