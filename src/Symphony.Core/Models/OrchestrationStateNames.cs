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
}
