namespace Symphony.Core.Models;

/// <summary>
/// An open pull request on the tracked repository, whoever opened it.
///
/// Distinct from <see cref="PullRequestStatus"/>, which answers "what is the
/// exact head and CI state of the PR this run created" for the merge gate. This
/// answers a different question the engine could not previously ask at all:
/// what is sitting on the repository waiting for a person.
///
/// The status page needed it because a green pull request awaiting a merge
/// decision is the single most common way work waits on the owner, and it was
/// invisible - the page reported an empty queue as "nothing needs you" while
/// several PRs sat open.
///
/// <paramref name="ChecksState"/> is GitHub's statusCheckRollup ("SUCCESS",
/// "PENDING", "FAILURE", ...) or null when the PR has no checks.
/// </summary>
public sealed record OpenPullRequest(
    int Number,
    string Title,
    string Url,
    string? Author,
    bool IsDraft,
    string? ChecksState,
    string? Mergeable,
    DateTimeOffset UpdatedAtUtc,
    // "owner/repo". Trailing with a default because this record is deserialized
    // from snapshots written before multi-repository tracking; empty means the
    // repository that was the only one at the time. Needed because "PR #122" is
    // unique only within a repository.
    string Repository = "",
    // The head branch. Trailing and defaulted for the same snapshot-compatibility
    // reason as Repository. It is what tells a pull request the plane opened
    // ("symphony/115") from one a person opened, which decides whether an
    // untracked green PR is a decision waiting on the owner or a fault where the
    // pipeline dropped its own work.
    string? HeadRefName = null);
