using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Host.Services;

/// <summary>What one worker is doing, or was last doing.</summary>
public sealed record StaffMember(
    string Runner,
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

    public static IReadOnlyList<StaffMember> Build(
        IReadOnlyList<string> configuredRunners,
        IReadOnlyList<RunEntity> activeRuns,
        IReadOnlyList<RunEntity> recentRuns,
        DateTimeOffset now)
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

        return members;
    }

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
