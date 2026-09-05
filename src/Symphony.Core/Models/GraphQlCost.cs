using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Symphony.Core.Models;

/// <summary>
/// What a GraphQL query costs GitHub, computed from the query TEXT rather than
/// from what comes back.
///
/// WHY THIS EXISTS. On 2026-09-05 the plane spent the whole 5,000-point hourly
/// GraphQL budget and went blind for the rest of the hour, and the only warning
/// was the blindness itself. The reading was in the response headers all along;
/// nothing was reading them, and the endpoint reached for instead - <c>gh api
/// rate_limit</c> - puts the CORE budget in its top-level <c>rate</c> block and
/// the GraphQL one at <c>.resources.graphql</c>, so a glance at it reports 5,000
/// remaining about a budget that has none. That had already happened twice on
/// 2026-09-01. Three rediscoveries of one arithmetic error is the signature of a
/// fact that belongs in a build, not in a postmortem.
///
/// GitHub charges the <c>first</c>/<c>last</c> values a query REQUESTS, multiplied
/// down each nesting path and divided by 100 (minimum one point) - not the number
/// of nodes that come back. A query asking for 50 labels on each of 50 issues has
/// already been charged for 2,500 labels before GitHub has looked at a single one.
/// So the cost is a property of the source text, which means it can be asserted
/// without a network call, which is what <see cref="CountNodes"/> is for.
/// </summary>
public static class GraphQlCost
{
    /// <summary>
    /// The primary hourly GraphQL budget for a personal access token. Observed
    /// directly in <c>X-Ratelimit-Limit</c> on 2026-09-05.
    /// </summary>
    public const int HourlyBudget = 5000;

    /// <summary>
    /// GitHub's published conversion: nodes requested, divided by 100, rounded up,
    /// never less than one point.
    /// </summary>
    public static int PointsForNodes(int nodes) =>
        nodes <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(nodes / 100.0));

    // "first: 25", "last: 10", "first: $blockers" - the paging arguments GitHub
    // charges for. The leading guard keeps the operation SIGNATURE out of it:
    // "$first: Int!" declares a variable, it does not request 'first' of anything.
    private static readonly Regex PageArgument = new(
        @"(?<![$\w])(?:first|last)\s*:\s*(?<value>\d+|\$\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "nodes(ids: $ids)" - a node list whose size is the length of the id list the
    // caller passes, so it is charged like a page size even though it never says
    // "first". Same guard, for the same reason: "$ids: [ID!]!" is a declaration.
    private static readonly Regex IdListArgument = new(
        @"(?<![$\w])ids\s*:\s*\$(?<name>\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The number of nodes a query asks GitHub for.
    ///
    /// Walks the selection sets and multiplies each connection's page size by the
    /// page sizes of the connections enclosing it, which is GitHub's documented
    /// rule. <paramref name="variableSizes"/> supplies the value of any page size
    /// given as a variable, keyed by variable name WITHOUT the '$' - and the size
    /// of any id list, so <c>nodes(ids: $ids)</c> is charged for the ids actually
    /// sent rather than for one node.
    ///
    /// A page size this method cannot resolve - a variable with no entry - is
    /// counted as one rather than skipped. Understating a cost is the failure this
    /// whole type exists to prevent, so an unknown is charged as a connection
    /// rather than as nothing.
    /// </summary>
    public static int CountNodes(string graphQlQuery, IReadOnlyDictionary<string, int>? variableSizes = null)
    {
        ArgumentNullException.ThrowIfNull(graphQlQuery);

        var total = 0;
        var multipliers = new Stack<int>();
        multipliers.Push(1);

        // Everything since the last brace: the field name, its arguments and any
        // directives, which is exactly the text that says what this selection set
        // is charged for.
        var header = new StringBuilder();
        var inString = false;
        var escaped = false;
        var inComment = false;

        foreach (var current in graphQlQuery)
        {
            if (inComment)
            {
                if (current is '\n' or '\r')
                {
                    inComment = false;
                }

                continue;
            }

            if (inString)
            {
                // Deliberately not appended. A string ARGUMENT can contain the text
                // "first: 500" - a search term, a title - and a header that carries
                // it would charge the query for a page nobody requested.
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (current)
            {
                case '#':
                    inComment = true;
                    break;

                case '"':
                    inString = true;
                    break;

                case '{':
                {
                    var enclosing = multipliers.Peek();
                    var requested = RequestedNodes(header.ToString(), variableSizes);
                    var multiplier = requested is { } page ? enclosing * page : enclosing;
                    if (requested is not null)
                    {
                        total += multiplier;
                    }

                    multipliers.Push(multiplier);
                    header.Clear();
                    break;
                }

                case '}':
                    // Guarded rather than trusted: a malformed query must produce a
                    // number, not an exception, because the caller asking is a test
                    // whose job is to report the cost it found.
                    if (multipliers.Count > 1)
                    {
                        multipliers.Pop();
                    }

                    header.Clear();
                    break;

                default:
                    header.Append(current);
                    break;
            }
        }

        return total;
    }

    /// <summary>
    /// The points a query costs, from its text and the sizes of its list variables.
    /// </summary>
    public static int PointsFor(string graphQlQuery, IReadOnlyDictionary<string, int>? variableSizes = null) =>
        PointsForNodes(CountNodes(graphQlQuery, variableSizes));

    private static int? RequestedNodes(string header, IReadOnlyDictionary<string, int>? variableSizes)
    {
        var page = PageArgument.Match(header);
        if (page.Success)
        {
            var value = page.Groups["value"].Value;
            if (value.StartsWith('$'))
            {
                return Resolve(value[1..], variableSizes);
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var literal)
                ? literal
                : 1;
        }

        var ids = IdListArgument.Match(header);
        return ids.Success ? Resolve(ids.Groups["name"].Value, variableSizes) : null;
    }

    private static int Resolve(string variableName, IReadOnlyDictionary<string, int>? variableSizes) =>
        variableSizes is not null && variableSizes.TryGetValue(variableName, out var size) && size > 0
            ? size
            : 1;
}

/// <summary>
/// One GraphQL read the plane makes, and how often it makes it. The point of
/// naming the call rate is that a query's cost is meaningless on its own: the
/// candidate scan that exhausted the budget cost 46 points, which is nothing, and
/// 8,280 points an hour, which is 1.7x the entire allowance.
/// </summary>
/// <param name="Name">What this read is, in the words the code uses for it.</param>
/// <param name="Nodes">Nodes requested per call, from <see cref="GraphQlCost.CountNodes"/>.</param>
/// <param name="CallsPerHour">How many times an hour the plane issues it in steady state.</param>
/// <param name="Assumption">Why the call rate is what it is, so a reader can dispute the number rather than the total.</param>
public sealed record GraphQlReadCost(string Name, int Nodes, double CallsPerHour, string Assumption)
{
    public int PointsPerCall => GraphQlCost.PointsForNodes(Nodes);

    public double PointsPerHour => PointsPerCall * CallsPerHour;
}

/// <summary>
/// The modelled steady-state hourly GraphQL cost, itemised. Itemised because a
/// single number that fails a build tells nobody which query to change.
/// </summary>
public sealed record GraphQlHourlyCost(IReadOnlyList<GraphQlReadCost> Reads)
{
    public double PointsPerHour => Reads.Sum(read => read.PointsPerHour);

    public double PercentOfBudget => PointsPerHour * 100.0 / GraphQlCost.HourlyBudget;

    /// <summary>The arithmetic, in the form a pull request description can carry.</summary>
    public string Describe()
    {
        var lines = new StringBuilder();
        foreach (var read in Reads.OrderByDescending(read => read.PointsPerHour))
        {
            lines.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1} nodes -> {2} points x {3:0.##}/hour = {4:0.##} points/hour ({5})",
                read.Name,
                read.Nodes,
                read.PointsPerCall,
                read.CallsPerHour,
                read.PointsPerHour,
                read.Assumption));
        }

        lines.Append(string.Format(
            CultureInfo.InvariantCulture,
            "total: {0:0.##} points/hour, {1:0.#}% of the {2}-point budget",
            PointsPerHour,
            PercentOfBudget,
            GraphQlCost.HourlyBudget));
        return lines.ToString();
    }
}
