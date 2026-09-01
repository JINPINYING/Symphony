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

        Assert.Equal(["claude", "codex"], staff.Select(m => m.Runner));
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

        Assert.Equal(2, staff.Count);
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

        var codex = Assert.Single(staff);
        Assert.Equal(StaffSummary.StateIdle, codex.State);
        Assert.Equal("#111", codex.IssueIdentifier);
    }

    [Fact]
    public void ARunnerThatHasNeverRunIsStillOnTheTeam()
    {
        var staff = StaffSummary.Build(["claude", "codex"], activeRuns: [], recentRuns: [], Now);

        Assert.Equal(2, staff.Count);
        Assert.All(staff, m => Assert.Equal(StaffSummary.StateIdle, m.State));
    }

    [Fact]
    public void TheSameRunnerNamedTwiceIsOnePerson()
    {
        var staff = StaffSummary.Build(["claude", "Claude"], activeRuns: [], recentRuns: [], Now);

        Assert.Single(staff);
    }
}
