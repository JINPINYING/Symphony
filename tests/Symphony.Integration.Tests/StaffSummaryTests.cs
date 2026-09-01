using Symphony.Core.Models;
using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Integration.Tests;

// "What the team is doing" is the question an operator actually opens a dashboard
// to ask, so a team that is short a member answers it wrongly no matter how
// accurate the rest of the row is.
public sealed class StaffSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 20, 0, 0, TimeSpan.Zero);

    private static RunEntity Run(string runner, string issue, string status) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        IssueId = "issue-" + issue,
        IssueIdentifier = issue,
        Runner = runner,
        Status = status,
        Phase = RunPhaseNames.Implementation,
        StartedAtUtc = Now.AddMinutes(-18),
        LastEventAtUtc = Now.AddMinutes(-1)
    };

    [Fact]
    public void EveryConfiguredRunnerAppears()
    {
        var staff = StaffSummary.Build(
            ["claude", "codex"],
            activeRuns: [Run("claude", "#128", RunStatusNames.Running)],
            recentRuns: [],
            Now);

        Assert.Equal(["claude", "codex"], staff.Where(m => m.Role == StaffSummary.RoleRunner).Select(m => m.Runner));
    }

    // The one that was wrong on the page: with runner_by_label emptied and both
    // lanes pointed at one vendor, the team rendered as a team of one - while the
    // fallback vendor was still reviewing everything the implementer produced.
    [Fact]
    public void AWorkingImplementerDoesNotHideAnIdleTeammate()
    {
        var staff = StaffSummary.Build(
            ["claude", "codex"],
            activeRuns: [Run("claude", "#128", RunStatusNames.Running)],
            recentRuns: [Run("codex", "#111", RunStatusNames.Succeeded)],
            Now);

        Assert.Equal(2, staff.Count(m => m.Role == StaffSummary.RoleRunner));
        Assert.Equal(StaffSummary.StateWorking, staff.Single(m => m.Runner == "claude").State);
        Assert.Equal(StaffSummary.StateIdle, staff.Single(m => m.Runner == "codex").State);
    }

    // "Idle" alone is indistinguishable from "broken and never started", so an
    // idle member has to say what it last did.
    [Fact]
    public void AnIdleMemberReportsItsLastJob()
    {
        var staff = StaffSummary.Build(
            ["codex"],
            activeRuns: [],
            recentRuns: [Run("codex", "#111", RunStatusNames.Succeeded)],
            Now);

        var codex = Assert.Single(staff, m => m.Role == StaffSummary.RoleRunner);
        Assert.Equal(StaffSummary.StateIdle, codex.State);
        Assert.Equal("#111", codex.IssueIdentifier);
    }

    [Fact]
    public void ARunnerThatHasNeverRunIsStillOnTheTeam()
    {
        var staff = StaffSummary.Build(["claude", "codex"], activeRuns: [], recentRuns: [], Now);

        var runners = staff.Where(m => m.Role == StaffSummary.RoleRunner).ToList();
        Assert.Equal(2, runners.Count);
        Assert.All(runners, m => Assert.Equal(StaffSummary.StateIdle, m.State));
    }

    [Fact]
    public void TheSameRunnerNamedTwiceIsOnePerson()
    {
        var staff = StaffSummary.Build(["claude", "Claude"], activeRuns: [], recentRuns: [], Now);

        Assert.Single(staff, m => m.Role == StaffSummary.RoleRunner);
    }

    // "The team" means everyone who acts on this project. Built from runners alone
    // the panel showed one row while a scheduler triaged every fifteen minutes, a
    // session wrote code beside the queue, and a decision sat with the owner.
    [Fact]
    public void TheTeamIsEveryoneWhoActsOnTheProject()
    {
        var staff = StaffSummary.Build(
            ["claude"],
            activeRuns: [],
            recentRuns: [],
            Now,
            schedulers: [new WatchedTaskReport("ADCP Commander", @"\ADCP Commander", "Enabled", "Ready",
                Now.AddMinutes(-3), 0, Now.AddMinutes(12), 15, WatchedTaskReport.HealthOk, "Last ran 3 minutes ago, on schedule.")],
            sessions: [new AgentActivityReport("Claude", "Rebasing the evidence lane.", null, null, Now.AddMinutes(-2))],
            decisionsWaitingOnOwner: 2);

        Assert.Contains(staff, m => m.Role == StaffSummary.RoleRunner && m.Runner == "claude");
        Assert.Contains(staff, m => m.Role == StaffSummary.RoleScheduler && m.Runner == "ADCP Commander");
        Assert.Contains(staff, m => m.Role == StaffSummary.RoleSession && m.State == StaffSummary.StateWorking);
        Assert.Contains(staff, m => m.Role == StaffSummary.RoleOwner && m.State == StaffSummary.StateWaiting);
    }

    // A scheduler that has stopped firing is the failure nobody notices, so it
    // must not sit on the team looking like an idle colleague.
    [Fact]
    public void ALateSchedulerReadsAsLateNotIdle()
    {
        var staff = StaffSummary.Build(
            [], activeRuns: [], recentRuns: [], Now,
            schedulers: [new WatchedTaskReport("Publisher", @"\Publisher", "Enabled", "Ready",
                Now.AddHours(-27), 0, null, 15, WatchedTaskReport.HealthLate, "Has not run for 1 day.")]);

        Assert.Equal(StaffSummary.StateLate, Assert.Single(staff, m => m.Role == StaffSummary.RoleScheduler).State);
    }

    // Yesterday's session is not a colleague today.
    [Fact]
    public void ASessionThatWentQuietLongAgoLeavesTheTeam()
    {
        var staff = StaffSummary.Build(
            [], activeRuns: [], recentRuns: [], Now,
            sessions: [new AgentActivityReport("Claude", "Stage 1 handed off.", null, null, Now.AddHours(-24))]);

        Assert.DoesNotContain(staff, m => m.Role == StaffSummary.RoleSession);
    }

    // The feed carries no session id, so two sessions reporting under one name are
    // one row. Stated as a limit rather than guessed at with a count.
    [Fact]
    public void SessionsSharingANameAreOneRow()
    {
        var staff = StaffSummary.Build(
            [], activeRuns: [], recentRuns: [], Now,
            sessions:
            [
                new AgentActivityReport("Claude", "newer", null, null, Now.AddMinutes(-1)),
                new AgentActivityReport("Claude", "older", null, null, Now.AddMinutes(-9))
            ]);

        var session = Assert.Single(staff, m => m.Role == StaffSummary.RoleSession);
        Assert.Equal("newer", session.Activity);
    }

    [Fact]
    public void TheOwnerIsOnTheTeamEvenWithNothingWaiting()
    {
        var staff = StaffSummary.Build([], activeRuns: [], recentRuns: [], Now);

        var owner = Assert.Single(staff, m => m.Role == StaffSummary.RoleOwner);
        Assert.Equal(StaffSummary.StateIdle, owner.State);
        Assert.Contains("Nothing is waiting", owner.Activity);
    }
}
