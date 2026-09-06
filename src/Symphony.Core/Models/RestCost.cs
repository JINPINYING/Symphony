using System.Globalization;
using System.Text;

namespace Symphony.Core.Models;

/// <summary>
/// What REST costs GitHub's <c>core</c> budget.
///
/// WHY THIS EXISTS SEPARATELY FROM <see cref="GraphQlCost"/>. They are two
/// budgets of the same size and they are charged by different rules, and
/// treating REST as the free alternative to GraphQL is precisely how the plane
/// arrived at 7,293 core points an hour against a 5,000 limit on 2026-09-06.
/// GraphQL is charged on the nodes a query REQUESTS, so its lever is the page
/// size. REST is charged one point per request whatever comes back, so its
/// levers are different ones: how often a thing is polled, how many pages a
/// listing walks, and whether the request was conditional - GitHub does not
/// charge a request it answers <c>304 Not Modified</c>.
/// </summary>
public static class RestCost
{
    /// <summary>
    /// The primary hourly <c>core</c> budget for a personal access token.
    /// Observed directly in <c>X-Ratelimit-Limit</c> alongside
    /// <c>X-Ratelimit-Resource: core</c> on 2026-09-06 - the same size as the
    /// GraphQL budget, which is the fact that made "move it to REST" look free
    /// four times running.
    /// </summary>
    public const int HourlyBudget = 5000;
}

/// <summary>
/// One REST read the plane makes, how many requests it takes, and how often it
/// makes it.
/// </summary>
/// <param name="CallSite">
/// The name the runtime records this call under, so the modelled figure and the
/// measured one are about the same thing. See <c>GitHubRestCallSites</c>.
/// </param>
/// <param name="RequestsPerCall">
/// Requests one execution of this read costs, pages included. A listing that
/// walks two pages is two requests and two points, which is the whole difference
/// between REST and GraphQL costing.
/// </param>
/// <param name="CallsPerHour">How many times an hour the plane executes it in steady state.</param>
/// <param name="Assumption">
/// Why the call rate and page count are what they are, so a reader can dispute
/// the assumption rather than the total.
/// </param>
public sealed record RestReadCost(
    string CallSite,
    int RequestsPerCall,
    double CallsPerHour,
    string Assumption)
{
    /// <summary>
    /// One point per request. No conditional-request credit is taken here on
    /// purpose: a 304 costs nothing, but whether a resource changed is GitHub's
    /// decision and not the plane's, and a ceiling proved on the assumption that
    /// nothing ever changes proves nothing. Conditional requests are margin
    /// against this figure, not a reduction of it.
    /// </summary>
    public double PointsPerHour => RequestsPerCall * CallsPerHour;
}

/// <summary>
/// The modelled steady-state hourly <c>core</c> cost, itemised. Itemised because
/// a single number that fails a build tells nobody which read to change - and
/// because the acceptance this exists for is per-call-site attribution, which a
/// total cannot give.
/// </summary>
public sealed record RestHourlyCost(IReadOnlyList<RestReadCost> Reads)
{
    public double PointsPerHour => Reads.Sum(read => read.PointsPerHour);

    public double PercentOfBudget => PointsPerHour * 100.0 / RestCost.HourlyBudget;

    /// <summary>The arithmetic, in the form a pull request description can carry.</summary>
    public string Describe()
    {
        var lines = new StringBuilder();
        foreach (var read in Reads.OrderByDescending(read => read.PointsPerHour))
        {
            lines.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1} request(s) x {2:0.##}/hour = {3:0.##} points/hour ({4})",
                read.CallSite,
                read.RequestsPerCall,
                read.CallsPerHour,
                read.PointsPerHour,
                read.Assumption));
        }

        lines.Append(string.Format(
            CultureInfo.InvariantCulture,
            "total: {0:0.##} points/hour, {1:0.#}% of the {2}-point core budget",
            PointsPerHour,
            PercentOfBudget,
            RestCost.HourlyBudget));
        return lines.ToString();
    }
}
