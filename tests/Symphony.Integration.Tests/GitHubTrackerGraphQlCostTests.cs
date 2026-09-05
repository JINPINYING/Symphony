using System.Text.RegularExpressions;
using Symphony.Core.Configuration;
using Symphony.Core.Models;
using Symphony.Infrastructure.Tracker.GitHub;

namespace Symphony.Integration.Tests;

/// <summary>
/// The build that was missing.
///
/// The plane exhausted its hourly GraphQL budget on 2026-09-01 (twice) and again
/// on 2026-09-05, and every time the discovery was the plane falling silent for
/// the rest of the hour. Every time, the number that mattered - page sizes times
/// nesting times repositories times scans an hour - was computable from the
/// source before the change shipped. These tests compute it.
/// </summary>
public sealed class GitHubTrackerGraphQlCostTests
{
    [Fact]
    public void ModelledSteadyStateStaysUnderTheCeiling()
    {
        var cost = GitHubTrackerGraphQlCost.Model(GitHubTrackerGraphQlCost.PessimisticSteadyState);

        Assert.True(
            cost.PointsPerHour < TrackerReadCadence.ModelledHourlyCeiling,
            $"Modelled GraphQL cost is {cost.PointsPerHour:0} points/hour against a ceiling of " +
            $"{TrackerReadCadence.ModelledHourlyCeiling} and a budget of {GraphQlCost.HourlyBudget}.\n" +
            cost.Describe());

        // The ceiling is not the budget. A plane that plans to spend the whole
        // allowance has none left for the bursts it cannot model.
        Assert.True(TrackerReadCadence.ModelledHourlyCeiling < GraphQlCost.HourlyBudget);
    }

    [Fact]
    public void TheModelIsItemisedSoAFailureNamesTheQueryToChange()
    {
        var cost = GitHubTrackerGraphQlCost.Model(GitHubTrackerGraphQlCost.PessimisticSteadyState);

        Assert.Contains(cost.Reads, read => read.Name.Contains("enrichment", StringComparison.Ordinal));
        Assert.All(cost.Reads, read => Assert.False(string.IsNullOrWhiteSpace(read.Assumption)));
    }

    /// <summary>
    /// The cadence the model assumes has to be the cadence the tick service uses.
    /// A model computed against a 60-second scan while the runtime scans every
    /// 15 is not a model of anything.
    /// </summary>
    [Fact]
    public void TheModelAndTheRuntimeShareOneCadence()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), TrackerReadCadence.CandidateScan);
        Assert.Equal(60d, TrackerReadCadence.CallsPerHour(TrackerReadCadence.CandidateScan));
        Assert.Equal(30d, TrackerReadCadence.CallsPerHour(TrackerReadCadence.OpenPullRequestPoll));
    }

    /// <summary>
    /// Every connection the plane pages either asks for <c>totalCount</c>, so a
    /// short page is detectable, or is one of the deliberate first-one reads where
    /// the rest of the page is data the plane has decided not to look at.
    ///
    /// This is the mechanical half of "a read that decides something must be
    /// complete, or say that it is not": a page size chosen for cost, with nothing
    /// reporting that the page was short, is a silent wrong answer.
    /// </summary>
    [Theory]
    [InlineData("enrichment")]
    [InlineData("issue states")]
    [InlineData("issues by ids")]
    public void EveryPagedConnectionEitherReportsItsTotalOrReadsExactlyOne(string which)
    {
        var query = which switch
        {
            "enrichment" => GitHubTrackerClient.EnrichmentQueryText,
            "issue states" => GitHubTrackerClient.IssueStatesQueryText,
            _ => GitHubTrackerClient.IssuesByIdsQueryText
        };

        foreach (var connection in PagedConnections(query))
        {
            var body = SelectionSetAfter(query, connection.Index);
            var readsExactlyOne = connection.PageSize == "$branches";

            Assert.True(
                readsExactlyOne || body.Contains("totalCount", StringComparison.Ordinal),
                $"{connection.Field}(first: {connection.PageSize}) in the {which} query pages without " +
                "asking for totalCount, so a short page cannot be told from a complete one.");
        }
    }

    /// <summary>
    /// No connection asks for a page bigger than the plane consumes on its first
    /// pass. Fifty labels per issue were 55% of the cost of the query that
    /// exhausted the budget, and the plane uses four labels in total.
    /// </summary>
    [Fact]
    public void NoQueryAsksForAPageLargerThanTheFirstPassConsumes()
    {
        foreach (var query in new[]
                 {
                     GitHubTrackerClient.EnrichmentQueryText,
                     GitHubTrackerClient.IssueStatesQueryText,
                     GitHubTrackerClient.IssuesByIdsQueryText
                 })
        {
            foreach (var connection in PagedConnections(query))
            {
                Assert.True(
                    connection.PageSize.StartsWith('$'),
                    $"{connection.Field} pages at a hard-coded {connection.PageSize}. Page sizes are " +
                    "variables so the narrow first pass and the wide re-read are the same query.");
            }
        }
    }

    private sealed record PagedConnection(string Field, string PageSize, int Index);

    private static IEnumerable<PagedConnection> PagedConnections(string query) =>
        Regex.Matches(query, @"(?<field>\w+)\((?<args>[^)]*?)first:\s*(?<page>\$?\w+)")
            .Select(match => new PagedConnection(
                match.Groups["field"].Value,
                match.Groups["page"].Value,
                match.Index));

    /// <summary>The braces immediately following a connection: its selection set.</summary>
    private static string SelectionSetAfter(string query, int index)
    {
        var open = query.IndexOf('{', index);
        if (open < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        for (var i = open; i < query.Length; i++)
        {
            if (query[i] == '{')
            {
                depth++;
            }
            else if (query[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return query[open..(i + 1)];
                }
            }
        }

        return query[open..];
    }
}
