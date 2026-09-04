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

    // Reads keyed by tracker id take the caller's identifier ("#115") alongside it
    // wherever one is known.
    //
    // WHY. A tracker id is a GitHub GraphQL node id, and GraphQL is the budget this
    // token exhausts - so an id-only read has to be answered on the transport that
    // fails first. The identifier is what REST addresses an issue by, and every
    // caller already carries it next to the id: runs, retries, ledger rows and
    // cache rows all store both. Passing it moves the read onto the primary REST
    // budget; omitting it is still correct and still works, on GraphQL.
    Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(
        TrackerQuery query,
        IReadOnlyList<string> issueIds,
        IReadOnlyDictionary<string, string>? identifiersByIssueId = null,
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
        string? issueIdentifier = null,
        CancellationToken cancellationToken = default);

    Task<string?> PostIssueCommentAsync(
        TrackerQuery query,
        string issueId,
        string body,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NormalizedIssueComment>> FetchIssueCommentsAsync(
        TrackerQuery query,
        string issueId,
        string? issueIdentifier = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByIdsAsync(
        TrackerQuery query,
        IReadOnlyList<string> issueIds,
        IReadOnlyDictionary<string, string>? identifiersByIssueId = null,
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

    // Every open pull request on the repository, whoever opened it. The other
    // PR methods answer questions about a PR this engine created; this one
    // answers what is sitting on the repository waiting for a person, which the
    // engine previously had no way to see.
    Task<IReadOnlyList<OpenPullRequest>> FetchOpenPullRequestsAsync(
        TrackerQuery query,
        int limit,
        CancellationToken cancellationToken = default);

    // Paths changed by the pull request, for the merge policy gate.
    Task<IReadOnlyList<string>> FetchPullRequestFilesAsync(
        TrackerQuery query,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    // Merges the pull request at an EXACT head. The expected head is sent to
    // GitHub so the merge is refused server-side if the branch moved after the
    // policy gate evaluated it. Returns null on success, or a refusal reason.
    Task<string?> MergePullRequestAsync(
        TrackerQuery query,
        int pullRequestNumber,
        string expectedHeadSha,
        string method,
        CancellationToken cancellationToken = default);

    // Removes execution labels from an issue. Called once work reaches a terminal
    // state so the issue stops matching the candidate query; without it a merged
    // issue stays eligible and is re-dispatched forever.
    Task RemoveIssueLabelsAsync(
        TrackerQuery query,
        string issueId,
        IReadOnlyList<string> labelNames,
        CancellationToken cancellationToken = default);
}
