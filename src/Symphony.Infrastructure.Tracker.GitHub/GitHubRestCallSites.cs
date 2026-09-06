namespace Symphony.Infrastructure.Tracker.GitHub;

/// <summary>
/// The names every GitHub call is attributed to.
///
/// WHY NAMES AND NOT A TOTAL. On 2026-09-06 the plane's <c>core</c> budget was
/// measured at 7,293 points an hour against a 5,000 limit. The headers said the
/// budget was gone; nothing said what had taken it, so the four fixes that
/// preceded it each moved load from the budget that was visibly hurting onto the
/// one nobody was watching. A call site is the unit a fix can act on.
///
/// These same strings are used by <see cref="GitHubTrackerRestCost"/>, so the
/// modelled hourly figure for a read and the measured one carry the same key and
/// can be put side by side. A test asserts the two sets agree, because a model
/// naming a call site the runtime never records is a model of nothing.
/// </summary>
public static class GitHubRestCallSites
{
    /// <summary>The candidate scan: "is there new work?", per repository.</summary>
    public const string CandidateScan = "rest:candidate_scan";

    /// <summary>The tracked-issue cache refresh, listing issue state and labels.</summary>
    public const string IssueStateListing = "rest:issue_state_listing";

    /// <summary>One issue read by number - state, labels, milestone.</summary>
    public const string IssueByNumber = "rest:issue_by_number";

    /// <summary>An issue's comments, walked to the end.</summary>
    public const string IssueComments = "rest:issue_comments";

    /// <summary>The comment-marker probe an escalation makes before posting.</summary>
    public const string IssueCommentMarker = "rest:issue_comment_marker";

    /// <summary>One pull request by number.</summary>
    public const string PullRequestByNumber = "rest:pull_request";

    /// <summary>The combined commit status for a head SHA.</summary>
    public const string CommitStatus = "rest:commit_status";

    /// <summary>The check runs for a head SHA.</summary>
    public const string CommitCheckRuns = "rest:commit_check_runs";

    /// <summary>The open-pull-request listing.</summary>
    public const string OpenPullRequests = "rest:open_pull_requests";

    /// <summary>The lookup of an open pull request by its head branch.</summary>
    public const string PullRequestByHeadBranch = "rest:pull_request_by_head_branch";

    /// <summary>The files a pull request touches, read by the merge policy gate.</summary>
    public const string PullRequestFiles = "rest:pull_request_files";

    /// <summary>
    /// Every call site above. Used by the model-versus-runtime agreement test;
    /// adding a call site without adding it here fails that test, which is the
    /// point.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        CandidateScan,
        IssueStateListing,
        IssueByNumber,
        IssueComments,
        IssueCommentMarker,
        PullRequestByNumber,
        CommitStatus,
        CommitCheckRuns,
        OpenPullRequests,
        PullRequestByHeadBranch,
        PullRequestFiles
    ];
}

/// <summary>
/// The same, for the calls that are still GraphQL. Kept beside the REST names
/// because the lesson of 2026-09-06 is that the two budgets have to be looked at
/// together or load simply moves between them.
/// </summary>
public static class GitHubGraphQlCallSites
{
    /// <summary>The three issue fields REST cannot express, fetched per scan.</summary>
    public const string Enrichment = "graphql:issue_enrichment";

    /// <summary>An issue's comments, when REST cannot address the issue by number.</summary>
    public const string IssueComments = "graphql:issue_comments";

    /// <summary>The comment-marker probe, when REST cannot address the issue by number.</summary>
    public const string IssueCommentMarker = "graphql:issue_comment_marker";

    /// <summary>Issue state for ids whose issue number the caller could not name.</summary>
    public const string IssueStatesByIds = "graphql:issue_states_by_ids";

    /// <summary>Whole issues read by node id, same residue.</summary>
    public const string IssuesByIds = "graphql:issues_by_ids";

    /// <summary>Mutations: comments, labels, merges, closes.</summary>
    public const string Mutation = "graphql:mutation";

    /// <summary>A GraphQL document an agent asked the plane to execute for it.</summary>
    public const string AgentExtension = "graphql:agent_extension";

    public static IReadOnlyList<string> All { get; } =
    [
        Enrichment,
        IssueComments,
        IssueCommentMarker,
        IssueStatesByIds,
        IssuesByIds,
        Mutation,
        AgentExtension
    ];
}
