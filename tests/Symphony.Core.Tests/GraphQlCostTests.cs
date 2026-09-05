using Symphony.Core.Models;

namespace Symphony.Core.Tests;

/// <summary>
/// The arithmetic that decides whether the plane can run for a whole hour.
///
/// The candidate scan cost 46 points a call and 8,280 points an hour against a
/// 5,000-point budget, and nobody knew until the plane went blind - three times.
/// The number was always computable from the query text; nothing computed it.
/// These tests pin the computation, starting with the query that caused the
/// outage.
/// </summary>
public sealed class GraphQlCostTests
{
    // The candidate scan exactly as it stood on 2026-09-05, before the read moved
    // to REST. Kept verbatim because it is the worked example in ADCP#88 and the
    // only case where the expected answer is known from GitHub's own accounting.
    private const string ExhaustingScanQuery = """
        query($owner: String!, $repo: String!) {
          repository(owner: $owner, name: $repo) {
            issues(first: 50) {
              nodes {
                id
                number
                labels(first: 50) { nodes { name } }
                blockedBy(first: 20) { nodes { number } }
                linkedBranches(first: 10) { nodes { ref { name } } }
                closedByPullRequestsReferences(first: 10) { nodes { number } }
              }
            }
          }
        }
        """;

    [Fact]
    public void TheQueryThatExhaustedTheBudgetCostsWhatTheIncidentSaidItDid()
    {
        // 50 issues + (50 labels + 20 blockers + 10 branches + 10 pull requests)
        // on each of them. Charged on what is REQUESTED, not on what comes back.
        Assert.Equal(4_550, GraphQlCost.CountNodes(ExhaustingScanQuery));
        Assert.Equal(46, GraphQlCost.PointsFor(ExhaustingScanQuery));

        // 46 x 3 repositories x 60 scans an hour = 8,280 against a 5,000 budget.
        Assert.True(46 * 3 * 60 > GraphQlCost.HourlyBudget);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(100, 1)]
    [InlineData(101, 2)]
    [InlineData(4_550, 46)]
    public void PointsAreNodesOverAHundredRoundedUp(int nodes, int expectedPoints) =>
        Assert.Equal(expectedPoints, GraphQlCost.PointsForNodes(nodes));

    [Fact]
    public void NestedPagesMultiply()
    {
        const string query = """
            query {
              repository {
                issues(first: 10) {
                  nodes {
                    labels(first: 5) { nodes { name } }
                  }
                }
              }
            }
            """;

        // 10 issues, and 5 labels on each of them.
        Assert.Equal(10 + 50, GraphQlCost.CountNodes(query));
    }

    [Fact]
    public void PageSizesGivenAsVariablesAreChargedAtTheValueSupplied()
    {
        const string query = """
            query($ids: [ID!]!, $blockers: Int!) {
              nodes(ids: $ids) {
                ... on Issue {
                  blockedBy(first: $blockers) { totalCount nodes { number } }
                }
              }
            }
            """;

        var sizes = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ids"] = 20,
            ["blockers"] = 5
        };

        // 20 ids, and 5 blockers on each of them. The id list is charged like a
        // page even though it never says "first".
        Assert.Equal(20 + 100, GraphQlCost.CountNodes(query, sizes));
    }

    [Fact]
    public void AnUnresolvablePageSizeIsChargedRatherThanIgnored()
    {
        const string query = """
            query($blockers: Int!) {
              issue {
                blockedBy(first: $blockers) { nodes { number } }
              }
            }
            """;

        // Understating a cost is the failure this whole type exists to prevent, so
        // a page size with no supplied value counts as a connection rather than as
        // nothing.
        Assert.Equal(1, GraphQlCost.CountNodes(query));
    }

    [Fact]
    public void TheOperationSignatureIsNotAPageRequest()
    {
        // "$first: Int!" DECLARES a variable called first. Charging for it would
        // invent a page nobody asked for, and a cost model that cries wolf is one
        // that gets an exclusion added to it rather than a query fixed.
        const string query = """
            query($first: Int!, $ids: [ID!]!) {
              viewer { login }
            }
            """;

        Assert.Equal(0, GraphQlCost.CountNodes(query));
        Assert.Equal(1, GraphQlCost.PointsFor(query));
    }

    [Fact]
    public void CommentsAndStringsDoNotCountAsPageRequests()
    {
        const string query = """
            query {
              # labels(first: 500) - a comment, not a request
              search(query: "first: 500") {
                nodes { id }
              }
            }
            """;

        Assert.Equal(0, GraphQlCost.CountNodes(query));
    }

    [Fact]
    public void AnHourlyCostItemisesWhatItCharged()
    {
        var cost = new GraphQlHourlyCost([
            new GraphQlReadCost("enrichment", 300, 180, "one batch per scan"),
            new GraphQlReadCost("writes", 100, 200, "one point each")
        ]);

        // 3 points x 180 + 1 point x 200.
        Assert.Equal(740, cost.PointsPerHour);
        Assert.Equal(14.8, cost.PercentOfBudget, 1);

        // A single number that fails a build tells nobody which query to change.
        Assert.Contains("enrichment", cost.Describe(), StringComparison.Ordinal);
        Assert.Contains("one batch per scan", cost.Describe(), StringComparison.Ordinal);
    }
}
