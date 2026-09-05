using Symphony.Core.Configuration;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Tracker.GitHub;

/// <summary>
/// How much GraphQL budget the plane spends in an hour of ordinary running,
/// computed from the query text this adapter actually sends.
///
/// WHY IT IS A TYPE AND NOT A COMMENT. The same arithmetic error has been
/// rediscovered three times - twice on 2026-09-01 and again on 2026-09-05 - and
/// every time the discovery was the plane going blind for the rest of the hour.
/// Each rediscovery followed a change that moved one number: a third repository,
/// an added field, a page size. Nothing recomputed the product, because the
/// product lived in nobody's head and in no file. It lives here now, it is
/// derived from the query constants rather than from a copy of them, and a build
/// asserts it.
///
/// WHAT IT DELIBERATELY DOES NOT CLAIM. A model is not a measurement. The call
/// rates below are assumptions, each one written down beside the number it
/// produces so a reader can dispute the assumption rather than the total. The
/// measurement is the other half of this change: every response's
/// <c>x-ratelimit-*</c> headers are recorded, shown on the status page, and raise
/// an attention item at 80% of the budget. The model catches an unaffordable
/// query before it ships; the headers catch an assumption that was wrong.
/// </summary>
public static class GitHubTrackerGraphQlCost
{
    /// <summary>
    /// The load the model is computed against. Every field is an assumption about
    /// how much work the plane is doing, not a limit imposed on it.
    /// </summary>
    /// <param name="RepositoryCount">Repositories watched. Three today.</param>
    /// <param name="CandidateIssuesPerRepository">
    /// Issues a candidate scan returns per repository. The scan is label-filtered
    /// server-side, so this is the executable backlog rather than the repository.
    /// </param>
    /// <param name="UnresolvedIssueIdsPerRefresh">
    /// Ids in a state refresh that GraphQL has to answer because the caller could
    /// not name the issue number REST addresses it by. Every caller in the tick
    /// path supplies the number, so this is the residue rather than the workload.
    /// </param>
    /// <param name="WritesPerHour">
    /// Mutations and the small reads that serve them: comments posted, labels
    /// added and removed, merges attempted, comment markers probed by node id.
    /// </param>
    public sealed record Load(
        int RepositoryCount,
        int CandidateIssuesPerRepository,
        int UnresolvedIssueIdsPerRefresh,
        int WritesPerHour);

    /// <summary>
    /// The load the build asserts against: three repositories, a backlog larger
    /// than the plane has ever carried, a state-refresh residue an order of
    /// magnitude above the zero the code paths predict, and a write rate well
    /// above anything observed. Deliberately pessimistic - a ceiling proved under
    /// optimistic assumptions proves nothing.
    /// </summary>
    public static readonly Load PessimisticSteadyState = new(
        RepositoryCount: TrackerReadCadence.ModelledRepositoryCount,
        CandidateIssuesPerRepository: 25,
        UnresolvedIssueIdsPerRefresh: 5,
        WritesPerHour: 200);

    public static GraphQlHourlyCost Model(Load load)
    {
        ArgumentNullException.ThrowIfNull(load);

        var repositories = Math.Max(1, load.RepositoryCount);
        var scansPerHour = TrackerReadCadence.CallsPerHour(TrackerReadCadence.CandidateScan);
        var refreshesPerHour = TrackerReadCadence.CallsPerHour(TrackerReadCadence.TrackedIssueRefresh);

        var reads = new List<GraphQlReadCost>();

        // 1. Candidate-scan enrichment. The scan itself is REST and costs no
        //    GraphQL points at all; this is the three fields REST cannot express,
        //    fetched for the issues the scan returned.
        if (load.CandidateIssuesPerRepository > 0)
        {
            var perBatch = Math.Min(load.CandidateIssuesPerRepository, GitHubTrackerClient.EnrichmentBatchSize);
            var batches = (int)Math.Ceiling(
                load.CandidateIssuesPerRepository / (double)GitHubTrackerClient.EnrichmentBatchSize);

            reads.Add(new GraphQlReadCost(
                "issue enrichment (linkedBranches, blockedBy, closedByPullRequestsReferences)",
                GraphQlCost.CountNodes(
                    GitHubTrackerClient.EnrichmentQueryText,
                    GitHubTrackerClient.EnrichmentPageSizes(perBatch)),
                scansPerHour * repositories * batches,
                $"one batch of {perBatch} per candidate scan, {repositories} repositories, " +
                $"scan every {TrackerReadCadence.CandidateScan.TotalSeconds:0}s"));
        }

        // 2. The state-refresh residue. Ids the caller could not name by number,
        //    which is the only reason this read is not REST.
        if (load.UnresolvedIssueIdsPerRefresh > 0)
        {
            var perBatch = Math.Min(load.UnresolvedIssueIdsPerRefresh, GitHubTrackerClient.IssueStatesBatchSize);

            reads.Add(new GraphQlReadCost(
                "issue state refresh (ids with no known issue number)",
                GraphQlCost.CountNodes(
                    GitHubTrackerClient.IssueStatesQueryText,
                    GitHubTrackerClient.IssueStatesPageSizes(perBatch)),
                refreshesPerHour * repositories,
                $"{perBatch} unresolved ids per refresh, {repositories} repositories, " +
                $"refresh every {TrackerReadCadence.TrackedIssueRefresh.TotalSeconds:0}s - " +
                "every tick-path caller supplies the number, so this is residue, not workload"));

            reads.Add(new GraphQlReadCost(
                "issue read by id (ids with no known issue number)",
                GraphQlCost.CountNodes(
                    GitHubTrackerClient.IssuesByIdsQueryText,
                    GitHubTrackerClient.IssuesByIdsPageSizes(
                        Math.Min(load.UnresolvedIssueIdsPerRefresh, GitHubTrackerClient.IssuesByIdsBatchSize))),
                refreshesPerHour * repositories,
                "same residue, on the phase and directive paths"));
        }

        // 3. Writes. A mutation names one subject and requests no pages, so it is
        //    the one-point minimum; the label mutations resolve names through a
        //    100-label repository read first, which is also one point.
        if (load.WritesPerHour > 0)
        {
            reads.Add(new GraphQlReadCost(
                "mutations and the reads that serve them",
                Nodes: 100,
                load.WritesPerHour,
                "comments, label changes, merges: one point each, none of them paged"));
        }

        return new GraphQlHourlyCost(reads);
    }
}
