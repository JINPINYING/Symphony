namespace Symphony.Core.Models;

/// <summary>
/// What GitHub said about the budget on one response, taken from the
/// <c>x-ratelimit-*</c> headers it puts on every answer.
///
/// WHY FROM HEADERS. On 2026-09-05 the GraphQL budget was exhausted -
/// <c>X-Ratelimit-Used: 5011</c> against <c>X-Ratelimit-Limit: 5000</c> - while
/// <c>gh api rate_limit</c> read 5,000 remaining at the same moment, because its
/// top-level <c>rate</c> block is the CORE budget under another name and the
/// GraphQL figure sits at <c>.resources.graphql</c>. It is also a separate call
/// that has to be made and remembered. The headers ride on the calls the plane is
/// already making, name their own resource, and cost nothing.
/// </summary>
/// <param name="Resource">
/// <c>x-ratelimit-resource</c>: "graphql", "core", "search". The two budgets are
/// separate and exhausting either one blinds different things, so a reading that
/// does not say which it describes is not a reading.
/// </param>
/// <param name="Limit">The whole allowance for the current window.</param>
/// <param name="Used">Spent so far in the current window.</param>
/// <param name="Remaining">Left in the current window.</param>
/// <param name="ResetAtUtc">When the window rolls over, from <c>x-ratelimit-reset</c>.</param>
/// <param name="ObservedAtUtc">When this response arrived, so a burn rate can be computed across two of them.</param>
public sealed record GitHubRateLimitReading(
    string Resource,
    int Limit,
    int Used,
    int Remaining,
    DateTimeOffset? ResetAtUtc,
    DateTimeOffset ObservedAtUtc)
{
    public const string GraphQlResource = "graphql";
    public const string RestResource = "core";

    /// <summary>
    /// How much of the window has been spent, 0-100. Reported from
    /// <see cref="Used"/> against <see cref="Limit"/> rather than from
    /// <see cref="Remaining"/>, because GitHub has been seen to report Used above
    /// Limit (5011/5000) and the overshoot is exactly the interesting part.
    /// </summary>
    public double UsedPercent => Limit <= 0 ? 0 : Math.Round(Used * 100.0 / Limit, 1);
}
