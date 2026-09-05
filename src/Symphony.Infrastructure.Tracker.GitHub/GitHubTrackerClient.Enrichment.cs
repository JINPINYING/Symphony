using System.Text.Json;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Tracker.GitHub;

/// <summary>
/// The three issue fields REST cannot express, fetched over GraphQL and allowed
/// to fail.
///
/// WHY THIS IS SEPARATE. <c>linkedBranches</c>, <c>blockedBy</c> and
/// <c>closedByPullRequestsReferences</c> have no <c>/repos/...</c> equivalent, so
/// they stay on the budget that keeps being exhausted. What changes is the
/// consequence: they enrich a view rather than decide whether the plane can work,
/// so when GraphQL refuses, the scan returns the issues it already read over REST
/// and marks them <see cref="NormalizedIssue.EnrichmentDegraded"/>. A GraphQL
/// exhaustion costs detail, not dispatch.
///
/// WHY THE PAGE SIZES ARE VARIABLES. This is now the plane's only recurring
/// GraphQL read, so it is the only one whose page sizes are charged sixty times
/// an hour per repository - and GitHub charges what a query REQUESTS, multiplied
/// down the nesting, whether or not the nodes exist. Asking for ten branches when
/// exactly one is ever read was 500 of the 2,050 nodes this query used to cost.
/// The narrow sizes below are what the plane consumes; the wide ones exist for
/// the rare issue that has more than the narrow page holds, which the query
/// detects for itself through <c>totalCount</c> and re-reads in full. Cheap in
/// the common case, complete in the uncommon one - rather than cheap and quietly
/// wrong, which is what a smaller page with no <c>totalCount</c> would be.
/// </summary>
public sealed partial class GitHubTrackerClient
{
    /// <summary>
    /// Linked branches requested per issue. One, because exactly one is consumed:
    /// <c>GetLinkedBranchName</c> returns the first branch it finds and ignores
    /// the rest. There is no <c>totalCount</c> on this connection on purpose -
    /// additional branches are not truncated data, they are data the plane has
    /// decided not to look at, and re-reading to fetch nodes nobody reads is the
    /// exact behaviour this change exists to remove.
    /// </summary>
    private const int NarrowLinkedBranchPage = 1;

    /// <summary>
    /// Blockers and closing pull requests requested per issue on the first pass.
    /// Both are consumed IN FULL - the blocker rule refuses to dispatch while any
    /// blocker is open - so these are not a cap on what is read, only on what the
    /// first request pays for. <c>totalCount</c> says whether the page was short
    /// of the whole, and a short page is re-read wide.
    /// </summary>
    private const int NarrowConnectionPage = 5;

    /// <summary>
    /// The re-read page size. A hundred is GitHub's own per-connection maximum, so
    /// a second pass that still reports truncation means the issue genuinely has
    /// more than a hundred blockers or closing pull requests - at which point the
    /// answer is "this read is incomplete", not a third guess.
    /// </summary>
    private const int WideConnectionPage = 100;

    private const string GraphQlIssueEnrichmentQuery = """
        query($ids: [ID!]!, $branches: Int!, $connections: Int!, $includePullRequests: Boolean!) {
          nodes(ids: $ids) {
            ... on Issue {
              id
              linkedBranches(first: $branches) {
                nodes {
                  ref {
                    name
                  }
                }
              }
              closedByPullRequestsReferences(first: $connections) @include(if: $includePullRequests) {
                totalCount
                nodes {
                  id
                  number
                  state
                  url
                  headRefName
                  baseRefName
                }
              }
              blockedBy(first: $connections) {
                totalCount
                nodes {
                  id
                  number
                  state
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// The enrichment query text and the page sizes it is issued with, so the cost
    /// model asserts against the query the plane actually sends rather than a copy
    /// of it that can drift.
    /// </summary>
    internal static string EnrichmentQueryText => GraphQlIssueEnrichmentQuery;

    internal static IReadOnlyDictionary<string, int> EnrichmentPageSizes(int issueCount) =>
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ids"] = Math.Max(1, issueCount),
            ["branches"] = NarrowLinkedBranchPage,
            ["connections"] = NarrowConnectionPage
        };

    /// <summary>How many issues one enrichment request covers.</summary>
    internal const int EnrichmentBatchSize = 50;

    /// <summary>
    /// Adds the GraphQL-only fields to REST-read issues. Never throws: a chunk that
    /// fails leaves its issues exactly as REST returned them, flagged degraded.
    /// </summary>
    private async Task<IReadOnlyList<NormalizedIssue>> TryEnrichIssuesAsync(
        TrackerQuery query,
        IReadOnlyList<NormalizedIssue> issues,
        CancellationToken cancellationToken)
    {
        if (issues.Count == 0 || string.IsNullOrWhiteSpace(query.ApiKey))
        {
            return issues;
        }

        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;
        var enriched = new List<NormalizedIssue>(issues.Count);

        foreach (var batch in issues.Chunk(EnrichmentBatchSize))
        {
            Dictionary<string, EnrichmentNode>? nodesById;
            try
            {
                nodesById = await FetchEnrichmentNodesAsync(
                    endpoint,
                    query,
                    batch.Select(issue => issue.Id).ToArray(),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (GitHubTrackerException)
            {
                // A rate limit, a schema drift, a malformed payload - all the same
                // decision. The issues are already read; say the detail is missing
                // rather than losing the scan that produced them.
                nodesById = null;
            }

            foreach (var issue in batch)
            {
                if (nodesById is null)
                {
                    enriched.Add(issue with { EnrichmentDegraded = true });
                    continue;
                }

                enriched.Add(nodesById.TryGetValue(issue.Id, out var node)
                    ? ApplyEnrichment(issue, node, query.IncludePullRequests)
                    : issue);
            }
        }

        return enriched;
    }

    /// <summary>
    /// One enrichment node, and whether the connections on it came back whole.
    /// Carried together because "five blockers" and "five of eleven blockers" are
    /// different answers and the caller has to be able to tell them apart.
    /// </summary>
    private readonly record struct EnrichmentNode(JsonElement Node, bool Truncated);

    private async Task<Dictionary<string, EnrichmentNode>> FetchEnrichmentNodesAsync(
        string endpoint,
        TrackerQuery query,
        string[] issueIds,
        CancellationToken cancellationToken)
    {
        var nodesById = await ReadEnrichmentNodesAsync(
            endpoint, query, issueIds, NarrowConnectionPage, cancellationToken);

        // Whatever came back short of its own totalCount is re-read at GitHub's
        // maximum page. This is the rare path - an issue with more than five
        // blockers - so it is paid for only when it happens, and the whole batch
        // is re-read rather than a per-issue query, because a batch is one request
        // either way and this must not turn one truncation into N requests.
        var truncated = nodesById
            .Where(entry => entry.Value.Truncated)
            .Select(entry => entry.Key)
            .ToArray();

        if (truncated.Length == 0)
        {
            return nodesById;
        }

        Dictionary<string, EnrichmentNode> wide;
        try
        {
            wide = await ReadEnrichmentNodesAsync(
                endpoint, query, truncated, WideConnectionPage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GitHubTrackerException)
        {
            // The re-read is the optional half. Failing it must not discard the
            // narrow pass that succeeded for the rest of the batch: those issues
            // are complete and their detail is worth keeping. The truncated ones
            // stay flagged, so they fall back to what was last known rather than
            // to a partial list presented as a whole one.
            return nodesById;
        }

        foreach (var (issueId, node) in wide)
        {
            nodesById[issueId] = node;
        }

        return nodesById;
    }

    private async Task<Dictionary<string, EnrichmentNode>> ReadEnrichmentNodesAsync(
        string endpoint,
        TrackerQuery query,
        string[] issueIds,
        int connectionPage,
        CancellationToken cancellationToken)
    {
        using var request = BuildGraphQlRequest(
            endpoint,
            query.ApiKey,
            GraphQlIssueEnrichmentQuery,
            new
            {
                ids = issueIds,
                branches = NarrowLinkedBranchPage,
                connections = connectionPage,
                includePullRequests = query.IncludePullRequests
            });

        using var response = await SendAsync(request, cancellationToken);
        using var document = await ParseGraphQlDocumentAsync(response, cancellationToken);

        var dataElement = GetRequiredObject(document.RootElement, "data");
        var nodesById = new Dictionary<string, EnrichmentNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in GetRequiredArray(dataElement, "nodes").EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetOptionalString(node, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                // Cloned because the JsonDocument backing it is disposed on the way
                // out of this method.
                nodesById[id] = new EnrichmentNode(node.Clone(), HasTruncatedConnection(node));
            }
        }

        return nodesById;
    }

    /// <summary>
    /// Whether any connection under this node reported more items than it returned.
    ///
    /// Generic rather than per-field on purpose: it looks for the shape - an object
    /// carrying both <c>totalCount</c> and <c>nodes</c> - so a connection added to
    /// the query later is covered by asking for <c>totalCount</c> and nothing else.
    /// A connection that does not ask for <c>totalCount</c> is declaring that a
    /// short page is not truncation, which is true of <c>linkedBranches</c> and
    /// must be true of anything else that opts out.
    /// </summary>
    private static bool HasTruncatedConnection(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasTruncatedConnection(item))
                {
                    return true;
                }
            }

            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty("totalCount", out var totalCountElement) &&
            totalCountElement.ValueKind == JsonValueKind.Number &&
            totalCountElement.TryGetInt32(out var totalCount) &&
            element.TryGetProperty("nodes", out var nodesElement) &&
            nodesElement.ValueKind == JsonValueKind.Array &&
            totalCount > nodesElement.GetArrayLength())
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (HasTruncatedConnection(property.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static NormalizedIssue ApplyEnrichment(NormalizedIssue issue, EnrichmentNode node, bool includePullRequests)
    {
        var pullRequests = ParseClosingPullRequestRefs(node.Node, includePullRequests);
        var branchName = GetLinkedBranchName(node.Node) ?? pullRequests.FirstOrDefault()?.HeadRef;

        return issue with
        {
            BranchName = branchName,
            PullRequests = pullRequests,
            BlockedBy = ParseBlockerRefs(node.Node),
            // Still short of the whole after a re-read at GitHub's maximum page:
            // more than a hundred blockers or closing pull requests on one issue.
            // Absurd, and therefore exactly the case where a silent partial answer
            // would be believed. Degraded means the caller keeps what it already
            // knew instead of acting on a list it cannot trust - the same treatment
            // an exhausted budget gets, for the same reason.
            EnrichmentDegraded = node.Truncated
        };
    }

    private static IReadOnlyList<PullRequestRef> ParseClosingPullRequestRefs(JsonElement node, bool includePullRequests)
    {
        if (!includePullRequests ||
            !node.TryGetProperty("closedByPullRequestsReferences", out var referencesNode) ||
            referencesNode.ValueKind != JsonValueKind.Object ||
            !referencesNode.TryGetProperty("nodes", out var pullRequestNodes) ||
            pullRequestNodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return pullRequestNodes
            .EnumerateArray()
            .Where(pullRequestNode => pullRequestNode.ValueKind == JsonValueKind.Object)
            .Select(pullRequestNode => new PullRequestRef(
                GetOptionalString(pullRequestNode, "id"),
                GetOptionalInt(pullRequestNode, "number"),
                GetOptionalString(pullRequestNode, "state"),
                GetOptionalString(pullRequestNode, "url"),
                GetOptionalString(pullRequestNode, "headRefName"),
                GetOptionalString(pullRequestNode, "baseRefName")))
            .ToList();
    }

    private static IReadOnlyList<BlockerRef> ParseBlockerRefs(JsonElement node)
    {
        if (!node.TryGetProperty("blockedBy", out var blockedByNode) ||
            blockedByNode.ValueKind != JsonValueKind.Object ||
            !blockedByNode.TryGetProperty("nodes", out var blockerNodes) ||
            blockerNodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return blockerNodes
            .EnumerateArray()
            .Where(blockerNode => blockerNode.ValueKind == JsonValueKind.Object)
            .Select(blockerNode =>
            {
                var number = GetOptionalInt(blockerNode, "number");
                return new BlockerRef(
                    GetOptionalString(blockerNode, "id"),
                    number.HasValue ? $"#{number.Value}" : null,
                    NormalizeState(GetOptionalString(blockerNode, "state")));
            })
            .ToList();
    }
}
