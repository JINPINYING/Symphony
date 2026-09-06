namespace Symphony.Core.Models;

/// <summary>
/// One call the plane made to GitHub, and which budget it was charged to.
///
/// WHY THIS EXISTS. Four fixes in five days each moved load off the budget that
/// was visibly hurting and onto the other one, and none of them measured the
/// destination: on 2026-09-06 the <c>core</c> (REST) budget was burning 7,293
/// points an hour against a 5,000 limit while <c>graphql</c> sat at 4,477. The
/// budget headers say how much has been spent; they do not say BY WHAT. Without
/// that, the next fix moves the load a third time.
///
/// A REST call costs exactly one point, so counting calls per site IS the
/// attribution - no estimate is involved. A call answered <c>304 Not Modified</c>
/// costs nothing, which is why <see cref="Charged"/> is recorded rather than
/// inferred from the call happening.
/// </summary>
/// <param name="CallSite">
/// The read this call belongs to, in the words the code uses for it. The cost
/// model and the runtime share these names, so a modelled line and a measured
/// line can be put beside each other.
/// </param>
/// <param name="Resource">
/// The budget GitHub charged, from <c>x-ratelimit-resource</c>: "core",
/// "graphql", "search". <see cref="UnknownResource"/> when the response carried
/// no such header - a proxy that strips it leaves the call real and its budget
/// unknown, and unknown must not be silently filed under either one.
/// </param>
/// <param name="Charged">
/// False when GitHub answered <c>304 Not Modified</c> to a conditional request.
/// GitHub does not charge a 304 against the primary rate limit, and the ledger
/// this feeds proves that from the headers rather than trusting it.
/// </param>
/// <param name="ObservedAtUtc">When the response arrived, so a rate can be computed across calls.</param>
public sealed record GitHubApiCall(
    string CallSite,
    string Resource,
    bool Charged,
    DateTimeOffset ObservedAtUtc)
{
    /// <summary>
    /// The resource of a response that did not say. Named rather than left blank
    /// so a reader of the attribution can see that these calls were spent
    /// somewhere and that the somewhere was not observed.
    /// </summary>
    public const string UnknownResource = "unknown";
}
