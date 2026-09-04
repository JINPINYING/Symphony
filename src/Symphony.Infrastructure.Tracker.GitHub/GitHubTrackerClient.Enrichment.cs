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
/// </summary>
public sealed partial class GitHubTrackerClient
{
    private const string GraphQlIssueEnrichmentQuery = """
        query($ids: [ID!]!, $includePullRequests: Boolean!) {
          nodes(ids: $ids) {
            ... on Issue {
              id
              linkedBranches(first: 10) {
                nodes {
                  ref {
                    name
                  }
                }
              }
              closedByPullRequestsReferences(first: 10) @include(if: $includePullRequests) {
                nodes {
                  id
                  number
                  state
                  url
                  headRefName
                  baseRefName
                }
              }
              blockedBy(first: 20) {
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

        foreach (var batch in issues.Chunk(50))
        {
            Dictionary<string, JsonElement>? nodesById;
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

    private async Task<Dictionary<string, JsonElement>> FetchEnrichmentNodesAsync(
        string endpoint,
        TrackerQuery query,
        string[] issueIds,
        CancellationToken cancellationToken)
    {
        using var request = BuildGraphQlRequest(
            endpoint,
            query.ApiKey,
            GraphQlIssueEnrichmentQuery,
            new
            {
                ids = issueIds,
                includePullRequests = query.IncludePullRequests
            });

        using var response = await SendAsync(request, cancellationToken);
        using var document = await ParseGraphQlDocumentAsync(response, cancellationToken);

        var dataElement = GetRequiredObject(document.RootElement, "data");
        var nodesById = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
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
                nodesById[id] = node.Clone();
            }
        }

        return nodesById;
    }

    private static NormalizedIssue ApplyEnrichment(NormalizedIssue issue, JsonElement node, bool includePullRequests)
    {
        var pullRequests = ParseClosingPullRequestRefs(node, includePullRequests);
        var branchName = GetLinkedBranchName(node) ?? pullRequests.FirstOrDefault()?.HeadRef;

        return issue with
        {
            BranchName = branchName,
            PullRequests = pullRequests,
            BlockedBy = ParseBlockerRefs(node),
            EnrichmentDegraded = false
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
