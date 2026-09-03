namespace Symphony.Host.Services;

/// <summary>
/// Remembers how each watched task's previous run ended, so the page can tell a
/// blip from a streak.
///
/// WHY THIS EXISTS. Task Scheduler reports one sample: the last run and its exit
/// code. From one sample the only honest statement is about that one run - and
/// yet the panel used to answer it with "it will keep failing on the same
/// schedule until the cause is fixed", escalate to its worst level, and tell the
/// owner nothing new would be picked up until they fixed it. On 2026-09-02 all of
/// that was said about a run a deploy had killed, six minutes before the next run
/// exited zero unattended.
///
/// A prediction needs more than one observation. This holds the previous one, so
/// the difference between "a run failed" and "runs keep failing" is something the
/// engine has actually seen rather than something it assumed.
///
/// Singleton, because a streak is only meaningful across polls. Resetting on
/// restart is correct rather than merely tolerable: a process that has just
/// started has observed exactly one run and should not inherit a claim about the
/// next one. Same reasoning as <see cref="TrackerReachability"/>.
/// </summary>
public sealed class WatchedTaskHistory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Observation> _byTask = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Record what this report says about the task's latest run, and return the
    /// report the history can justify.
    ///
    /// Idempotent per run. The reader is polled by both the dashboard and the tick
    /// loop, so the SAME failed run is seen many times over; a streak advances only
    /// when the last-run timestamp moves, which is the only evidence available that
    /// a different run happened.
    /// </summary>
    public WatchedTaskReport Observe(WatchedTaskReport report)
    {
        // Only the failure lane carries a streak. Disabled, late and unknown are
        // judged on a single sample by design - they are statements about the
        // present, not predictions about the next run.
        var failed = report.Health is WatchedTaskReport.HealthRecovering or WatchedTaskReport.HealthFailing;
        var key = string.IsNullOrWhiteSpace(report.Path) ? report.Name : report.Path;

        int streak;
        lock (_gate)
        {
            _byTask.TryGetValue(key, out var previous);

            if (previous is not null && previous.LastRunUtc == report.LastRunUtc)
            {
                // The same run, seen again. Nothing new has been observed, so the
                // count must not move - otherwise a single failed run would be
                // promoted to a streak purely by the dashboard being open.
                streak = failed ? previous.ConsecutiveFailures : 0;
            }
            else
            {
                streak = failed ? (previous?.ConsecutiveFailures ?? 0) + 1 : 0;
            }

            _byTask[key] = new Observation(report.LastRunUtc, streak);
        }

        return streak > 1 ? WatchedTaskEvaluator.EscalateToFailing(report, streak) : report;
    }

    private sealed record Observation(DateTimeOffset? LastRunUtc, int ConsecutiveFailures);
}
