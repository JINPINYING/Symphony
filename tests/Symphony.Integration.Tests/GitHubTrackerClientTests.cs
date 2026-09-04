using System.Net;
using System.Text;
using Symphony.Core.Models;
using Symphony.Infrastructure.Tracker.GitHub;

namespace Symphony.Integration.Tests;

public sealed class GitHubTrackerClientTests
{
    [Fact]
    public async Task FetchCandidateIssuesAsync_ShouldReadIssuesOverRestAndEnrichOverGraphQl()
    {
        const string restIssues = """
            [
              {
                "node_id": "I_001",
                "number": 101,
                "title": "Issue one",
                "body": "Body one",
                "state": "open",
                "html_url": "https://example/1",
                "created_at": "2026-03-05T00:00:00Z",
                "updated_at": "2026-03-05T01:00:00Z",
                "milestone": { "title": "Sprint 1", "number": 1 },
                "labels": [ { "name": "backend" }, { "name": "priority1" } ]
              },
              {
                "node_id": "I_002",
                "number": 102,
                "title": "Issue two",
                "body": "Body two",
                "state": "open",
                "html_url": "https://example/2",
                "created_at": "2026-03-05T00:00:00Z",
                "updated_at": "2026-03-05T01:00:00Z",
                "milestone": { "title": "Sprint 2", "number": 2 },
                "labels": [ { "name": "frontend" } ]
              },
              {
                "node_id": "PR_900",
                "number": 900,
                "title": "A pull request wearing an issue payload",
                "state": "open",
                "html_url": "https://example/pull/900",
                "created_at": "2026-03-05T00:00:00Z",
                "updated_at": "2026-03-05T01:00:00Z",
                "milestone": { "title": "Sprint 1", "number": 1 },
                "labels": [ { "name": "backend" } ],
                "pull_request": { "url": "https://example/pull/900" }
              }
            ]
            """;

        const string graphQlEnrichment = """
            {
              "data": {
                "nodes": [
                  {
                    "id": "I_001",
                    "linkedBranches": { "nodes": [ { "ref": { "name": "feature/issue-101" } } ] },
                    "closedByPullRequestsReferences": {
                      "nodes": [
                        {
                          "id": "PR_1",
                          "number": 501,
                          "state": "OPEN",
                          "url": "https://example/pr/1",
                          "headRefName": "feature/1",
                          "baseRefName": "main"
                        }
                      ]
                    },
                    "blockedBy": { "nodes": [ { "id": "I_099", "number": 99, "state": "OPEN" } ] }
                  }
                ]
              }
            }
            """;

        var handler = new RoutingHandler(restIssues, graphQlEnrichment);
        using var httpClient = new HttpClient(handler);

        var client = new GitHubTrackerClient(httpClient);
        var issues = await client.FetchCandidateIssuesAsync(new TrackerQuery(
            Endpoint: "https://api.github.com/graphql",
            ApiKey: "token",
            Owner: "released",
            Repo: "symphony",
            ActiveStates: ["Open", "In Progress"],
            Labels: ["backend"],
            Milestone: "Sprint 1"));

        var issue = Assert.Single(issues);
        Assert.Equal("#101", issue.Identifier);
        Assert.Equal("I_001", issue.Id);
        Assert.Equal("Issue one", issue.Title);
        Assert.Equal("Open", issue.State);
        Assert.Equal("Sprint 1", issue.Milestone);
        Assert.Equal("https://example/1", issue.Url);
        Assert.Contains("backend", issue.Labels);
        Assert.Equal(1, issue.Priority);
        Assert.False(issue.EnrichmentDegraded);

        // GraphQL-only fields still arrive, from the enrichment call.
        Assert.Equal("feature/issue-101", issue.BranchName);
        Assert.Single(issue.PullRequests);
        Assert.Collection(
            issue.BlockedBy,
            blocker =>
            {
                Assert.Equal("I_099", blocker.Id);
                Assert.Equal("#99", blocker.Identifier);
                Assert.Equal("Open", blocker.State);
            });

        // The scan itself is REST, on the budget that does not run out.
        var scan = Assert.Single(handler.RestRequests);
        Assert.StartsWith("https://api.github.com/repos/released/symphony/issues?", scan, StringComparison.Ordinal);
        Assert.Contains("state=open", scan, StringComparison.Ordinal);
        Assert.Contains("labels=backend", scan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchCandidateIssuesAsync_ShouldSurviveGraphQlRateLimitAndReportDegradedEnrichment()
    {
        const string restIssues = """
            [
              {
                "node_id": "I_001",
                "number": 101,
                "title": "Issue one",
                "state": "open",
                "html_url": "https://example/1",
                "created_at": "2026-03-05T00:00:00Z",
                "updated_at": "2026-03-05T01:00:00Z",
                "milestone": null,
                "labels": [ { "name": "backend" } ]
              }
            ]
            """;

        // The exact shape GitHub returned on 2026-09-03, twice.
        const string rateLimited = """
            {
              "errors": [
                { "type": "RATE_LIMITED", "message": "API rate limit already exceeded" }
              ]
            }
            """;

        var handler = new RoutingHandler(restIssues, rateLimited);
        using var httpClient = new HttpClient(handler);

        var client = new GitHubTrackerClient(httpClient);
        var issues = await client.FetchCandidateIssuesAsync(new TrackerQuery(
            Endpoint: "https://api.github.com/graphql",
            ApiKey: "token",
            Owner: "released",
            Repo: "symphony",
            ActiveStates: ["Open"],
            Labels: [],
            Milestone: null));

        // The whole point: an exhausted GraphQL budget costs detail, not dispatch.
        var issue = Assert.Single(issues);
        Assert.Equal("#101", issue.Identifier);
        Assert.True(issue.EnrichmentDegraded);
        Assert.Empty(issue.BlockedBy);
        Assert.Null(issue.BranchName);
    }

    [Fact]
    public async Task FetchCandidateIssuesAsync_ShouldFollowRestPagination()
    {
        const string firstPage = """
            [
              {
                "node_id": "I_001",
                "number": 101,
                "title": "One",
                "state": "open",
                "html_url": "https://example/1",
                "labels": []
              }
            ]
            """;
        const string secondPage = """
            [
              {
                "node_id": "I_002",
                "number": 102,
                "title": "Two",
                "state": "open",
                "html_url": "https://example/2",
                "labels": []
              }
            ]
            """;

        var handler = new SequencedRestHandler(
            [
                (firstPage, "<https://api.github.com/repositories/1/issues?page=2>; rel=\"next\""),
                (secondPage, null)
            ],
            graphQlJson: "{\"data\":{\"nodes\":[]}}");
        using var httpClient = new HttpClient(handler);

        var client = new GitHubTrackerClient(httpClient);
        var issues = await client.FetchCandidateIssuesAsync(new TrackerQuery(
            Endpoint: "https://api.github.com/graphql",
            ApiKey: "token",
            Owner: "released",
            Repo: "symphony",
            ActiveStates: ["Open"],
            Labels: [],
            Milestone: null));

        Assert.Equal(2, issues.Count);
        Assert.Equal(["#101", "#102"], issues.Select(issue => issue.Identifier));
        Assert.Equal(
            "https://api.github.com/repositories/1/issues?page=2",
            handler.RestRequests[1]);
    }

    [Fact]
    public async Task FetchCandidateIssuesAsync_ShouldFailWhenPaginationAdvertisesAPageItCannotName()
    {
        var handler = new SequencedRestHandler(
            [("[]", "<>; rel=\"next\"")],
            graphQlJson: "{\"data\":{\"nodes\":[]}}");
        using var httpClient = new HttpClient(handler);

        var client = new GitHubTrackerClient(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubTrackerException>(() =>
            client.FetchCandidateIssuesAsync(new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null)));

        Assert.Equal("github_missing_end_cursor", ex.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "0", null)]
    [InlineData(HttpStatusCode.TooManyRequests, "42", "90")]
    public async Task FetchCandidateIssuesAsync_ShouldNameARestRateLimitAndCarryItsClock(
        HttpStatusCode statusCode,
        string remaining,
        string? retryAfterSeconds)
    {
        using var httpClient = new HttpClient(new RateLimitedRestHandler(statusCode, remaining, retryAfterSeconds));

        var client = new GitHubTrackerClient(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubTrackerException>(() =>
            client.FetchCandidateIssuesAsync(new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null)));

        Assert.Equal(GitHubTrackerException.RateLimitedCode, ex.Code);
        Assert.True(ex.IsRateLimited);
        Assert.Contains("rate limit", ex.Message, StringComparison.OrdinalIgnoreCase);

        if (retryAfterSeconds is null)
        {
            Assert.Null(GitHubTrackerException.GetRetryAfter(ex));
        }
        else
        {
            Assert.Equal(TimeSpan.FromSeconds(90), GitHubTrackerException.GetRetryAfter(ex));
        }
    }

    [Fact]
    public async Task FetchCandidateIssuesAsync_ShouldNotTreatAnOrdinaryForbiddenAsARateLimit()
    {
        // A 403 with budget left is a refused token, not a clock. Waiting it out
        // would keep the plane blind forever over a credential nobody was told about.
        using var httpClient = new HttpClient(
            new RateLimitedRestHandler(HttpStatusCode.Forbidden, remaining: "4321", retryAfterSeconds: null, body: "{\"message\":\"Resource not accessible\"}"));

        var client = new GitHubTrackerClient(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubTrackerException>(() =>
            client.FetchCandidateIssuesAsync(new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null)));

        Assert.Equal("github_api_status", ex.Code);
        Assert.False(ex.IsRateLimited);
    }

    [Theory]
    [InlineData("Closed")]
    [InlineData("Done")]
    [InlineData("Resolved")]
    [InlineData("Completed")]
    public async Task FetchIssuesByStatesAsync_ShouldReturnIssuesMatchingRequestedStates(string requestedState)
    {
        const string restIssues = """
            [
              {
                "node_id": "I_010",
                "number": 110,
                "title": "Open issue",
                "state": "open",
                "html_url": "https://example/10",
                "labels": []
              },
              {
                "node_id": "I_011",
                "number": 111,
                "title": "Closed issue",
                "state": "closed",
                "html_url": "https://example/11",
                "labels": []
              }
            ]
            """;

        var handler = new RoutingHandler(restIssues, "{\"data\":{\"nodes\":[]}}");
        using var httpClient = new HttpClient(handler);

        var client = new GitHubTrackerClient(httpClient);
        var issues = await client.FetchIssuesByStatesAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: ["backend"],
                Milestone: "Sprint 1"),
            states: [requestedState]);

        var issue = Assert.Single(issues);
        Assert.Equal("#111", issue.Identifier);
        Assert.Equal("Closed", issue.State);
        Assert.Contains("state=closed", Assert.Single(handler.RestRequests), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.github.com/graphql", "https://api.github.com")]
    [InlineData("https://ghe.example.com/api/graphql", "https://ghe.example.com/api/v3")]
    [InlineData("", "https://api.github.com")]
    [InlineData("not-a-url", "https://api.github.com")]
    public void RestBaseUrl_ShouldPairWithTheConfiguredGraphQlEndpoint(string endpoint, string expected)
    {
        Assert.Equal(expected, GitHubTrackerClient.RestBaseUrl(endpoint));
    }

    [Fact]
    public async Task FetchIssueStatesByIdsAsync_ShouldUseRestWhenTheCallerNamesTheIssueNumber()
    {
        const string restIssue = """
            {
              "node_id": "I_100",
              "number": 100,
              "state": "closed",
              "html_url": "https://example/100",
              "labels": [ { "name": "Symphony-Ready" } ]
            }
            """;

        var handler = new RoutingHandler(restIssue, "{\"data\":{\"nodes\":[]}}");
        using var httpClient = new HttpClient(handler);

        var client = new GitHubTrackerClient(httpClient);
        var states = await client.FetchIssueStatesByIdsAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null),
            issueIds: ["I_100"],
            identifiersByIssueId: new Dictionary<string, string> { ["I_100"] = "#100" });

        var state = Assert.Single(states);
        Assert.Equal("I_100", state.Id);
        Assert.Equal("Closed", state.State);
        Assert.Equal(["symphony-ready"], state.Labels);

        Assert.Empty(handler.GraphQlRequests);
        Assert.Equal("https://api.github.com/repos/released/symphony/issues/100", Assert.Single(handler.RestRequests));
    }

    // The tracked-issue cache refresh asks about every issue it has ever seen, on
    // every tick. One GET each would turn a fifty-issue cache on a fifteen-second
    // tick into twelve thousand requests an hour, re-creating the exhaustion this
    // change exists to stop - on the primary budget this time.
    [Fact]
    public async Task FetchIssueStatesByIdsAsync_ShouldListTheRepositoryRatherThanReadEveryIssueSeparately()
    {
        var listing = "[" + string.Join(
            ",",
            Enumerable.Range(1, 30).Select(number => $$"""
                {
                  "node_id": "I_{{number}}",
                  "number": {{number}},
                  "state": "{{(number % 2 == 0 ? "closed" : "open")}}",
                  "labels": [ { "name": "symphony-ready" } ]
                }
                """)) + "]";

        var handler = new RoutingHandler(listing, "{\"data\":{\"nodes\":[]}}");
        using var httpClient = new HttpClient(handler);

        var ids = Enumerable.Range(1, 30).Select(number => $"I_{number}").ToList();
        var identifiers = ids.ToDictionary(id => id, id => "#" + id["I_".Length..]);

        var client = new GitHubTrackerClient(httpClient);
        var states = await client.FetchIssueStatesByIdsAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null),
            issueIds: ids,
            identifiersByIssueId: identifiers);

        Assert.Equal(30, states.Count);
        Assert.Equal("Closed", states.Single(state => state.Id == "I_2").State);
        Assert.Equal("Open", states.Single(state => state.Id == "I_3").State);

        // One listing, not thirty reads - and no GraphQL at all.
        var request = Assert.Single(handler.RestRequests);
        Assert.Contains("state=all", request, StringComparison.Ordinal);
        Assert.Empty(handler.GraphQlRequests);
    }

    [Fact]
    public async Task FetchPullRequestStatusAsync_ShouldReadTheHeadChecksAndMergeabilityOverRest()
    {
        const string pullRequest = """
            {
              "number": 501,
              "state": "closed",
              "draft": false,
              "merged": true,
              "mergeable": null,
              "head": { "ref": "symphony/78", "sha": "abc123" }
            }
            """;
        const string combinedStatus = """
            { "state": "success", "total_count": 1 }
            """;
        const string checkRuns = """
            {
              "check_runs": [
                { "status": "completed", "conclusion": "success" },
                { "status": "completed", "conclusion": "skipped" }
              ]
            }
            """;

        var handler = new PullRequestRestHandler(pullRequest, combinedStatus, checkRuns);
        using var httpClient = new HttpClient(handler);

        var client = new GitHubTrackerClient(httpClient);
        var status = await client.FetchPullRequestStatusAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null),
            pullRequestNumber: 501);

        Assert.NotNull(status);
        Assert.Equal(501, status!.Number);
        // REST says closed+merged; the phase machine reads GraphQL's MERGED.
        Assert.Equal("MERGED", status.State);
        Assert.Equal("abc123", status.HeadSha);
        Assert.Equal("SUCCESS", status.ChecksState);
        // null mergeable means "GitHub has not computed it", never "conflicting".
        Assert.Equal("UNKNOWN", status.Mergeable);
        Assert.Empty(handler.GraphQlRequests);
    }

    [Fact]
    public async Task FetchPullRequestStatusAsync_ShouldReportAnUnfinishedCheckRunAsPending()
    {
        const string pullRequest = """
            {
              "number": 502,
              "state": "open",
              "draft": false,
              "mergeable": true,
              "head": { "ref": "symphony/79", "sha": "def456" }
            }
            """;
        const string combinedStatus = """
            { "state": "pending", "total_count": 0 }
            """;
        const string checkRuns = """
            { "check_runs": [ { "status": "in_progress", "conclusion": null } ] }
            """;

        using var httpClient = new HttpClient(new PullRequestRestHandler(pullRequest, combinedStatus, checkRuns));

        var client = new GitHubTrackerClient(httpClient);
        var status = await client.FetchPullRequestStatusAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null),
            pullRequestNumber: 502);

        Assert.NotNull(status);
        Assert.Equal("OPEN", status!.State);
        Assert.Equal("PENDING", status.ChecksState);
        Assert.Equal("MERGEABLE", status.Mergeable);
    }

    [Fact]
    public async Task FetchPullRequestStatusAsync_ShouldReturnNullForAPullRequestThatIsNotThere()
    {
        using var httpClient = new HttpClient(new StaticStatusCodeHandler(HttpStatusCode.NotFound));

        var client = new GitHubTrackerClient(httpClient);
        var status = await client.FetchPullRequestStatusAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null),
            pullRequestNumber: 999);

        Assert.Null(status);
    }

    [Fact]
    public async Task FetchIssueStatesByIdsAsync_ShouldReturnOnlyScopedIssueStates()
    {
        const string payload = """
            {
              "data": {
                "nodes": [
                  {
                    "id": "I_100",
                    "state": "OPEN",
                    "repository": {
                      "name": "symphony",
                      "owner": { "login": "released" }
                    }
                  },
                  {
                    "id": "I_200",
                    "state": "CLOSED",
                    "repository": {
                      "name": "symphony",
                      "owner": { "login": "released" }
                    }
                  },
                  {
                    "id": "I_999",
                    "state": "CLOSED",
                    "repository": {
                      "name": "other",
                      "owner": { "login": "released" }
                    }
                  }
                ]
              }
            }
            """;

        using var httpClient = new HttpClient(new StaticJsonHandler(payload))
        {
            BaseAddress = new Uri("https://api.github.com/graphql")
        };

        var client = new GitHubTrackerClient(httpClient);
        var states = await client.FetchIssueStatesByIdsAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null),
            issueIds: ["I_200", "I_100", "I_999", "I_404"]);

        Assert.Collection(
            states,
            state =>
            {
                Assert.Equal("I_200", state.Id);
                Assert.Equal("Closed", state.State);
            },
            state =>
            {
                Assert.Equal("I_100", state.Id);
                Assert.Equal("Open", state.State);
            });
    }

    [Fact]
    public async Task FetchIssuesByStatesAsync_ShouldReturnImmediatelyWhenStateFilterIsEmpty()
    {
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/graphql")
        };

        var client = new GitHubTrackerClient(httpClient);
        var issues = await client.FetchIssuesByStatesAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null),
            states: []);

        Assert.Empty(issues);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "github_api_status")]
    [InlineData(HttpStatusCode.Unauthorized, "github_api_status")]
    public async Task FetchCandidateIssuesAsync_ShouldMapNonSuccessStatusCodes(HttpStatusCode statusCode, string expectedCode)
    {
        using var httpClient = new HttpClient(new StaticStatusCodeHandler(statusCode))
        {
            BaseAddress = new Uri("https://api.github.com/graphql")
        };

        var client = new GitHubTrackerClient(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubTrackerException>(() =>
            client.FetchCandidateIssuesAsync(new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null)));

        Assert.Equal(expectedCode, ex.Code);
    }

    [Fact]
    public async Task FetchCandidateIssuesAsync_ShouldMapMalformedPayloads()
    {
        // A listing that is not a list. The scan must refuse rather than report an
        // unreadable page as an empty repository.
        const string payload = """
            { "message": "Moved Permanently" }
            """;

        using var httpClient = new HttpClient(new StaticJsonHandler(payload));

        var client = new GitHubTrackerClient(httpClient);
        var ex = await Assert.ThrowsAsync<GitHubTrackerException>(() =>
            client.FetchCandidateIssuesAsync(new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null)));

        Assert.Equal("github_unknown_payload", ex.Code);
    }

    [Fact]
    public async Task ExecuteGitHubGraphQlAsync_ShouldReturnSuccessForSingleOperation()
    {
        const string payload = """
            {
              "data": {
                "viewer": {
                  "login": "nick"
                }
              }
            }
            """;

        using var httpClient = new HttpClient(new StaticJsonHandler(payload))
        {
            BaseAddress = new Uri("https://api.github.com/graphql")
        };

        var client = new GitHubTrackerClient(httpClient);
        var result = await client.ExecuteGitHubGraphQlAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null),
            "query { viewer { login } }",
            null);

        Assert.True(result.Success);
        Assert.Contains("\"viewer\"", result.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteGitHubGraphQlAsync_ShouldPreserveGraphQlErrorBody()
    {
        const string payload = """
            {
              "errors": [
                { "message": "boom" }
              ]
            }
            """;

        using var httpClient = new HttpClient(new StaticJsonHandler(payload))
        {
            BaseAddress = new Uri("https://api.github.com/graphql")
        };

        var client = new GitHubTrackerClient(httpClient);
        var result = await client.ExecuteGitHubGraphQlAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null),
            "query { viewer { login } }",
            null);

        Assert.False(result.Success);
        Assert.Equal("github_graphql_errors", result.ErrorCode);
        Assert.Contains("\"errors\"", result.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteGitHubGraphQlAsync_ShouldRejectMultipleOperations()
    {
        using var httpClient = new HttpClient(new StaticJsonHandler("{\"data\":{}}"))
        {
            BaseAddress = new Uri("https://api.github.com/graphql")
        };

        var client = new GitHubTrackerClient(httpClient);
        var result = await client.ExecuteGitHubGraphQlAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null),
            "query One { viewer { login } } mutation Two { closeIssue(input: {}) { clientMutationId } }",
            null);

        Assert.False(result.Success);
        Assert.Equal("invalid_graphql_document", result.ErrorCode);
    }

    /// <summary>
    /// Answers REST and GraphQL separately, and records which was asked. The point
    /// of every REST test here is WHICH transport carried the question, so a handler
    /// that cannot tell them apart cannot prove anything.
    /// </summary>
    private sealed class RoutingHandler(string restJson, string graphQlJson) : HttpMessageHandler
    {
        public List<string> RestRequests { get; } = [];
        public List<string> GraphQlRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var isGraphQl = url.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase);
            (isGraphQl ? GraphQlRequests : RestRequests).Add(url);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(isGraphQl ? graphQlJson : restJson, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>REST pages served in order, each with the Link header it should carry.</summary>
    private sealed class SequencedRestHandler(
        IReadOnlyList<(string Json, string? LinkHeader)> pages,
        string graphQlJson) : HttpMessageHandler
    {
        public List<string> RestRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(graphQlJson, Encoding.UTF8, "application/json")
                });
            }

            var index = RestRequests.Count;
            RestRequests.Add(url);
            var (json, link) = index < pages.Count ? pages[index] : ("[]", null);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (link is not null)
            {
                response.Headers.TryAddWithoutValidation("Link", link);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class RateLimitedRestHandler(
        HttpStatusCode statusCode,
        string remaining,
        string? retryAfterSeconds,
        string body = "{\"message\":\"API rate limit exceeded for user ID 1.\"}") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining);
            if (retryAfterSeconds is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds);
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>The three REST reads a pull request status is assembled from.</summary>
    private sealed class PullRequestRestHandler(
        string pullRequestJson,
        string combinedStatusJson,
        string checkRunsJson) : HttpMessageHandler
    {
        public List<string> GraphQlRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase))
            {
                GraphQlRequests.Add(url);
            }

            var json = url.Contains("/check-runs", StringComparison.Ordinal)
                ? checkRunsJson
                : url.EndsWith("/status", StringComparison.Ordinal)
                    ? combinedStatusJson
                    : pullRequestJson;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StaticStatusCodeHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
            });
        }
    }
}
