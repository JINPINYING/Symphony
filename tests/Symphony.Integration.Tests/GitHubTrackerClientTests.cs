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
    // ------------------------------------------------------------------
    // GraphQL budget: what the enrichment query asks for, and what it does
    // when the page it asked for was not the whole answer. ADCP#88.
    // ------------------------------------------------------------------

    private const string OneRestIssue = """
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

    private static TrackerQuery OneRepositoryQuery() => new(
        Endpoint: "https://api.github.com/graphql",
        ApiKey: "token",
        Owner: "released",
        Repo: "symphony",
        ActiveStates: ["Open"],
        Labels: [],
        Milestone: null);

    /// <summary>
    /// GitHub charges what a query REQUESTS, multiplied down the nesting, whether
    /// or not the nodes exist. Ten linked branches per issue when exactly one is
    /// ever read was 500 of the 2,050 nodes this query used to cost, sixty times
    /// an hour per repository.
    /// </summary>
    [Fact]
    public async Task EnrichmentAsksForOnlyTheNodesItReads()
    {
        var handler = new RecordingGraphQlHandler(OneRestIssue, [EnrichmentPayload(blockerTotal: 1, blockerCount: 1)]);
        using var httpClient = new HttpClient(handler);

        await new GitHubTrackerClient(httpClient).FetchCandidateIssuesAsync(OneRepositoryQuery());

        var body = Assert.Single(handler.GraphQlBodies);

        // One branch, because GetLinkedBranchName reads the first and ignores the
        // rest; five blockers and five closing pull requests, which is what an
        // issue carries, with totalCount to say when it is not.
        Assert.Contains("\"branches\":1", body, StringComparison.Ordinal);
        Assert.Contains("\"connections\":5", body, StringComparison.Ordinal);
        Assert.Contains("totalCount", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The blocker rule refuses to dispatch while any blocker is open, so a
    /// blocker list short of the whole reads as "nothing blocks this" - the one
    /// wrong answer that dispatches. A short page is detected and re-read, not
    /// believed.
    /// </summary>
    [Fact]
    public async Task ATruncatedBlockerListIsReReadAtTheFullPageRatherThanBelieved()
    {
        var handler = new RecordingGraphQlHandler(OneRestIssue,
        [
            EnrichmentPayload(blockerTotal: 7, blockerCount: 5),
            EnrichmentPayload(blockerTotal: 7, blockerCount: 7)
        ]);
        using var httpClient = new HttpClient(handler);

        var issues = await new GitHubTrackerClient(httpClient).FetchCandidateIssuesAsync(OneRepositoryQuery());

        Assert.Equal(2, handler.GraphQlBodies.Count);
        Assert.Contains("\"connections\":5", handler.GraphQlBodies[0], StringComparison.Ordinal);
        Assert.Contains("\"connections\":100", handler.GraphQlBodies[1], StringComparison.Ordinal);

        var issue = Assert.Single(issues);
        Assert.Equal(7, issue.BlockedBy.Count);
        Assert.False(issue.EnrichmentDegraded);
    }

    [Fact]
    public async Task AListStillTruncatedAfterTheWideReadIsReportedAsIncomplete()
    {
        // More than a hundred blockers on one issue. Absurd, and therefore exactly
        // the case where a silent partial answer would be believed.
        var handler = new RecordingGraphQlHandler(OneRestIssue,
        [
            EnrichmentPayload(blockerTotal: 400, blockerCount: 5),
            EnrichmentPayload(blockerTotal: 400, blockerCount: 100)
        ]);
        using var httpClient = new HttpClient(handler);

        var issues = await new GitHubTrackerClient(httpClient).FetchCandidateIssuesAsync(OneRepositoryQuery());

        // Degraded, so the caller keeps what it already knew rather than acting on
        // a list it cannot trust - the same treatment an exhausted budget gets.
        Assert.True(Assert.Single(issues).EnrichmentDegraded);
    }

    [Fact]
    public async Task AFailedWideReReadKeepsTheIssuesTheNarrowPassAlreadyAnswered()
    {
        const string rateLimited = """
            {
              "errors": [
                { "type": "RATE_LIMITED", "message": "API rate limit already exceeded" }
              ]
            }
            """;

        var handler = new RecordingGraphQlHandler(OneRestIssue,
        [
            EnrichmentPayload(blockerTotal: 7, blockerCount: 5),
            rateLimited
        ]);
        using var httpClient = new HttpClient(handler);

        var issues = await new GitHubTrackerClient(httpClient).FetchCandidateIssuesAsync(OneRepositoryQuery());

        // The scan still returns. The issue whose blockers could not be read whole
        // says so; nothing else is lost.
        Assert.True(Assert.Single(issues).EnrichmentDegraded);
    }

    /// <summary>
    /// The budget reading GitHub attaches to a REFUSAL is the most valuable one
    /// there is - it is the only one that says how the budget was spent - so it
    /// has to be recorded before the refusal is thrown and the response disposed.
    /// </summary>
    [Fact]
    public async Task TheBudgetHeadersAreRecordedEvenWhenTheCallIsRefused()
    {
        var observer = new RecordingRateLimitObserver();
        var handler = new RateLimitedRestHandler(
            HttpStatusCode.Forbidden,
            remaining: "0",
            retryAfterSeconds: null,
            limit: "5000",
            used: "5000",
            resource: "core");
        using var httpClient = new HttpClient(handler);

        var client = new GitHubTrackerClient(httpClient, observer);

        await Assert.ThrowsAsync<GitHubTrackerException>(
            () => client.FetchCandidateIssuesAsync(OneRepositoryQuery()));

        var reading = Assert.Single(observer.Readings);
        Assert.Equal("core", reading.Resource);
        Assert.Equal(5000, reading.Limit);
        Assert.Equal(5000, reading.Used);
        Assert.Equal(0, reading.Remaining);
    }

    [Fact]
    public async Task TheBudgetHeadersAreRecordedOnASuccessfulRead()
    {
        var observer = new RecordingRateLimitObserver();
        var handler = new RecordingGraphQlHandler(OneRestIssue, [EnrichmentPayload(blockerTotal: 0, blockerCount: 0)])
        {
            RateLimitHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["x-ratelimit-resource"] = "graphql",
                ["x-ratelimit-limit"] = "5000",
                ["x-ratelimit-used"] = "5011",
                ["x-ratelimit-remaining"] = "0",
                ["x-ratelimit-reset"] = "1788610784"
            }
        };
        using var httpClient = new HttpClient(handler);

        await new GitHubTrackerClient(httpClient, observer).FetchCandidateIssuesAsync(OneRepositoryQuery());

        // The exact readings from 2026-09-05, including Used above Limit - the
        // overshoot is the interesting part, so UsedPercent is computed from Used
        // against Limit rather than inferred from Remaining.
        Assert.All(observer.Readings, reading => Assert.Equal("graphql", reading.Resource));
        var latest = observer.Readings[^1];
        Assert.Equal(5011, latest.Used);
        Assert.Equal(0, latest.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 12, 19, 44, TimeSpan.Zero), latest.ResetAtUtc);
        Assert.Equal(100.2, latest.UsedPercent);
    }

    /// <summary>
    /// The raw <c>github_graphql</c> tool spends from the same 5,000-point hourly
    /// budget as every scan, and it is the one path that cannot go through the
    /// shared GraphQL send helper - it reports failure as a result rather than an
    /// exception. A budget observed only on the plane's own calls under-reports
    /// exactly when the agents are busiest, so the reading is taken here too.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"data\":{\"viewer\":{\"login\":\"nick\"}}}")]
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}")]
    public async Task TheBudgetHeadersAreRecordedForTheRawGraphQlTool(HttpStatusCode statusCode, string payload)
    {
        var observer = new RecordingRateLimitObserver();
        using var httpClient = new HttpClient(new BudgetHeaderHandler(statusCode, payload))
        {
            BaseAddress = new Uri("https://api.github.com/graphql")
        };

        var result = await new GitHubTrackerClient(httpClient, observer).ExecuteGitHubGraphQlAsync(
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

        Assert.Equal(statusCode == HttpStatusCode.OK, result.Success);

        var reading = Assert.Single(observer.Readings);
        Assert.Equal("graphql", reading.Resource);
        Assert.Equal(5000, reading.Limit);
        Assert.Equal(5011, reading.Used);
        Assert.Equal(0, reading.Remaining);
    }

    private static string EnrichmentPayload(int blockerTotal, int blockerCount)
    {
        var blockers = string.Join(',', Enumerable.Range(1, blockerCount)
            .Select(index => $$"""{ "id": "I_{{index}}", "number": {{index}}, "state": "CLOSED" }"""));

        return $$"""
            {
              "data": {
                "nodes": [
                  {
                    "id": "I_001",
                    "linkedBranches": { "nodes": [ { "ref": { "name": "feature/issue-101" } } ] },
                    "closedByPullRequestsReferences": { "totalCount": 0, "nodes": [] },
                    "blockedBy": { "totalCount": {{blockerTotal}}, "nodes": [ {{blockers}} ] }
                  }
                ]
              }
            }
            """;
    }

    private sealed class RecordingRateLimitObserver : Symphony.Core.Abstractions.IGitHubRateLimitObserver
    {
        public List<GitHubRateLimitReading> Readings { get; } = [];

        public void Record(GitHubRateLimitReading reading) => Readings.Add(reading);
    }

    /// <summary>
    /// REST answered once, then GraphQL answered from a queue - so a narrow pass
    /// and its wide re-read can be given different payloads - and every GraphQL
    /// request body is kept, because what the query ASKED for is the thing under
    /// test.
    /// </summary>
    private sealed class RecordingGraphQlHandler(string restJson, IReadOnlyList<string> graphQlResponses)
        : HttpMessageHandler
    {
        public List<string> GraphQlBodies { get; } = [];

        public IReadOnlyDictionary<string, string>? RateLimitHeaders { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isGraphQl = request.RequestUri!.ToString().EndsWith("/graphql", StringComparison.OrdinalIgnoreCase);

            string payload;
            if (isGraphQl)
            {
                GraphQlBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                var index = GraphQlBodies.Count - 1;
                payload = index < graphQlResponses.Count
                    ? graphQlResponses[index]
                    : graphQlResponses[^1];
            }
            else
            {
                payload = restJson;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            foreach (var (name, value) in RateLimitHeaders ?? new Dictionary<string, string>())
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        }
    }

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
        string body = "{\"message\":\"API rate limit exceeded for user ID 1.\"}",
        // The rest of the budget headers GitHub sends alongside remaining. Optional
        // so the refusal tests that predate the budget reading keep asserting only
        // what they were about.
        string? limit = null,
        string? used = null,
        string? resource = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining);
            if (limit is not null)
            {
                response.Headers.TryAddWithoutValidation("x-ratelimit-limit", limit);
            }

            if (used is not null)
            {
                response.Headers.TryAddWithoutValidation("x-ratelimit-used", used);
            }

            if (resource is not null)
            {
                response.Headers.TryAddWithoutValidation("x-ratelimit-resource", resource);
            }

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
    /// <summary>
    /// Answers with a chosen status and the budget headers GitHub attached on
    /// 2026-09-05, so the same handler proves the reading is taken on the success
    /// and the refusal alike.
    /// </summary>
    private sealed class BudgetHeaderHandler(HttpStatusCode statusCode, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            response.Headers.TryAddWithoutValidation("x-ratelimit-resource", "graphql");
            response.Headers.TryAddWithoutValidation("x-ratelimit-limit", "5000");
            response.Headers.TryAddWithoutValidation("x-ratelimit-used", "5011");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1788610784");

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
