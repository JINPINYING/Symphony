namespace Symphony.Core.Models;

public static class RunStatusNames
{
    public const string Running = "running";
    public const string Retrying = "retrying";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
    public const string Stalled = "stalled";
    public const string CanceledByReconciliation = "canceled_by_reconciliation";
    public const string ReleasedIneligible = "released_ineligible";
    public const string NeedsCommandCenter = "needs_command_center";

    // Terminal outcome for a run whose escalation was resolved by an explicit
    // command-center directive (M3): the directive either re-dispatched the issue
    // as a fresh run or closed it.
    public const string ResolvedByDirective = "resolved_by_directive";

    // Terminal outcome for a run whose escalation stopped being real on its own:
    // the pull request it was escalated over reached a terminal state, so there
    // is no longer a decision to make. Kept distinct from ResolvedByDirective so
    // the record says HOW it ended - a person answering, or the question simply
    // going away - which is exactly the attribution this system keeps needing.
    public const string ResolvedByPhaseClear = "resolved_by_phase_clear";

    // Terminal outcome for a run whose escalation could not be acted on because
    // the source issue stopped being readable: a directive was posted and was
    // valid, but every attempt to reload the issue from the repository the run
    // records came back empty - deleted, transferred, or recorded against a
    // repository it no longer lives in. Kept distinct from ResolvedByDirective
    // because nothing was resolved: it says the plane stopped asking, and why.
    public const string AbandonedUnreadableIssue = "abandoned_unreadable_issue";
}

public static class RunPhaseNames
{
    public const string Implementation = "implementation";
    public const string Verify = "verify";
    public const string Review = "review";
    public const string FinalReview = "final_review";
}

public static class RetryDelayTypes
{
    public const string Continuation = "continuation";
    public const string Backoff = "backoff";
}

public static class RunStopReasons
{
    public const string Terminal = "terminal";
    public const string Inactive = "inactive";
    public const string Stalled = "stalled";
    public const string Shutdown = "shutdown";

    // The startup guard has spent the whole pre-session attempt budget. This is
    // terminal, not stalled: stopping such a run as "stalled" schedules a retry
    // that the claim store then fences forever, leaving the run in 'retrying'
    // with an elapsed due_at and no route to any terminal status. That takes the
    // whole plane offline, because a reserved issue holds its slot and nothing
    // reconciles a reservation that is still nominally active (ADCP#23).
    public const string StartupExhausted = "startup_exhausted";
}
