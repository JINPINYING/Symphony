using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Host.Services;

/// <summary>What one worker is doing, or was last doing.</summary>
public sealed record StaffMember(
    string Runner,
    string Role,
    string State,
    string? IssueIdentifier,
    string? Phase,
    string Activity,
    double? ElapsedSeconds,
    int? TurnCount,
    int? TotalTokens,
    string? LastMessage);

/// <summary>
/// The workforce view: who is working, on what, and for how long.
///
/// The rest of the page answers "does anything need me?" and "is the machine
/// healthy?". This answers a different and more natural question - what is my
/// team doing right now - and it is the question an operator actually opens a
/// dashboard to ask.
///
/// A worker with no current run is reported as idle WITH its last known job,
/// because "idle" alone is indistinguishable from "broken and never started".
/// Showing what it last finished, and when, is the difference.
/// </summary>
public static class StaffSummary
{
    public const string StateWorking = "working";
    public const string StateIdle = "idle";
    public const string StateWaiting = "waiting";
    public const string StateLate = "late";

    // "The team" means everyone who acts on this project, not only the workers the
    // plane can dispatch to. Built from runners alone, the panel answered "what is
    // my team doing" with one row while a scheduler was triaging every fifteen
    // minutes, two interactive sessions were writing code outside the queue, and
    // decisions were sitting with the owner.
    public const string RoleRunner = "runner";
    public const string RoleScheduler = "scheduler";
    public const string RoleSession = "session";
    public const string RoleOwner = "owner";

    /// <summary>How recently an interactive session must have reported to count
    /// as working. Matches the activity feed's own live window, so the two
    /// surfaces cannot disagree about who is active.</summary>
    public static readonly TimeSpan SessionLiveWindow = AgentActivity.LiveWindow;

    /// <summary>How long a session stays on the team after it goes quiet. Long
    /// enough to survive a slow turn, short enough that yesterday's session is
    /// not still listed as a colleague.</summary>
    public static readonly TimeSpan SessionMemory = TimeSpan.FromHours(2);

    public static IReadOnlyList<StaffMember> Build(
        IReadOnlyList<string> configuredRunners,
        IReadOnlyList<RunEntity> activeRuns,
        IReadOnlyList<RunEntity> recentRuns,
        DateTimeOffset now,
        IReadOnlyList<WatchedTaskReport>? schedulers = null,
        IReadOnlyList<AgentActivityReport>? sessions = null,
        int decisionsWaitingOnOwner = 0)
    {
        var members = new List<StaffMember>();

        foreach (var runner in configuredRunners.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            var active = activeRuns.FirstOrDefault(run =>
                string.Equals(run.Runner, runner, StringComparison.OrdinalIgnoreCase));

            if (active is not null)
            {
                members.Add(new StaffMember(
                    Runner: runner,
                    Role: RoleRunner,
                    State: StateWorking,
                    IssueIdentifier: active.IssueIdentifier,
                    Phase: active.Phase,
                    Activity: DescribePhase(active.Phase, active.IssueIdentifier),
                    ElapsedSeconds: Math.Max((now - active.StartedAtUtc).TotalSeconds, 0d),
                    TurnCount: active.TurnCount,
                    TotalTokens: active.TotalTokens,
                    LastMessage: DashboardEventPresentation.GetVisibleMessage(active.LastEvent, active.LastMessage)));
                continue;
            }

            // Idle. Say what it last did, so idle is distinguishable from broken.
            var last = recentRuns
                .Where(run => string.Equals(run.Runner, runner, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(run => run.LastEventAtUtc ?? run.StartedAtUtc)
                .FirstOrDefault();

            members.Add(new StaffMember(
                Runner: runner,
                Role: RoleRunner,
                State: StateIdle,
                IssueIdentifier: last?.IssueIdentifier,
                Phase: last?.Phase,
                Activity: last is null
                    ? "Never dispatched"
                    : $"Last worked {last.IssueIdentifier} - {DescribeOutcome(last.Status)}",
                ElapsedSeconds: last is null ? null : Math.Max((now - (last.LastEventAtUtc ?? last.StartedAtUtc)).TotalSeconds, 0d),
                TurnCount: null,
                TotalTokens: null,
                LastMessage: null));
        }

        members.AddRange(BuildSchedulers(schedulers ?? [], now));
        members.AddRange(BuildSessions(sessions ?? [], now));
        members.Add(BuildOwner(decisionsWaitingOnOwner));

        return members;
    }

    // Schedulers act on the project without being dispatched to, and their silence
    // is the failure nobody notices - the artifact publisher stopped for 27 hours
    // while the page reported a calm system.
    private static IEnumerable<StaffMember> BuildSchedulers(
        IReadOnlyList<WatchedTaskReport> schedulers,
        DateTimeOffset now) =>
        schedulers.Select(task => new StaffMember(
            Runner: task.Name,
            Role: RoleScheduler,
            // Health answers "is this scheduler still being started?", which is not
            // the same question as "is it running right now". Mapping only on
            // health produced a row reading "Idle - Currently running, started less
            // than a minute ago", where the state and the sentence beside it
            // contradicted each other. The scheduler's own Status is the authority
            // on whether it is executing.
            State: string.Equals(task.Status, "Running", StringComparison.OrdinalIgnoreCase)
                ? StateWorking
                : task.Health switch
                {
                    WatchedTaskReport.HealthOk => StateIdle,
                    _ => StateLate
                },
            IssueIdentifier: null,
            Phase: null,
            Activity: task.Explanation,
            ElapsedSeconds: task.LastRunUtc is null ? null : Math.Max((now - task.LastRunUtc.Value).TotalSeconds, 0d),
            TurnCount: null,
            TotalTokens: null,
            LastMessage: null));

    // Interactive sessions - the work done beside the queue rather than in it.
    //
    // Grouped by the name they report under, which is the only identity the feed
    // carries: two sessions reporting as "Claude" appear as one member. That is a
    // real limit and the honest presentation of it is one row per name, never a
    // count of sessions the data cannot support.
    private static IEnumerable<StaffMember> BuildSessions(
        IReadOnlyList<AgentActivityReport> sessions,
        DateTimeOffset now) =>
        sessions
            .Where(report => now - report.AtUtc <= SessionMemory)
            .GroupBy(report => report.Actor, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(report => report.AtUtc).First())
            .OrderBy(report => report.Actor, StringComparer.OrdinalIgnoreCase)
            .Select(report => new StaffMember(
                Runner: report.Actor,
                Role: RoleSession,
                State: now - report.AtUtc <= SessionLiveWindow ? StateWorking : StateIdle,
                IssueIdentifier: null,
                Phase: null,
                Activity: report.Summary,
                ElapsedSeconds: Math.Max((now - report.AtUtc).TotalSeconds, 0d),
                TurnCount: null,
                TotalTokens: null,
                LastMessage: null));

    // The owner is on the team because decisions are work, and a panel that leaves
    // them off implies the queue is the whole project.
    private static StaffMember BuildOwner(int decisionsWaiting) => new(
        Runner: "You",
        Role: RoleOwner,
        State: decisionsWaiting > 0 ? StateWaiting : StateIdle,
        IssueIdentifier: null,
        Phase: null,
        Activity: decisionsWaiting > 0
            ? $"{decisionsWaiting} decision{(decisionsWaiting == 1 ? string.Empty : "s")} waiting on you"
            : "Nothing is waiting on you",
        ElapsedSeconds: null,
        TurnCount: null,
        TotalTokens: null,
        LastMessage: null);

    private static string DescribePhase(string? phase, string issue) => phase switch
    {
        RunPhaseNames.Implementation => $"Writing the change for {issue}",
        RunPhaseNames.Verify => $"Verifying {issue} against CI",
        RunPhaseNames.Review => $"Reviewing {issue}",
        RunPhaseNames.FinalReview => $"Final review of {issue} after repair",
        _ => $"Working on {issue}"
    };

    private static string DescribeOutcome(string status) => status switch
    {
        RunStatusNames.Succeeded => "finished",
        RunStatusNames.Failed => "failed",
        RunStatusNames.Stalled => "stalled",
        RunStatusNames.NeedsCommandCenter => "escalated to you",
        RunStatusNames.CanceledByReconciliation => "cancelled, work already done",
        _ => status.Replace('_', ' ')
    };
}
