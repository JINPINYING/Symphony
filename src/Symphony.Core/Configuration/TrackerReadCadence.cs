namespace Symphony.Core.Configuration;

/// <summary>
/// How often the plane asks GitHub anything, and the ceiling that cadence has to
/// stay under.
///
/// These numbers used to live as private constants in the tick service, which is
/// where they are used - and nowhere near the arithmetic that decides whether
/// they are affordable. That separation is how the budget was exhausted three
/// times: each change moved one number, nothing recomputed the product, and the
/// first sign of trouble was the plane going blind. They are here so the cost
/// model and the runtime read the SAME values, and so changing a cadence changes
/// what the build asserts.
/// </summary>
public static class TrackerReadCadence
{
    /// <summary>How often candidate issues are re-read from the tracker.</summary>
    public static readonly TimeSpan CandidateScan = TimeSpan.FromSeconds(60);

    /// <summary>How often the tracked-issue cache re-reads state and labels.</summary>
    public static readonly TimeSpan TrackedIssueRefresh = TimeSpan.FromSeconds(60);

    /// <summary>How often open pull requests are listed.</summary>
    public static readonly TimeSpan OpenPullRequestPoll = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The repository count the cost model assumes. Three is what the plane
    /// actually watches, and the multiplier that turned an affordable query into
    /// an unaffordable one on 2026-09-01 without anything recomputing the total.
    /// </summary>
    public const int ModelledRepositoryCount = 3;

    /// <summary>
    /// The most the modelled steady state may cost per hour. Not the budget: the
    /// budget is 5,000 and a plane that plans to spend all of it has no room for
    /// the bursts it cannot model - a directive storm, a repair round, a startup
    /// sweep. 2,000 is 40% of the allowance, which leaves the rest for the work
    /// that is not steady state.
    /// </summary>
    public const int ModelledHourlyCeiling = 2000;

    public static double CallsPerHour(TimeSpan interval) =>
        interval <= TimeSpan.Zero ? 0 : TimeSpan.FromHours(1) / interval;
}
