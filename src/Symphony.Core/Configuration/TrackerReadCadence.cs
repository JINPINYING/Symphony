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
    /// How often ONE active phase ledger re-reads its pull request from GitHub.
    ///
    /// This read used to ride the raw tick, which is 15 seconds, and it is three
    /// REST calls when the stage needs CI - a pull request, a combined commit
    /// status and a check-run listing. Four ledgers at 15 seconds is 2,880 core
    /// points an hour on its own, over half the budget, for a question whose
    /// answer is "still waiting" nearly every time. Nothing in the phase machine
    /// waits on GitHub evidence that moves faster than a couple of minutes: its
    /// own backstop is two hours, the candidate scan beside it is 60 seconds, and
    /// CI takes minutes.
    ///
    /// The interval is a floor on POLLING, not on progress: a ledger whose row
    /// has changed since its last read is re-read at once, so a stage transition
    /// is never made to wait for a clock.
    /// </summary>
    public static readonly TimeSpan PhaseLedgerPoll = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How often one escalated issue's comments are re-read looking for a
    /// command-center directive.
    ///
    /// Same shape as above, and worse per call: an escalated Symphony issue
    /// carries enough comments to page twice, so this was two core points per
    /// issue per tick - 480 an hour for one parked issue. A directive is a person
    /// typing, and a person does not notice sixty seconds.
    /// </summary>
    public static readonly TimeSpan EscalatedIssueDirectivePoll = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often the parked-run sweep asks GitHub whether runs with no ledger row
    /// have been overtaken by events.
    ///
    /// It only looks at runs that have already been parked for two hours, so a
    /// five-minute clock cannot make it late by anything that matters, and its
    /// two reads per repository per tick were pure repetition of an answer that
    /// changes on the scale of hours.
    /// </summary>
    public static readonly TimeSpan ParkedRunSweep = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The repository count the cost model assumes. Three is what the plane
    /// actually watches, and the multiplier that turned an affordable query into
    /// an unaffordable one on 2026-09-01 without anything recomputing the total.
    /// </summary>
    public const int ModelledRepositoryCount = 3;

    /// <summary>
    /// The most the modelled GraphQL steady state may cost per hour. Not the
    /// budget: the budget is 5,000 and a plane that plans to spend all of it has
    /// no room for the bursts it cannot model - a directive storm, a repair
    /// round, a startup sweep. 2,000 is 40% of the allowance, which leaves the
    /// rest for the work that is not steady state.
    ///
    /// Named for its resource since 2026-09-06. It used to be
    /// <c>ModelledHourlyCeiling</c>, as though the plane had one budget, during
    /// the five days in which four fixes moved load off GraphQL and onto REST
    /// without anyone modelling REST at all.
    /// </summary>
    public const int ModelledGraphQlHourlyCeiling = 2000;

    /// <summary>
    /// The same ceiling for the <c>core</c> (REST) budget, which is a separate
    /// allowance of the same size and was the one actually being exhausted:
    /// 7,293 points an hour measured on 2026-09-06, against 5,000.
    ///
    /// Half the allowance. A steady state that plans to spend more than half
    /// leaves no room for the burst it cannot model, and the plane's bursts are
    /// real - a startup sweep, a repair round, and every <c>gh</c> call the
    /// commander makes with the same token, which spends this budget and appears
    /// in no model here.
    /// </summary>
    public const int ModelledCoreHourlyCeiling = 2500;

    public static double CallsPerHour(TimeSpan interval) =>
        interval <= TimeSpan.Zero ? 0 : TimeSpan.FromHours(1) / interval;
}
