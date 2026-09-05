using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Symphony.Core.Metadata;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Tracker.GitHub;

/// <summary>
/// The tracker's READ transport.
///
/// WHY THIS EXISTS. Every read used to be a GraphQL call, and GraphQL is the
/// budget this token exhausts: on 2026-09-03 the tracker went blind twice on
/// "API rate limit already exceeded" while the REST budget sat at 4999/5000 all
/// day, untouched. The component whose failure stops every dispatch was the one
/// most exposed to the limit that keeps being hit, and its retries were
/// themselves GraphQL calls - once throttled it spent its recovery budget
/// confirming it was throttled.
///
/// So the reads that decide whether the plane can work at all - the candidate
/// scan, issue state, pull requests, checks and comments - are answered from
/// <c>/repos/...</c> against the primary 5000/hour budget. GraphQL is left with
/// the writes and with the three issue fields REST cannot express
/// (<c>linkedBranches</c>, <c>blockedBy</c>, <c>closedByPullRequestsReferences</c>),
/// which are enrichment: they make a view better, and their absence must never
/// stop a dispatch. See <c>TryEnrichIssuesAsync</c>.
/// </summary>
public sealed partial class GitHubTrackerClient
{
    private const string RestAcceptHeader = "application/vnd.github+json";
    private const string RestApiVersionHeaderName = "X-GitHub-Api-Version";
    private const string RestApiVersion = "2022-11-28";

    // Upper bound on pages walked for one listing. At 100 records per page this is
    // five thousand, far beyond any repository Symphony tracks; it exists so a
    // malformed Link header cannot spin a tick forever.
    private const int MaxRestPages = 50;

    /// <summary>
    /// The REST root that pairs with a configured GraphQL endpoint.
    ///
    /// Derived rather than configured because the two are the same install: a
    /// second setting is a second thing to get wrong, and an install pointed at a
    /// GitHub Enterprise GraphQL endpoint with a github.com REST root would read
    /// one repository while writing to another.
    /// </summary>
    internal static string RestBaseUrl(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return "https://api.github.com";
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/graphql".Length];
        }

        // GitHub Enterprise Server serves GraphQL at /api/graphql and REST at
        // /api/v3; github.com serves REST from the root of api.github.com.
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            path += "/v3";
        }

        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    private static string RepositoryUrl(TrackerQuery query) =>
        $"{RestBaseUrl(query.Endpoint)}/repos/{Uri.EscapeDataString(query.Owner)}/{Uri.EscapeDataString(query.Repo)}";

    private static HttpRequestMessage BuildRestRequest(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(RestAcceptHeader));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(SymphonyProductInfo.Name, SymphonyProductInfo.UserAgentVersion));
        request.Headers.TryAddWithoutValidation(RestApiVersionHeaderName, RestApiVersion);
        return request;
    }

    /// <summary>
    /// Sends a REST read and maps a refusal onto the same exception vocabulary the
    /// GraphQL path uses, so callers need not know which transport answered. A rate
    /// limit is named as one and carries the wait GitHub asked for.
    /// </summary>
    private async Task<HttpResponseMessage> SendRestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubTrackerException("github_api_request", "GitHub REST request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubTrackerException("github_api_request", "GitHub REST request failed.", ex);
        }

        // Before anything can return or throw, and before the response is disposed
        // on the refusal path below. The reading GitHub attached to a refusal is
        // the most valuable one there is - it is the only one that says how the
        // budget was spent - and a record taken only on success would lose exactly
        // the readings worth having.
        RecordRateLimit(response);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        // Read the body BEFORE disposing. GitHub says "API rate limit exceeded" and
        // "You have exceeded a secondary rate limit" in the payload, and a 403 with
        // the body thrown away is indistinguishable from a permissions refusal -
        // which is the confusion that had the plane retrying into the limit.
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            body = string.Empty;
        }

        var statusCode = response.StatusCode;
        var rateLimited = IsRateLimitedResponse(response, body);
        var retryAfter = rateLimited ? ReadRetryAfter(response) : null;
        response.Dispose();

        if (rateLimited)
        {
            throw new GitHubTrackerException(
                GitHubTrackerException.RateLimitedCode,
                $"GitHub REST: {DescribeRateLimit(body, (int)statusCode)}",
                retryAfter: retryAfter);
        }

        throw new GitHubTrackerException(
            "github_api_status",
            $"GitHub REST returned HTTP {(int)statusCode}.",
            statusCode: (int)statusCode);
    }

    private static bool IsRateLimitedResponse(HttpResponseMessage response, string body)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return false;
        }

        // The primary limit answers 403 with x-ratelimit-remaining: 0; the secondary
        // one answers 403 or 429 with Retry-After and says so in the body. A 403 that
        // is neither is a real refusal - a bad token, a missing scope - and must not
        // be waited out as though a clock would clear it.
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remainingValues) &&
            remainingValues.FirstOrDefault() is { } remaining &&
            int.TryParse(remaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRemaining) &&
            parsedRemaining <= 0)
        {
            return true;
        }

        if (response.Headers.RetryAfter is not null)
        {
            return true;
        }

        return body.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records what GitHub said about the budget on this response.
    ///
    /// WHY IT IS TAKEN HERE. The plane exhausted the 5,000-point hourly GraphQL
    /// budget on 2026-09-05 and the first sign of it was the candidate scan going
    /// blind. <c>gh api rate_limit</c> read 5,000 remaining at the same moment: its
    /// top-level <c>rate</c> block is the core budget, and the GraphQL figure is at
    /// <c>.resources.graphql</c>. The headers are on the calls the plane is already
    /// making, they name their own resource, and they cost nothing.
    ///
    /// Silent when the headers are absent or unreadable: a proxy that strips them
    /// is not a reason to fail a read, and an invented reading is worse than none
    /// because the panel it feeds would present it as measurement.
    /// </summary>
    private void RecordRateLimit(HttpResponseMessage response)
    {
        if (rateLimitObserver is null)
        {
            return;
        }

        var limit = ReadHeaderInt(response, "x-ratelimit-limit");
        var used = ReadHeaderInt(response, "x-ratelimit-used");
        var remaining = ReadHeaderInt(response, "x-ratelimit-remaining");
        if (limit is null || used is null || remaining is null)
        {
            return;
        }

        var resource = ReadHeaderString(response, "x-ratelimit-resource");
        var reset = ReadHeaderLong(response, "x-ratelimit-reset");

        try
        {
            rateLimitObserver.Record(new GitHubRateLimitReading(
                string.IsNullOrWhiteSpace(resource) ? "unknown" : resource.Trim().ToLowerInvariant(),
                limit.Value,
                used.Value,
                remaining.Value,
                reset is null ? null : DateTimeOffset.FromUnixTimeSeconds(reset.Value),
                DateTimeOffset.UtcNow));
        }
        catch (Exception)
        {
            // Telemetry taken on the way past a real read. Losing the telemetry
            // must never lose the read.
        }
    }

    private static string? ReadHeaderString(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static int? ReadHeaderInt(HttpResponseMessage response, string name) =>
        int.TryParse(ReadHeaderString(response, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static long? ReadHeaderLong(HttpResponseMessage response, string name) =>
        long.TryParse(ReadHeaderString(response, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string DescribeRateLimit(string body, int statusCode)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var message = document.RootElement.ValueKind == JsonValueKind.Object
                    ? GetOptionalString(document.RootElement, "message")
                    : null;
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
            catch (JsonException)
            {
                // A non-JSON body from a proxy is still evidence; fall through.
            }
        }

        return $"rate limit exceeded (HTTP {statusCode}).";
    }

    /// <summary>
    /// The wait GitHub named, from <c>Retry-After</c> or from the distance to
    /// <c>x-ratelimit-reset</c>. Null when it named none - the caller then uses its
    /// own backoff rather than presenting an invented number as GitHub's.
    /// </summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                return wait;
            }
        }

        if (response.Headers.TryGetValues("x-ratelimit-reset", out var resetValues) &&
            resetValues.FirstOrDefault() is { } reset &&
            long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetEpochSeconds))
        {
            var wait = DateTimeOffset.FromUnixTimeSeconds(resetEpochSeconds) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                return wait;
            }
        }

        return null;
    }

    private static async Task<JsonDocument> ParseRestDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new GitHubTrackerException("github_unknown_payload", "GitHub REST payload was not valid JSON.", ex);
        }
    }

    /// <summary>
    /// The next page URL from a <c>Link</c> header, or null at the end of a listing.
    /// A header that advertises a next page it cannot name is a pagination integrity
    /// error, not an end of list: stopping there silently is how a scan reports half
    /// a repository as all of it.
    /// </summary>
    private static string? ReadNextPageUrl(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var linkValues))
        {
            return null;
        }

        foreach (var header in linkValues)
        {
            // Matched rather than split on ',': a Link header holds several
            // relations separated by commas, and the URLs inside it may contain
            // commas of their own (a two-label candidate query echoes `labels=a,b`).
            // Splitting on the separator would tear those in half and report the
            // fragment as the next page.
            var match = LinkHeaderNextRegex().Match(header);
            if (!match.Success)
            {
                continue;
            }

            var url = match.Groups["url"].Value.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new GitHubTrackerException(
                    "github_missing_end_cursor",
                    "GitHub REST pagination advertised a next page with no URL.");
            }

            return url;
        }

        return null;
    }

    [GeneratedRegex("""<(?<url>[^>]*)>\s*;\s*rel\s*=\s*"?next"?""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkHeaderNextRegex();

    /// <summary>
    /// Walks a REST listing to its end, handing every element to <paramref name="onElement"/>.
    /// </summary>
    private async Task ReadRestListAsync(
        TrackerQuery query,
        string firstPageUrl,
        Action<JsonElement> onElement,
        CancellationToken cancellationToken)
    {
        var url = firstPageUrl;
        for (var page = 0; page < MaxRestPages && url is not null; page++)
        {
            using var request = BuildRestRequest(HttpMethod.Get, url, query.ApiKey);
            using var response = await SendRestAsync(request, cancellationToken);
            var next = ReadNextPageUrl(response);
            using var document = await ParseRestDocumentAsync(response, cancellationToken);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubTrackerException(
                    "github_unknown_payload",
                    "GitHub REST listing payload was not an array.");
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                onElement(element);
            }

            url = next;
        }
    }

    /// <summary>
    /// Reads one REST object, treating 404 as "it is not there" rather than as a
    /// fault: a pull request that was never opened and a tracker that cannot be
    /// reached must not look the same to a caller that fails closed on the second.
    /// </summary>
    private async Task<JsonDocument?> ReadRestObjectAsync(
        TrackerQuery query,
        string url,
        CancellationToken cancellationToken)
    {
        using var request = BuildRestRequest(HttpMethod.Get, url, query.ApiKey);
        HttpResponseMessage response;
        try
        {
            response = await SendRestAsync(request, cancellationToken);
        }
        catch (GitHubTrackerException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return null;
        }

        using (response)
        {
            return await ParseRestDocumentAsync(response, cancellationToken);
        }
    }

    // ---------------------------------------------------------------------
    // Issues
    // ---------------------------------------------------------------------

    /// <summary>
    /// The candidate scan, and the state-filtered scan that startup cleanup uses.
    /// REST, unconditionally: this is the read that decides whether the plane can
    /// work at all, and it must not share a budget with anything optional.
    /// </summary>
    private async Task<IReadOnlyList<NormalizedIssue>> FetchIssuesInternalAsync(
        TrackerQuery query,
        IReadOnlyList<string> states,
        bool applyCandidateFilters,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 50 : query.PageSize, 1, 100);
        var parameters = new List<string>
        {
            $"state={RestIssueStateFilter(states)}",
            $"per_page={pageSize.ToString(CultureInfo.InvariantCulture)}",
            "sort=created",
            "direction=asc"
        };

        if (applyCandidateFilters && query.Labels.Count != 0)
        {
            // REST applies label filters with AND semantics, which is what
            // MatchesLabels asserts locally; the server-side filter is the same
            // rule, applied before the bytes are sent.
            parameters.Add($"labels={Uri.EscapeDataString(string.Join(',', query.Labels))}");
        }

        var repository = $"{query.Owner}/{query.Repo}";
        var issues = new List<NormalizedIssue>();

        await ReadRestListAsync(
            query,
            $"{RepositoryUrl(query)}/issues?{string.Join('&', parameters)}",
            element =>
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                // /issues returns pull requests too, and the spec excludes PR-only
                // work items from dispatch. A pull request is the element carrying a
                // "pull_request" object; nothing else does.
                if (element.TryGetProperty("pull_request", out var pullRequestMarker) &&
                    pullRequestMarker.ValueKind == JsonValueKind.Object)
                {
                    return;
                }

                var issue = ParseRestIssue(element, repository);

                if (applyCandidateFilters && !MatchesRestMilestone(element, query.Milestone))
                {
                    return;
                }

                if (applyCandidateFilters && !MatchesLabels(issue.Labels, query.Labels))
                {
                    return;
                }

                if (!MatchesActiveState(issue.State, states))
                {
                    return;
                }

                issues.Add(issue);
            },
            cancellationToken);

        return await TryEnrichIssuesAsync(query, issues, cancellationToken);
    }

    /// <summary>
    /// Maps the configured active states onto the one filter REST accepts. The
    /// per-issue check in <c>MatchesActiveState</c> still runs: this only decides
    /// how much GitHub has to send.
    /// </summary>
    private static string RestIssueStateFilter(IReadOnlyList<string> states)
    {
        var wanted = BuildIssueStates(states);
        var open = wanted.Any(state => string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase));
        var closed = wanted.Any(state => string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase));

        return open && closed ? "all" : closed ? "closed" : "open";
    }

    private static NormalizedIssue ParseRestIssue(JsonElement issueNode, string repository)
    {
        var labels = ParseRestLabelNames(issueNode);
        var number = GetOptionalInt(issueNode, "number");
        var nodeId = GetOptionalString(issueNode, "node_id");

        var milestoneTitle = issueNode.TryGetProperty("milestone", out var milestoneNode) &&
                             milestoneNode.ValueKind == JsonValueKind.Object
            ? GetOptionalString(milestoneNode, "title")
            : null;

        return new NormalizedIssue(
            // The GraphQL node id, which REST returns as node_id. Symphony's writes
            // are still GraphQL mutations keyed by it, and it is the primary key of
            // the issue cache: reading over REST must not change what an issue is
            // called anywhere else.
            Id: string.IsNullOrWhiteSpace(nodeId) ? $"{repository}#{number}" : nodeId,
            Identifier: number is null ? nodeId ?? "unknown" : $"#{number.Value}",
            Title: GetOptionalString(issueNode, "title") ?? "(untitled issue)",
            Description: GetOptionalString(issueNode, "body"),
            Priority: InferPriority(labels),
            State: NormalizeState(GetOptionalString(issueNode, "state")) ?? "Open",
            // linkedBranches has no REST equivalent; enrichment supplies it when
            // GraphQL is answering, and null is what this field already meant on
            // every issue with no linked branch.
            BranchName: null,
            Url: GetOptionalString(issueNode, "html_url"),
            Milestone: milestoneTitle,
            Labels: labels,
            PullRequests: [],
            BlockedBy: [],
            CreatedAt: ParseDateTimeOffset(issueNode, "created_at"),
            UpdatedAt: ParseDateTimeOffset(issueNode, "updated_at"),
            Repository: repository);
    }

    private static IReadOnlyList<string> ParseRestLabelNames(JsonElement issueNode)
    {
        if (!issueNode.TryGetProperty("labels", out var labelsNode) ||
            labelsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return labelsNode
            .EnumerateArray()
            // REST returns label objects, but the same endpoint may return bare
            // strings for a caller that asked for them; accept both rather than
            // silently dropping every label on an install that does.
            .Select(node => node.ValueKind switch
            {
                JsonValueKind.Object => GetOptionalString(node, "name"),
                JsonValueKind.String => node.GetString(),
                _ => null
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesRestMilestone(JsonElement issueNode, string? configuredMilestone)
    {
        if (string.IsNullOrWhiteSpace(configuredMilestone))
        {
            return true;
        }

        if (!issueNode.TryGetProperty("milestone", out var milestoneNode) ||
            milestoneNode.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (string.Equals(GetOptionalString(milestoneNode, "title"), configuredMilestone, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var milestoneNumber = GetOptionalInt(milestoneNode, "number");
        return milestoneNumber is not null &&
               string.Equals(
                   milestoneNumber.Value.ToString(CultureInfo.InvariantCulture),
                   configuredMilestone,
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task<NormalizedIssue?> FetchIssueByNumberRestAsync(
        TrackerQuery query,
        int number,
        CancellationToken cancellationToken)
    {
        using var document = await ReadRestObjectAsync(
            query,
            $"{RepositoryUrl(query)}/issues/{number.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ParseRestIssue(document.RootElement, $"{query.Owner}/{query.Repo}");
    }

    /// <summary>
    /// Above this many ids, one REST GET per issue costs more than listing the
    /// repository does.
    ///
    /// It matters because the tracked-issue cache refresh asks about EVERY issue it
    /// has ever seen, every tick. GraphQL answered that in one call per hundred; a
    /// GET per issue would turn a fifty-issue cache on a fifteen-second tick into
    /// twelve thousand requests an hour - re-creating, on the primary budget, the
    /// exhaustion this whole change exists to stop.
    /// </summary>
    private const int MaxIndividualIssueReads = 10;

    /// <summary>
    /// Resolves many issue ids from a repository listing rather than one GET each.
    /// Walks newest-updated first and stops as soon as every id is accounted for,
    /// so the usual case - a cache whose issues have not changed - costs one page.
    /// </summary>
    private async Task<Dictionary<string, IssueStateSnapshot>> FetchIssueStatesByListingRestAsync(
        TrackerQuery query,
        IReadOnlyCollection<string> issueIds,
        CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(issueIds, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, IssueStateSnapshot>(StringComparer.OrdinalIgnoreCase);

        var url = $"{RepositoryUrl(query)}/issues?state=all&per_page=100&sort=updated&direction=desc";
        for (var page = 0; page < MaxRestPages && url is not null && found.Count < wanted.Count; page++)
        {
            using var request = BuildRestRequest(HttpMethod.Get, url, query.ApiKey);
            using var response = await SendRestAsync(request, cancellationToken);
            var next = ReadNextPageUrl(response);
            using var document = await ParseRestDocumentAsync(response, cancellationToken);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubTrackerException(
                    "github_unknown_payload",
                    "GitHub REST listing payload was not an array.");
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var nodeId = GetOptionalString(element, "node_id");
                if (string.IsNullOrWhiteSpace(nodeId) || !wanted.Contains(nodeId))
                {
                    continue;
                }

                found[nodeId] = new IssueStateSnapshot(
                    nodeId,
                    NormalizeState(GetOptionalString(element, "state")) ?? "Open",
                    ParseRestLabelNames(element));
            }

            url = next;
        }

        return found;
    }

    private async Task<IssueStateSnapshot?> FetchIssueStateByNumberRestAsync(
        TrackerQuery query,
        string issueId,
        int number,
        CancellationToken cancellationToken)
    {
        using var document = await ReadRestObjectAsync(
            query,
            $"{RepositoryUrl(query)}/issues/{number.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Trust the id the caller already holds only when GitHub agrees the record
        // is the same one; a number that has been reused by a different node id
        // would otherwise refresh the wrong cache row.
        var nodeId = GetOptionalString(document.RootElement, "node_id");
        if (!string.IsNullOrWhiteSpace(nodeId) &&
            !string.Equals(nodeId, issueId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new IssueStateSnapshot(
            issueId,
            NormalizeState(GetOptionalString(document.RootElement, "state")) ?? "Open",
            ParseRestLabelNames(document.RootElement));
    }

    // ---------------------------------------------------------------------
    // Comments
    // ---------------------------------------------------------------------

    private async Task<IReadOnlyList<NormalizedIssueComment>> FetchIssueCommentsRestAsync(
        TrackerQuery query,
        int number,
        CancellationToken cancellationToken)
    {
        var comments = new List<NormalizedIssueComment>();

        await ReadRestListAsync(
            query,
            $"{RepositoryUrl(query)}/issues/{number.ToString(CultureInfo.InvariantCulture)}/comments?per_page=100",
            element =>
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                // node_id, not the numeric id: the directive log keys off comment ids
                // written when this read was GraphQL, and a comment must not change
                // its name because the transport did - a consumed directive would
                // become unconsumed and run a second time.
                var commentId = GetOptionalString(element, "node_id");
                if (string.IsNullOrWhiteSpace(commentId))
                {
                    return;
                }

                var authorLogin = element.TryGetProperty("user", out var userNode) &&
                                  userNode.ValueKind == JsonValueKind.Object
                    ? GetOptionalString(userNode, "login")
                    : null;

                comments.Add(new NormalizedIssueComment(
                    commentId,
                    GetOptionalString(element, "body") ?? string.Empty,
                    authorLogin,
                    GetOptionalString(element, "author_association"),
                    ParseDateTimeOffset(element, "created_at")));
            },
            cancellationToken);

        return comments;
    }

    private async Task<IssueCommentMarkerSnapshot?> FetchIssueCommentMarkerRestAsync(
        TrackerQuery query,
        string issueId,
        int number,
        string marker,
        CancellationToken cancellationToken)
    {
        string state;
        string? url;
        using (var document = await ReadRestObjectAsync(
                   query,
                   $"{RepositoryUrl(query)}/issues/{number.ToString(CultureInfo.InvariantCulture)}",
                   cancellationToken))
        {
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            state = NormalizeState(GetOptionalString(document.RootElement, "state")) ?? "Open";
            url = GetOptionalString(document.RootElement, "html_url");
        }

        var found = false;
        await ReadRestListAsync(
            query,
            $"{RepositoryUrl(query)}/issues/{number.ToString(CultureInfo.InvariantCulture)}/comments?per_page=100",
            element =>
            {
                if (found || element.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                var body = GetOptionalString(element, "body");
                if (body is not null && body.Contains(marker, StringComparison.Ordinal))
                {
                    found = true;
                }
            },
            cancellationToken);

        return new IssueCommentMarkerSnapshot(issueId, state, url, found);
    }

    // ---------------------------------------------------------------------
    // Pull requests and checks
    // ---------------------------------------------------------------------

    private async Task<PullRequestStatus?> FetchPullRequestStatusRestAsync(
        TrackerQuery query,
        int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        using var document = await ReadRestObjectAsync(
            query,
            $"{RepositoryUrl(query)}/pulls/{pullRequestNumber.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return await BuildPullRequestStatusAsync(query, document.RootElement, cancellationToken);
    }

    private async Task<PullRequestStatus?> BuildPullRequestStatusAsync(
        TrackerQuery query,
        JsonElement prNode,
        CancellationToken cancellationToken)
    {
        var number = GetOptionalInt(prNode, "number");
        var headSha = prNode.TryGetProperty("head", out var headNode) && headNode.ValueKind == JsonValueKind.Object
            ? GetOptionalString(headNode, "sha")
            : null;
        var state = NormalizeRestPullRequestState(prNode);

        if (number is null || string.IsNullOrWhiteSpace(headSha) || state is null)
        {
            return null;
        }

        return new PullRequestStatus(
            number.Value,
            state,
            prNode.TryGetProperty("draft", out var draftNode) && draftNode.ValueKind == JsonValueKind.True,
            headSha,
            await FetchChecksRollupRestAsync(query, headSha, cancellationToken),
            NormalizeRestMergeable(prNode));
    }

    /// <summary>
    /// REST reports state as open/closed with a separate merged flag; GraphQL
    /// reported OPEN/CLOSED/MERGED, and the phase machine treats MERGED as terminal
    /// and distinct. Preserve the three-value vocabulary the callers already read.
    /// </summary>
    private static string? NormalizeRestPullRequestState(JsonElement prNode)
    {
        var state = GetOptionalString(prNode, "state");
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        var merged = (prNode.TryGetProperty("merged", out var mergedNode) && mergedNode.ValueKind == JsonValueKind.True)
                     || (prNode.TryGetProperty("merged_at", out var mergedAtNode) && mergedAtNode.ValueKind == JsonValueKind.String);

        return merged ? "MERGED" : state.ToUpperInvariant();
    }

    /// <summary>
    /// REST reports mergeability as true/false/null; the merge gate reads GraphQL's
    /// MERGEABLE/CONFLICTING/UNKNOWN, and treats UNKNOWN as "do not know, do not
    /// block". Null must map to UNKNOWN and not to a refusal.
    /// </summary>
    private static string? NormalizeRestMergeable(JsonElement prNode)
    {
        if (!prNode.TryGetProperty("mergeable", out var mergeableNode))
        {
            return null;
        }

        return mergeableNode.ValueKind switch
        {
            JsonValueKind.True => "MERGEABLE",
            JsonValueKind.False => "CONFLICTING",
            _ => "UNKNOWN"
        };
    }

    /// <summary>
    /// GitHub's statusCheckRollup, rebuilt from the two REST surfaces it combines:
    /// the legacy combined commit status and check runs. Null when a head carries
    /// neither, which is what "no CI configured" already meant to the verify gate.
    /// </summary>
    private async Task<string?> FetchChecksRollupRestAsync(
        TrackerQuery query,
        string headSha,
        CancellationToken cancellationToken)
    {
        var pending = false;
        var failed = false;
        var any = false;

        using (var statusDocument = await ReadRestObjectAsync(
                   query,
                   $"{RepositoryUrl(query)}/commits/{Uri.EscapeDataString(headSha)}/status",
                   cancellationToken))
        {
            if (statusDocument is not null && statusDocument.RootElement.ValueKind == JsonValueKind.Object)
            {
                var totalCount = GetOptionalInt(statusDocument.RootElement, "total_count") ?? 0;
                if (totalCount > 0)
                {
                    any = true;
                    switch (GetOptionalString(statusDocument.RootElement, "state")?.ToLowerInvariant())
                    {
                        case "pending":
                            pending = true;
                            break;
                        case "failure":
                        case "error":
                            failed = true;
                            break;
                    }
                }
            }
        }

        using (var checkRunsDocument = await ReadRestObjectAsync(
                   query,
                   $"{RepositoryUrl(query)}/commits/{Uri.EscapeDataString(headSha)}/check-runs?per_page=100",
                   cancellationToken))
        {
            if (checkRunsDocument is not null &&
                checkRunsDocument.RootElement.ValueKind == JsonValueKind.Object &&
                checkRunsDocument.RootElement.TryGetProperty("check_runs", out var runsNode) &&
                runsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var run in runsNode.EnumerateArray())
                {
                    if (run.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    any = true;
                    if (!string.Equals(GetOptionalString(run, "status"), "completed", StringComparison.OrdinalIgnoreCase))
                    {
                        pending = true;
                        continue;
                    }

                    switch (GetOptionalString(run, "conclusion")?.ToLowerInvariant())
                    {
                        case "success":
                        case "neutral":
                        case "skipped":
                            break;
                        case null:
                            pending = true;
                            break;
                        default:
                            // failure, timed_out, action_required, cancelled, stale,
                            // startup_failure: anything that is not a pass is a fail.
                            // The merge gate refuses on anything but SUCCESS, so a
                            // conclusion this code does not recognise must land on the
                            // refusing side.
                            failed = true;
                            break;
                    }
                }
            }
        }

        if (!any)
        {
            return null;
        }

        return pending ? "PENDING" : failed ? "FAILURE" : "SUCCESS";
    }

    private async Task<PullRequestStatus?> FetchOpenPullRequestByHeadBranchRestAsync(
        TrackerQuery query,
        string headRefName,
        CancellationToken cancellationToken)
    {
        // REST scopes head by "owner:branch". The plane creates the branch on the
        // tracked repository itself, so the owner is the repository's own.
        var head = $"{query.Owner}:{headRefName}";
        var url = $"{RepositoryUrl(query)}/pulls?state=open&head={Uri.EscapeDataString(head)}" +
                  "&sort=created&direction=desc&per_page=5";

        JsonElement? newest = null;
        using var request = BuildRestRequest(HttpMethod.Get, url, query.ApiKey);
        using var response = await SendRestAsync(request, cancellationToken);
        using var document = await ParseRestDocumentAsync(response, cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                newest = element;
                break;
            }
        }

        return newest is null
            ? null
            : await BuildPullRequestStatusAsync(query, newest.Value, cancellationToken);
    }

    private async Task<IReadOnlyList<OpenPullRequest>> FetchOpenPullRequestsRestAsync(
        TrackerQuery query,
        int limit,
        CancellationToken cancellationToken)
    {
        // GitHub rejects per_page=0 and caps a page at 100. Clamping here stops a
        // misconfigured limit from turning the status page into an API error.
        var pageSize = Math.Clamp(limit, 1, 100);
        var url = $"{RepositoryUrl(query)}/pulls?state=open&sort=updated&direction=desc" +
                  $"&per_page={pageSize.ToString(CultureInfo.InvariantCulture)}";

        var nodes = new List<JsonElement>();
        using var request = BuildRestRequest(HttpMethod.Get, url, query.ApiKey);
        using var response = await SendRestAsync(request, cancellationToken);
        using var document = await ParseRestDocumentAsync(response, cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                nodes.Add(element.Clone());
            }
        }

        var results = new List<OpenPullRequest>(nodes.Count);
        foreach (var node in nodes)
        {
            var status = await BuildPullRequestStatusAsync(query, node, cancellationToken);
            if (status is null)
            {
                continue;
            }

            var author = node.TryGetProperty("user", out var userNode) && userNode.ValueKind == JsonValueKind.Object
                ? GetOptionalString(userNode, "login")
                : null;

            var updatedAt = ParseDateTimeOffset(node, "updated_at")?.ToUniversalTime() ?? DateTimeOffset.MinValue;

            results.Add(new OpenPullRequest(
                status.Number,
                GetOptionalString(node, "title") ?? $"#{status.Number}",
                GetOptionalString(node, "html_url") ?? string.Empty,
                author,
                status.IsDraft,
                status.ChecksState,
                status.Mergeable,
                updatedAt,
                $"{query.Owner}/{query.Repo}",
                node.TryGetProperty("head", out var headNode) && headNode.ValueKind == JsonValueKind.Object
                    ? GetOptionalString(headNode, "ref")
                    : null,
                status.HeadSha));
        }

        return results;
    }

    private async Task<IReadOnlyList<string>> FetchPullRequestFilesRestAsync(
        TrackerQuery query,
        int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();

        await ReadRestListAsync(
            query,
            $"{RepositoryUrl(query)}/pulls/{pullRequestNumber.ToString(CultureInfo.InvariantCulture)}/files?per_page=100",
            element =>
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                var path = GetOptionalString(element, "filename");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            },
            cancellationToken);

        return paths;
    }

    /// <summary>
    /// Parses "#123" into 123. The identifier is what every caller already carries
    /// next to a node id, and it is what REST addresses an issue by.
    /// </summary>
    internal static int? ParseIssueNumber(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var trimmed = identifier.Trim().TrimStart('#');
        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;
    }
}
