using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Symphony.Core.Abstractions;
using Symphony.Core.Metadata;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Tracker.GitHub;

public sealed partial class GitHubTrackerClient(HttpClient httpClient) : ITrackerClient, IGitHubTrackerClient
{
    // Field selection for the by-ids fallback, which is the only issue read still
    // served by GraphQL - and only when the caller cannot name the issue number
    // REST addresses it by. The candidate scan is REST (GitHubTrackerClient.Rest.cs).
    private const string GraphQlIssueNodeFields = """
                id
                number
                title
                body
                state
                url
                createdAt
                updatedAt
                milestone {
                  title
                  number
                }
                labels(first: 50) {
                  nodes {
                    name
                  }
                }
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
        """;

    private const string GraphQlIssuesByIdsQuery = """
        query($ids: [ID!]!, $includePullRequests: Boolean!) {
          nodes(ids: $ids) {
            ... on Issue {
              repository {
                name
                owner {
                  login
                }
              }
        """ + GraphQlIssueNodeFields + """

            }
          }
        }
        """;

    private const string GraphQlIssueStatesByIdsQuery = """
        query($ids: [ID!]!) {
          nodes(ids: $ids) {
            ... on Issue {
              id
              state
              labels(first: 50) {
                nodes {
                  name
                }
              }
              repository {
                name
                owner {
                  login
                }
              }
            }
          }
        }
        """;

    public async Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(
        TrackerQuery query,
        CancellationToken cancellationToken = default)
    {
        return await FetchIssuesInternalAsync(
            query,
            states: query.ActiveStates,
            applyCandidateFilters: true,
            cancellationToken);
    }

    public async Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(
        TrackerQuery query,
        IReadOnlyList<string> states,
        CancellationToken cancellationToken = default)
    {
        if (states.Count == 0)
        {
            return [];
        }

        return await FetchIssuesInternalAsync(
            query,
            states,
            applyCandidateFilters: false,
            cancellationToken);
    }

    public async Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(
        TrackerQuery query,
        IReadOnlyList<string> issueIds,
        IReadOnlyDictionary<string, string>? identifiersByIssueId = null,
        CancellationToken cancellationToken = default)
    {
        if (issueIds.Count == 0)
        {
            return [];
        }

        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;
        var orderedIds = issueIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var statesById = new Dictionary<string, IssueStateSnapshot>(StringComparer.OrdinalIgnoreCase);

        // This refresh is what clears a resolved escalation and what stops a run
        // whose issue was closed under it. When it was GraphQL-only, a GraphQL
        // exhaustion froze both: the attention list kept offering decisions the
        // owner had already made. Every id the caller can also name by number is
        // answered from REST, on the budget that was never touched.
        var restResolvable = orderedIds
            .Where(issueId => identifiersByIssueId is not null &&
                              identifiersByIssueId.TryGetValue(issueId, out var identifier) &&
                              ParseIssueNumber(identifier) is not null)
            .ToList();
        var unresolved = orderedIds.Except(restResolvable, StringComparer.OrdinalIgnoreCase).ToList();

        if (restResolvable.Count > MaxIndividualIssueReads)
        {
            // One listing beats one GET each once the set is large - which is
            // exactly the tracked-issue cache refresh, asking about every issue it
            // has ever seen on every tick.
            foreach (var (issueId, snapshot) in
                     await FetchIssueStatesByListingRestAsync(query, restResolvable, cancellationToken))
            {
                statesById[issueId] = snapshot;
            }
        }
        else
        {
            foreach (var issueId in restResolvable)
            {
                var number = ParseIssueNumber(identifiersByIssueId![issueId])!.Value;
                var snapshot = await FetchIssueStateByNumberRestAsync(query, issueId, number, cancellationToken);
                if (snapshot is not null)
                {
                    statesById[issueId] = snapshot;
                }
            }
        }

        foreach (var issueIdBatch in unresolved.Chunk(100))
        {
            using var request = BuildGraphQlRequest(
                endpoint,
                query.ApiKey,
                GraphQlIssueStatesByIdsQuery,
                new
                {
                    ids = issueIdBatch
                });

            using var response = await SendAsync(request, cancellationToken);
            using var document = await ParseGraphQlDocumentAsync(response, cancellationToken);

            var dataElement = GetRequiredObject(document.RootElement, "data");
            var nodesElement = GetRequiredArray(dataElement, "nodes");

            foreach (var issueNode in nodesElement.EnumerateArray())
            {
                if (issueNode.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!issueNode.TryGetProperty("repository", out var repositoryNode) ||
                    repositoryNode.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var owner = repositoryNode.TryGetProperty("owner", out var ownerNode)
                    ? GetOptionalString(ownerNode, "login")
                    : null;
                var repo = GetOptionalString(repositoryNode, "name");

                if (!string.Equals(owner, query.Owner, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(repo, query.Repo, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var issueId = GetOptionalString(issueNode, "id");
                if (string.IsNullOrWhiteSpace(issueId))
                {
                    continue;
                }

                var normalizedState = NormalizeState(GetOptionalString(issueNode, "state")) ?? "Open";
                statesById[issueId] = new IssueStateSnapshot(issueId, normalizedState, ExtractLabelNames(issueNode));
            }
        }

        var result = new List<IssueStateSnapshot>(statesById.Count);
        foreach (var issueId in orderedIds)
        {
            if (statesById.TryGetValue(issueId, out var state))
            {
                result.Add(state);
            }
        }

        return result;
    }

    private const string GraphQlIssueCommentMarkerQuery = """
        query($id: ID!, $after: String) {
          node(id: $id) {
            ... on Issue {
              id
              state
              url
              comments(first: 100, after: $after) {
                pageInfo {
                  hasNextPage
                  endCursor
                }
                nodes {
                  body
                }
              }
            }
          }
        }
        """;

    private const string GraphQlAddIssueCommentMutation = """
        mutation($subjectId: ID!, $body: String!) {
          addComment(input: { subjectId: $subjectId, body: $body }) {
            commentEdge {
              node {
                url
              }
            }
          }
        }
        """;

    // Upper bound on comment pages scanned for the idempotency marker. At 100
    // comments per page this covers issues far beyond anything Symphony manages.
    // Hitting the cap reports the marker as not found: the caller's durable
    // posted-flag remains the primary dedupe, so the worst case is one duplicate
    // comment on a pathological thread — never a silently swallowed escalation.
    private const int MaxCommentMarkerPages = 20;

    public async Task<IssueCommentMarkerSnapshot?> FetchIssueCommentMarkerAsync(
        TrackerQuery query,
        string issueId,
        string marker,
        string? issueIdentifier = null,
        CancellationToken cancellationToken = default)
    {
        if (ParseIssueNumber(issueIdentifier) is { } issueNumber)
        {
            return await FetchIssueCommentMarkerRestAsync(query, issueId, issueNumber, marker, cancellationToken);
        }

        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;

        string? state = null;
        string? url = null;
        string? after = null;
        var pagesScanned = 0;

        while (true)
        {
            using var request = BuildGraphQlRequest(
                endpoint,
                query.ApiKey,
                GraphQlIssueCommentMarkerQuery,
                new
                {
                    id = issueId,
                    after
                });

            using var response = await SendAsync(request, cancellationToken);
            using var document = await ParseGraphQlDocumentAsync(response, cancellationToken);

            var dataElement = GetRequiredObject(document.RootElement, "data");
            if (!dataElement.TryGetProperty("node", out var nodeElement) ||
                nodeElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var nodeIssueId = GetOptionalString(nodeElement, "id");
            if (string.IsNullOrWhiteSpace(nodeIssueId))
            {
                // The node resolved to something that is not an Issue.
                return null;
            }

            state ??= NormalizeState(GetOptionalString(nodeElement, "state")) ?? "Open";
            url ??= GetOptionalString(nodeElement, "url");

            var commentsElement = GetRequiredObject(nodeElement, "comments");
            var commentNodes = GetRequiredArray(commentsElement, "nodes");
            foreach (var commentNode in commentNodes.EnumerateArray())
            {
                if (commentNode.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var body = GetOptionalString(commentNode, "body");
                if (body is not null && body.Contains(marker, StringComparison.Ordinal))
                {
                    return new IssueCommentMarkerSnapshot(issueId, state, url, MarkerFound: true);
                }
            }

            var pageInfo = GetRequiredObject(commentsElement, "pageInfo");
            pagesScanned++;
            if (!GetRequiredBoolean(pageInfo, "hasNextPage"))
            {
                return new IssueCommentMarkerSnapshot(issueId, state, url, MarkerFound: false);
            }

            if (pagesScanned >= MaxCommentMarkerPages)
            {
                return new IssueCommentMarkerSnapshot(issueId, state, url, MarkerFound: false);
            }

            after = GetOptionalString(pageInfo, "endCursor");
            if (string.IsNullOrWhiteSpace(after))
            {
                return new IssueCommentMarkerSnapshot(issueId, state, url, MarkerFound: false);
            }
        }
    }

    public async Task<string?> PostIssueCommentAsync(
        TrackerQuery query,
        string issueId,
        string body,
        CancellationToken cancellationToken = default)
    {
        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;

        using var request = BuildGraphQlRequest(
            endpoint,
            query.ApiKey,
            GraphQlAddIssueCommentMutation,
            new
            {
                subjectId = issueId,
                body
            });

        using var response = await SendAsync(request, cancellationToken);
        using var document = await ParseGraphQlDocumentAsync(response, cancellationToken);

        var dataElement = GetRequiredObject(document.RootElement, "data");
        var addCommentElement = GetRequiredObject(dataElement, "addComment");
        if (!addCommentElement.TryGetProperty("commentEdge", out var edgeElement) ||
            edgeElement.ValueKind != JsonValueKind.Object ||
            !edgeElement.TryGetProperty("node", out var commentNode) ||
            commentNode.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetOptionalString(commentNode, "url");
    }

    private const string GraphQlIssueCommentsQuery = """
        query($id: ID!, $after: String) {
          node(id: $id) {
            ... on Issue {
              id
              comments(first: 100, after: $after) {
                pageInfo {
                  hasNextPage
                  endCursor
                }
                nodes {
                  id
                  body
                  createdAt
                  authorAssociation
                  author {
                    login
                  }
                }
              }
            }
          }
        }
        """;

    private const string GraphQlCloseIssueMutation = """
        mutation($issueId: ID!) {
          closeIssue(input: { issueId: $issueId }) {
            issue {
              id
              state
            }
          }
        }
        """;

    // Upper bound on comment pages fetched for directive processing; matches the
    // marker-scan cap rationale — far beyond any issue Symphony manages.
    private const int MaxCommentPages = 30;

    public async Task<IReadOnlyList<NormalizedIssueComment>> FetchIssueCommentsAsync(
        TrackerQuery query,
        string issueId,
        string? issueIdentifier = null,
        CancellationToken cancellationToken = default)
    {
        // Comments carry the command-center directives. A directive the plane
        // cannot read is a directive that does not run, so this read belongs on
        // the budget that does not run out.
        if (ParseIssueNumber(issueIdentifier) is { } issueNumber)
        {
            return await FetchIssueCommentsRestAsync(query, issueNumber, cancellationToken);
        }

        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;
        var comments = new List<NormalizedIssueComment>();
        string? after = null;
        var pages = 0;

        while (true)
        {
            using var request = BuildGraphQlRequest(
                endpoint,
                query.ApiKey,
                GraphQlIssueCommentsQuery,
                new
                {
                    id = issueId,
                    after
                });

            using var response = await SendAsync(request, cancellationToken);
            using var document = await ParseGraphQlDocumentAsync(response, cancellationToken);

            var dataElement = GetRequiredObject(document.RootElement, "data");
            if (!dataElement.TryGetProperty("node", out var nodeElement) ||
                nodeElement.ValueKind != JsonValueKind.Object ||
                string.IsNullOrWhiteSpace(GetOptionalString(nodeElement, "id")))
            {
                return comments;
            }

            var commentsElement = GetRequiredObject(nodeElement, "comments");
            foreach (var commentNode in GetRequiredArray(commentsElement, "nodes").EnumerateArray())
            {
                if (commentNode.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var commentId = GetOptionalString(commentNode, "id");
                if (string.IsNullOrWhiteSpace(commentId))
                {
                    continue;
                }

                var authorLogin = commentNode.TryGetProperty("author", out var authorNode) &&
                    authorNode.ValueKind == JsonValueKind.Object
                        ? GetOptionalString(authorNode, "login")
                        : null;

                comments.Add(new NormalizedIssueComment(
                    commentId,
                    GetOptionalString(commentNode, "body") ?? string.Empty,
                    authorLogin,
                    GetOptionalString(commentNode, "authorAssociation"),
                    ParseDateTimeOffset(commentNode, "createdAt")));
            }

            var pageInfo = GetRequiredObject(commentsElement, "pageInfo");
            pages++;
            if (!GetRequiredBoolean(pageInfo, "hasNextPage") || pages >= MaxCommentPages)
            {
                return comments;
            }

            after = GetOptionalString(pageInfo, "endCursor");
            if (string.IsNullOrWhiteSpace(after))
            {
                return comments;
            }
        }
    }

    public async Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByIdsAsync(
        TrackerQuery query,
        IReadOnlyList<string> issueIds,
        IReadOnlyDictionary<string, string>? identifiersByIssueId = null,
        CancellationToken cancellationToken = default)
    {
        if (issueIds.Count == 0)
        {
            return [];
        }

        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;
        var orderedIds = issueIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var issuesById = new Dictionary<string, NormalizedIssue>(StringComparer.OrdinalIgnoreCase);

        var unresolved = new List<string>(orderedIds.Count);
        foreach (var issueId in orderedIds)
        {
            var number = identifiersByIssueId is not null &&
                         identifiersByIssueId.TryGetValue(issueId, out var identifier)
                ? ParseIssueNumber(identifier)
                : null;

            if (number is null)
            {
                unresolved.Add(issueId);
                continue;
            }

            var issue = await FetchIssueByNumberRestAsync(query, number.Value, cancellationToken);
            if (issue is not null && string.Equals(issue.Id, issueId, StringComparison.OrdinalIgnoreCase))
            {
                issuesById[issue.Id] = (await TryEnrichIssuesAsync(query, [issue], cancellationToken))[0];
            }
        }

        foreach (var issueIdBatch in unresolved.Chunk(50))
        {
            using var request = BuildGraphQlRequest(
                endpoint,
                query.ApiKey,
                GraphQlIssuesByIdsQuery,
                new
                {
                    ids = issueIdBatch,
                    includePullRequests = query.IncludePullRequests
                });

            using var response = await SendAsync(request, cancellationToken);
            using var document = await ParseGraphQlDocumentAsync(response, cancellationToken);

            var dataElement = GetRequiredObject(document.RootElement, "data");
            foreach (var issueNode in GetRequiredArray(dataElement, "nodes").EnumerateArray())
            {
                if (issueNode.ValueKind != JsonValueKind.Object ||
                    string.IsNullOrWhiteSpace(GetOptionalString(issueNode, "id")))
                {
                    continue;
                }

                if (!issueNode.TryGetProperty("repository", out var repositoryNode) ||
                    repositoryNode.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var owner = repositoryNode.TryGetProperty("owner", out var ownerNode)
                    ? GetOptionalString(ownerNode, "login")
                    : null;
                var repo = GetOptionalString(repositoryNode, "name");
                if (!string.Equals(owner, query.Owner, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(repo, query.Repo, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var issue = ParseIssue(issueNode, query.IncludePullRequests, $"{query.Owner}/{query.Repo}");
                issuesById[issue.Id] = issue;
            }
        }

        var result = new List<NormalizedIssue>(issuesById.Count);
        foreach (var issueId in orderedIds)
        {
            if (issuesById.TryGetValue(issueId, out var issue))
            {
                result.Add(issue);
            }
        }

        return result;
    }

    // Pull requests, checks and changed files are all /repos/... reads. They used
    // to be GraphQL, which meant the merge gate, the verify gate and the status
    // page all went dark on the same exhaustion that blinded the candidate scan -
    // one budget, every question. The parsing lives in GitHubTrackerClient.Rest.cs.

    public async Task<PullRequestStatus?> FetchPullRequestStatusAsync(
        TrackerQuery query,
        int pullRequestNumber,
        CancellationToken cancellationToken = default)
    {
        return await FetchPullRequestStatusRestAsync(query, pullRequestNumber, cancellationToken);
    }

    public async Task<PullRequestStatus?> FetchOpenPullRequestByHeadBranchAsync(
        TrackerQuery query,
        string headRefName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(headRefName))
        {
            return null;
        }

        return await FetchOpenPullRequestByHeadBranchRestAsync(query, headRefName, cancellationToken);
    }

    public async Task<IReadOnlyList<OpenPullRequest>> FetchOpenPullRequestsAsync(
        TrackerQuery query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await FetchOpenPullRequestsRestAsync(query, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> FetchPullRequestFilesAsync(
        TrackerQuery query,
        int pullRequestNumber,
        CancellationToken cancellationToken = default)
    {
        return await FetchPullRequestFilesRestAsync(query, pullRequestNumber, cancellationToken);
    }

    private const string GraphQlMergePullRequestMutation = """
        mutation($pullRequestId: ID!, $expectedHeadOid: GitObjectID!, $method: PullRequestMergeMethod!) {
          mergePullRequest(input: { pullRequestId: $pullRequestId, expectedHeadOid: $expectedHeadOid, mergeMethod: $method }) {
            pullRequest { number state merged }
          }
        }
        """;

    private const string GraphQlPullRequestIdQuery = """
        query($owner: String!, $repo: String!, $number: Int!) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) { id }
          }
        }
        """;

    public async Task<string?> MergePullRequestAsync(
        TrackerQuery query,
        int pullRequestNumber,
        string expectedHeadSha,
        string method,
        CancellationToken cancellationToken = default)
    {
        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;

        string? pullRequestId;
        using (var idRequest = BuildGraphQlRequest(
                   endpoint,
                   query.ApiKey,
                   GraphQlPullRequestIdQuery,
                   new { owner = query.Owner, repo = query.Repo, number = pullRequestNumber }))
        using (var idResponse = await SendAsync(idRequest, cancellationToken))
        using (var idDocument = await ParseGraphQlDocumentAsync(idResponse, cancellationToken))
        {
            var dataElement = GetRequiredObject(idDocument.RootElement, "data");
            if (!dataElement.TryGetProperty("repository", out var repositoryNode) ||
                repositoryNode.ValueKind != JsonValueKind.Object ||
                !repositoryNode.TryGetProperty("pullRequest", out var prNode) ||
                prNode.ValueKind != JsonValueKind.Object)
            {
                return "pull request could not be resolved";
            }

            pullRequestId = GetOptionalString(prNode, "id");
        }

        if (string.IsNullOrWhiteSpace(pullRequestId))
        {
            return "pull request id was missing";
        }

        var graphQlMethod = method.ToUpperInvariant() switch
        {
            "MERGE" => "MERGE",
            "REBASE" => "REBASE",
            _ => "SQUASH"
        };

        try
        {
            using var request = BuildGraphQlRequest(
                endpoint,
                query.ApiKey,
                GraphQlMergePullRequestMutation,
                new { pullRequestId, expectedHeadOid = expectedHeadSha, method = graphQlMethod });

            using var response = await SendAsync(request, cancellationToken);
            using var document = await ParseGraphQlDocumentAsync(response, cancellationToken);

            var dataElement = GetRequiredObject(document.RootElement, "data");
            GetRequiredObject(dataElement, "mergePullRequest");
            return null;
        }
        catch (GitHubTrackerException ex)
        {
            // Includes the expected-head mismatch refusal, branch protection, and
            // any other server-side veto. The caller escalates rather than retries.
            return ex.Message;
        }
    }

    private const string GraphQlRepositoryLabelsQuery = """
        query($owner: String!, $repo: String!) {
          repository(owner: $owner, name: $repo) {
            labels(first: 100) {
              nodes { id name }
            }
          }
        }
        """;

    private const string GraphQlRemoveLabelsMutation = """
        mutation($labelableId: ID!, $labelIds: [ID!]!) {
          removeLabelsFromLabelable(input: { labelableId: $labelableId, labelIds: $labelIds }) {
            clientMutationId
          }
        }
        """;

    public async Task RemoveIssueLabelsAsync(
        TrackerQuery query,
        string issueId,
        IReadOnlyList<string> labelNames,
        CancellationToken cancellationToken = default)
    {
        if (labelNames.Count == 0)
        {
            return;
        }

        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;

        // Resolve names to node ids. A name the repository does not define is
        // simply absent from the result, which is the correct no-op.
        var labelIds = new List<string>();
        using (var labelsRequest = BuildGraphQlRequest(
                   endpoint,
                   query.ApiKey,
                   GraphQlRepositoryLabelsQuery,
                   new { owner = query.Owner, repo = query.Repo }))
        using (var labelsResponse = await SendAsync(labelsRequest, cancellationToken))
        using (var labelsDocument = await ParseGraphQlDocumentAsync(labelsResponse, cancellationToken))
        {
            var dataElement = GetRequiredObject(labelsDocument.RootElement, "data");
            if (!dataElement.TryGetProperty("repository", out var repositoryNode) ||
                repositoryNode.ValueKind != JsonValueKind.Object ||
                !repositoryNode.TryGetProperty("labels", out var labelsNode) ||
                labelsNode.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var labelNode in GetRequiredArray(labelsNode, "nodes").EnumerateArray())
            {
                var name = GetOptionalString(labelNode, "name");
                var id = GetOptionalString(labelNode, "id");
                if (name is null || id is null)
                {
                    continue;
                }

                if (labelNames.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)))
                {
                    labelIds.Add(id);
                }
            }
        }

        if (labelIds.Count == 0)
        {
            return;
        }

        using var request = BuildGraphQlRequest(
            endpoint,
            query.ApiKey,
            GraphQlRemoveLabelsMutation,
            new { labelableId = issueId, labelIds });

        using var response = await SendAsync(request, cancellationToken);
        using var document = await ParseGraphQlDocumentAsync(response, cancellationToken);
        GetRequiredObject(document.RootElement, "data");
    }

    public async Task CloseIssueAsync(
        TrackerQuery query,
        string issueId,
        CancellationToken cancellationToken = default)
    {
        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;

        using var request = BuildGraphQlRequest(
            endpoint,
            query.ApiKey,
            GraphQlCloseIssueMutation,
            new
            {
                issueId
            });

        using var response = await SendAsync(request, cancellationToken);
        using var document = await ParseGraphQlDocumentAsync(response, cancellationToken);

        var dataElement = GetRequiredObject(document.RootElement, "data");
        GetRequiredObject(dataElement, "closeIssue");
    }


    public async Task<GitHubGraphQlExecutionResult> ExecuteGitHubGraphQlAsync(
        TrackerQuery query,
        string graphQlDocument,
        string? variablesJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.ApiKey))
        {
            return new GitHubGraphQlExecutionResult(
                Success: false,
                PayloadJson: "{\"error\":\"missing_tracker_auth\"}",
                ErrorCode: "missing_tracker_auth",
                ErrorMessage: "GitHub tracker auth is required.");
        }

        if (string.IsNullOrWhiteSpace(graphQlDocument))
        {
            return new GitHubGraphQlExecutionResult(
                Success: false,
                PayloadJson: "{\"error\":\"invalid_graphql_document\"}",
                ErrorCode: "invalid_graphql_document",
                ErrorMessage: "GraphQL query must be non-empty.");
        }

        if (!ContainsSingleGraphQlOperation(graphQlDocument))
        {
            return new GitHubGraphQlExecutionResult(
                Success: false,
                PayloadJson: "{\"error\":\"invalid_graphql_document\"}",
                ErrorCode: "invalid_graphql_document",
                ErrorMessage: "GraphQL document must contain exactly one operation.");
        }

        JsonNode? variablesNode = null;
        if (!string.IsNullOrWhiteSpace(variablesJson))
        {
            try
            {
                variablesNode = JsonNode.Parse(variablesJson);
            }
            catch (JsonException ex)
            {
                return new GitHubGraphQlExecutionResult(
                    Success: false,
                    PayloadJson: "{\"error\":\"invalid_graphql_variables\"}",
                    ErrorCode: "invalid_graphql_variables",
                    ErrorMessage: ex.Message);
            }

            if (variablesNode is not JsonObject)
            {
                return new GitHubGraphQlExecutionResult(
                    Success: false,
                    PayloadJson: "{\"error\":\"invalid_graphql_variables\"}",
                    ErrorCode: "invalid_graphql_variables",
                    ErrorMessage: "GraphQL variables must be a JSON object.");
            }
        }

        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;

        try
        {
            using var request = BuildGraphQlRequest(
                endpoint,
                query.ApiKey,
                graphQlDocument,
                variablesNode);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var payloadJson = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new GitHubGraphQlExecutionResult(
                    Success: false,
                    PayloadJson: string.IsNullOrWhiteSpace(payloadJson)
                        ? $"{{\"error\":\"github_api_status\",\"status\":{(int)response.StatusCode}}}"
                        : payloadJson,
                    ErrorCode: "github_api_status",
                    ErrorMessage: $"GitHub GraphQL returned HTTP {(int)response.StatusCode}.");
            }

            using var payloadDocument = JsonDocument.Parse(payloadJson);
            var success = !(payloadDocument.RootElement.TryGetProperty("errors", out var errorsElement) &&
                            errorsElement.ValueKind == JsonValueKind.Array &&
                            errorsElement.GetArrayLength() > 0);

            return new GitHubGraphQlExecutionResult(
                Success: success,
                PayloadJson: payloadJson,
                ErrorCode: success ? null : "github_graphql_errors",
                ErrorMessage: success ? null : "GitHub GraphQL returned errors.");
        }
        catch (JsonException ex)
        {
            return new GitHubGraphQlExecutionResult(
                Success: false,
                PayloadJson: "{\"error\":\"github_unknown_payload\"}",
                ErrorCode: "github_unknown_payload",
                ErrorMessage: ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GitHubGraphQlExecutionResult(
                Success: false,
                PayloadJson: "{\"error\":\"github_api_request\"}",
                ErrorCode: "github_api_request",
                ErrorMessage: "GitHub GraphQL request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new GitHubGraphQlExecutionResult(
                Success: false,
                PayloadJson: "{\"error\":\"github_api_request\"}",
                ErrorCode: "github_api_request",
                ErrorMessage: ex.Message);
        }
    }

    private static IReadOnlyList<string> ExtractLabelNames(JsonElement issueNode) =>
        issueNode.TryGetProperty("labels", out var labelsNode) &&
        labelsNode.ValueKind == JsonValueKind.Object &&
        labelsNode.TryGetProperty("nodes", out var labelNodes) &&
        labelNodes.ValueKind == JsonValueKind.Array
            ? labelNodes
                .EnumerateArray()
                .Select(node => GetOptionalString(node, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    private static NormalizedIssue ParseIssue(JsonElement issueNode, bool includePullRequests, string repository)
    {
        var labels = issueNode.TryGetProperty("labels", out var labelsNode) &&
                     labelsNode.ValueKind == JsonValueKind.Object &&
                     labelsNode.TryGetProperty("nodes", out var labelNodes) &&
                     labelNodes.ValueKind == JsonValueKind.Array
            ? labelNodes
                .EnumerateArray()
                .Select(node => GetOptionalString(node, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        var blockedBy = issueNode.TryGetProperty("blockedBy", out var blockedByNode) &&
                        blockedByNode.ValueKind == JsonValueKind.Object &&
                        blockedByNode.TryGetProperty("nodes", out var blockerNodes) &&
                        blockerNodes.ValueKind == JsonValueKind.Array
            ? blockerNodes
                .EnumerateArray()
                .Select(node =>
                {
                    var number = GetOptionalInt(node, "number");
                    return new BlockerRef(
                        GetOptionalString(node, "id"),
                        number.HasValue ? $"#{number.Value}" : null,
                        NormalizeState(GetOptionalString(node, "state")));
                })
                .ToList()
            : [];

        var pullRequests = includePullRequests &&
                           issueNode.TryGetProperty("closedByPullRequestsReferences", out var pullRequestReferencesNode) &&
                           pullRequestReferencesNode.ValueKind != JsonValueKind.Null &&
                           pullRequestReferencesNode.TryGetProperty("nodes", out var pullRequestNodes) &&
                           pullRequestNodes.ValueKind == JsonValueKind.Array
            ? issueNode
                .GetProperty("closedByPullRequestsReferences")
                .GetProperty("nodes")
                .EnumerateArray()
                .Select(node => new PullRequestRef(
                    GetOptionalString(node, "id"),
                    GetOptionalInt(node, "number"),
                    GetOptionalString(node, "state"),
                    GetOptionalString(node, "url"),
                    GetOptionalString(node, "headRefName"),
                    GetOptionalString(node, "baseRefName")))
                .ToList()
            : [];

        var milestoneTitle = issueNode.TryGetProperty("milestone", out var milestoneNode) &&
                             milestoneNode.ValueKind != JsonValueKind.Null &&
                             milestoneNode.TryGetProperty("title", out var milestoneTitleNode)
            ? milestoneTitleNode.GetString()
            : null;

        var normalizedState = NormalizeState(GetOptionalString(issueNode, "state")) ?? "Open";
        var number = GetOptionalInt(issueNode, "number");
        var identifier = number is null ? GetOptionalString(issueNode, "id") ?? "unknown" : $"#{number.Value}";
        var branchName = GetLinkedBranchName(issueNode) ?? pullRequests.FirstOrDefault()?.HeadRef;

        return new NormalizedIssue(
            Id: GetOptionalString(issueNode, "id") ?? Guid.NewGuid().ToString("N"),
            Identifier: identifier,
            Title: GetOptionalString(issueNode, "title") ?? "(untitled issue)",
            Description: GetOptionalString(issueNode, "body"),
            Priority: InferPriority(labels),
            State: normalizedState,
            BranchName: branchName,
            Url: GetOptionalString(issueNode, "url"),
            Milestone: milestoneTitle,
            Labels: labels,
            PullRequests: pullRequests,
            BlockedBy: blockedBy,
            CreatedAt: ParseDateTimeOffset(issueNode, "createdAt"),
            UpdatedAt: ParseDateTimeOffset(issueNode, "updatedAt"),
            Repository: repository);
    }

    private static IReadOnlyList<string> BuildIssueStates(IReadOnlyList<string> activeStates)
    {
        if (activeStates.Count == 0)
        {
            return ["OPEN"];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in activeStates)
        {
            if (IssueStateMatcher.IsClosedState(state))
            {
                result.Add("CLOSED");
            }
            else
            {
                result.Add("OPEN");
            }
        }

        return result.Count == 0 ? ["OPEN"] : result.ToList();
    }

    private static bool MatchesMilestone(string? milestoneTitle, JsonElement issueNode, string? configuredMilestone)
    {
        if (string.IsNullOrWhiteSpace(configuredMilestone))
        {
            return true;
        }

        if (string.Equals(milestoneTitle, configuredMilestone, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!issueNode.TryGetProperty("milestone", out var milestoneNode) || milestoneNode.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        var milestoneNumber = milestoneNode.TryGetProperty("number", out var numberNode)
            ? numberNode.GetInt32().ToString()
            : null;

        return string.Equals(milestoneNumber, configuredMilestone, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesLabels(IReadOnlyList<string> issueLabels, IReadOnlyList<string> requestedLabels)
    {
        if (requestedLabels.Count == 0)
        {
            return true;
        }

        var issueLabelSet = new HashSet<string>(issueLabels, StringComparer.OrdinalIgnoreCase);
        return requestedLabels.All(label => issueLabelSet.Contains(label));
    }

    private static bool MatchesActiveState(string issueState, IReadOnlyList<string> configuredStates)
    {
        return IssueStateMatcher.MatchesConfiguredActiveState(issueState, configuredStates);
    }

    private static string? GetLinkedBranchName(JsonElement issueNode)
    {
        if (!issueNode.TryGetProperty("linkedBranches", out var linkedBranchesNode) ||
            linkedBranchesNode.ValueKind != JsonValueKind.Object ||
            !linkedBranchesNode.TryGetProperty("nodes", out var branchNodes) ||
            branchNodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var node in branchNodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object ||
                !node.TryGetProperty("ref", out var refNode) ||
                refNode.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var branchName = GetOptionalString(refNode, "name");
            if (!string.IsNullOrWhiteSpace(branchName))
            {
                return branchName;
            }
        }

        return null;
    }

    private static int? InferPriority(IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            var match = PriorityRegex().Match(label);
            if (match.Success && int.TryParse(match.Groups["priority"].Value, out var priority))
            {
                return priority;
            }
        }

        return null;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;
    }

    private static HttpRequestMessage BuildGraphQlRequest(
        string endpoint,
        string apiKey,
        string graphQlQuery,
        object? variables)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(SymphonyProductInfo.Name, SymphonyProductInfo.UserAgentVersion));
        request.Content = JsonContent.Create(new
        {
            query = graphQlQuery,
            variables
        });

        return request;
    }

    private static bool ContainsSingleGraphQlOperation(string graphQlDocument)
    {
        var stripped = StripGraphQlCommentsAndStrings(graphQlDocument);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return false;
        }

        var operationCount = 0;
        var depth = 0;
        var awaitingExplicitOperationBody = false;

        for (var index = 0; index < stripped.Length; index++)
        {
            var current = stripped[index];
            switch (current)
            {
                case '{':
                    if (depth == 0)
                    {
                        if (awaitingExplicitOperationBody)
                        {
                            awaitingExplicitOperationBody = false;
                        }
                        else
                        {
                            operationCount++;
                        }
                    }

                    depth++;
                    break;
                case '}':
                    depth = Math.Max(depth - 1, 0);
                    break;
                default:
                    if (depth != 0 || !char.IsLetter(current))
                    {
                        break;
                    }

                    var start = index;
                    while (index < stripped.Length && (char.IsLetter(stripped[index]) || stripped[index] == '_'))
                    {
                        index++;
                    }

                    var token = stripped[start..index];
                    if (token is "query" or "mutation" or "subscription")
                    {
                        operationCount++;
                        awaitingExplicitOperationBody = true;
                    }

                    index--;
                    break;
            }

            if (operationCount > 1)
            {
                return false;
            }
        }

        return operationCount == 1;
    }

    private static string StripGraphQlCommentsAndStrings(string input)
    {
        var chars = new List<char>(input.Length);
        var inString = false;
        var inComment = false;
        var escapeNext = false;

        foreach (var current in input)
        {
            if (inComment)
            {
                if (current is '\r' or '\n')
                {
                    inComment = false;
                    chars.Add(current);
                }

                continue;
            }

            if (inString)
            {
                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }

                if (current == '\\')
                {
                    escapeNext = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '#')
            {
                inComment = true;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            chars.Add(current);
        }

        return new string(chars.ToArray());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // GraphQL usually reports exhaustion as a 200 with a RATE_LIMITED
                // error (handled in ParseGraphQlDocumentAsync), but the secondary
                // limit answers 403/429 like REST does. Both must be named a rate
                // limit, or the caller retries into a clock it cannot move.
                var statusCode = response.StatusCode;
                var rateLimited = IsRateLimitedResponse(response, string.Empty);
                var retryAfter = rateLimited ? ReadRetryAfter(response) : null;
                response.Dispose();

                throw rateLimited
                    ? new GitHubTrackerException(
                        GitHubTrackerException.RateLimitedCode,
                        $"GitHub GraphQL: rate limit exceeded (HTTP {(int)statusCode}).",
                        retryAfter: retryAfter,
                        statusCode: (int)statusCode)
                    : new GitHubTrackerException(
                        "github_api_status",
                        $"GitHub GraphQL returned HTTP {(int)statusCode}.",
                        statusCode: (int)statusCode);
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubTrackerException("github_api_request", "GitHub GraphQL request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubTrackerException("github_api_request", "GitHub GraphQL request failed.", ex);
        }
    }

    private static async Task<JsonDocument> ParseGraphQlDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);

            if (document.RootElement.TryGetProperty("errors", out var errorsElement) &&
                errorsElement.ValueKind == JsonValueKind.Array &&
                errorsElement.GetArrayLength() > 0)
            {
                // Carry what GitHub actually said. "GitHub GraphQL returned errors"
                // is the same empty diagnostic as the exception type alone: it sent
                // a rate-limit exhaustion and a malformed query down one
                // indistinguishable path, and the plane retried into the limit
                // every tick because nothing told it which it had hit.
                var first = errorsElement[0];
                var type = first.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString()
                    : null;
                var message = first.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                document.Dispose();

                var rateLimited = string.Equals(type, "RATE_LIMITED", StringComparison.OrdinalIgnoreCase)
                    || (message?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ?? false);

                throw new GitHubTrackerException(
                    rateLimited ? GitHubTrackerException.RateLimitedCode : "github_graphql_errors",
                    string.IsNullOrWhiteSpace(message)
                        ? "GitHub GraphQL returned errors."
                        : $"GitHub GraphQL: {message}",
                    // A GraphQL rate limit answers 200 and puts the clock in the
                    // response headers. Carry it: the pause that follows should be
                    // the wait GitHub named, not a constant that guesses at it.
                    retryAfter: rateLimited ? ReadRetryAfter(response) : null);
            }

            return document;
        }
        catch (GitHubTrackerException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new GitHubTrackerException("github_unknown_payload", "GitHub GraphQL payload was not valid JSON.", ex);
        }
    }

    private static JsonElement GetRequiredObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            throw new GitHubTrackerException(
                "github_unknown_payload",
                $"GitHub GraphQL payload is missing object property '{propertyName}'.");
        }

        return property;
    }

    private static JsonElement GetRequiredArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            throw new GitHubTrackerException(
                "github_unknown_payload",
                $"GitHub GraphQL payload is missing array property '{propertyName}'.");
        }

        return property;
    }

    private static bool GetRequiredBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new GitHubTrackerException(
                "github_unknown_payload",
                $"GitHub GraphQL payload is missing boolean property '{propertyName}'.");
        }

        return property.GetBoolean();
    }

    private static string? NormalizeState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        return IssueStateMatcher.IsClosedState(state) ? "Closed" : "Open";
    }

    private static DateTimeOffset? ParseDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(property.GetString(), out var parsed)
            ? parsed
            : null;
    }

    [GeneratedRegex(@"(?:^|[\s:_-])p(?:riority)?(?<priority>[1-4])(?:$|[\s:_-])", RegexOptions.IgnoreCase)]
    private static partial Regex PriorityRegex();
}

// Note: trivial source changes intentionally shift this assembly's hash; Windows
// Smart App Control on the build machine scores binaries per hash and has blocked
// stale hashes before (0x800711C7). See docs in the M3 PR description.

// build-stamp: m4b-1788130263
