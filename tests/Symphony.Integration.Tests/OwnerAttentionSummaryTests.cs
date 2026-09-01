using Symphony.Core.Models;
using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Integration.Tests;

// This is the first thing on the page and the only thing most visits read, so
// what it calls "clear" matters more than what it calls alarming. A status page
// that reports normal operation as a problem teaches its reader to ignore it.
public sealed class OwnerAttentionSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private static (string Level, string Headline, string Detail, IReadOnlyList<AttentionItem> Items) Build(
        bool healthy = true,
        IReadOnlyList<RunEntity>? escalated = null,
        int running = 0,
        int retrying = 0,
        IReadOnlyList<PhaseLedgerEntity>? phases = null,
        IReadOnlyList<OpenPullRequest>? openPullRequests = null,
        IReadOnlyList<AgentActivityReport>? agentActivity = null,
        IReadOnlyList<WatchedTaskReport>? watchedTasks = null,
        DateTimeOffset? lastEvent = null) =>
        OwnerAttentionSummary.Build(healthy, escalated ?? [], running, retrying, phases ?? [], openPullRequests ?? [], agentActivity ?? [], watchedTasks ?? [], lastEvent, Now);

    private static WatchedTaskReport Task(string name, string health) =>
        new(name, "\\" + name, "Enabled", "Ready", Now.AddMinutes(-5), 0, Now.AddMinutes(10), 15, health, $"{name} is {health}.");

    private static AgentActivityReport Activity(string summary, TimeSpan ago) =>
        new("Claude", summary, null, null, Now - ago);

    private static OpenPullRequest Pr(int number, string? checks, bool draft = false) =>
        new(number, $"Change {number}", $"https://example.invalid/pull/{number}", "someone", draft, checks, "MERGEABLE", Now.AddHours(-6));

    private static RunEntity Escalated(string issue, bool posted) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        IssueId = "issue-" + issue,
        IssueIdentifier = issue,
        Status = RunStatusNames.NeedsCommandCenter,
        EscalationPostedAtUtc = posted ? Now.AddMinutes(-5) : null,
    };

    // The bug this whole input exists to fix: every other signal here is the
    // engine's own run state, so an empty queue read as "nothing needs you" while
    // several green pull requests sat open waiting for a merge decision.
    [Fact]
    public void AGreenPullRequestIsNotAnIdlePlane()
    {
        var result = Build(openPullRequests: [Pr(105, "SUCCESS")], lastEvent: Now.AddHours(-3));

        Assert.Equal(OwnerAttentionSummary.LevelAttention, result.Level);
        Assert.Equal("One thing is waiting on you", result.Headline);
        Assert.Contains(result.Items, i => i.Label == "PR #105 is waiting on you");
    }

    [Fact]
    public void FailingChecksSaySo()
    {
        var result = Build(openPullRequests: [Pr(106, "FAILURE")]);

        var item = Assert.Single(result.Items);
        Assert.Equal("PR #106 has failing checks", item.Label);
        Assert.Contains("CI is red", item.Detail);
    }

    [Fact]
    public void DraftsAndPendingChecksAreNotWaitingOnAnyone()
    {
        // A draft is the author's to finish and a pending check resolves itself.
        // Listing either is how a status page starts crying wolf.
        var result = Build(openPullRequests:
        [
            Pr(1, "SUCCESS", draft: true),
            Pr(2, "PENDING"),
            Pr(3, "EXPECTED")
        ]);

        Assert.Equal(OwnerAttentionSummary.LevelClear, result.Level);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void APullRequestWithNoChecksStillCounts()
    {
        // A repository without CI is not a repository without merge decisions.
        var result = Build(openPullRequests: [Pr(21, null)]);

        Assert.Contains(result.Items, i => i.Label == "PR #21 is waiting on you");
    }

    [Fact]
    public void SeveralPullRequestsAreCountedIndividually()
    {
        var result = Build(openPullRequests: [Pr(106, "SUCCESS"), Pr(105, "SUCCESS")]);

        Assert.Equal("2 things are waiting on you", result.Headline);
        // Listed by number so the page does not reshuffle between polls.
        Assert.Equal(["PR #105 is waiting on you", "PR #106 is waiting on you"], result.Items.Select(i => i.Label));
    }

    // An empty queue is not an idle project. An agent working outside the queue
    // used to render as "the plane is idle" - the page being confidently wrong
    // about the only question it is asked.
    [Fact]
    public void AnAgentWorkingOutsideTheQueueIsNotIdle()
    {
        var result = Build(
            agentActivity: [Activity("Rebasing the voice provider evidence lane.", TimeSpan.FromMinutes(2))],
            lastEvent: Now.AddHours(-3));

        Assert.Equal(OwnerAttentionSummary.LevelClear, result.Level);
        Assert.Equal("Claude is working", result.Headline);
        Assert.Contains("Rebasing the voice provider evidence lane.", result.Detail);
        Assert.DoesNotContain("idle", result.Detail);
    }

    [Fact]
    public void AStaleReportDoesNotKeepClaimingWorkIsUnderway()
    {
        // A session that dies without saying goodbye must not leave the page
        // asserting forever that something is happening.
        var result = Build(
            agentActivity: [Activity("Started something and then vanished.", TimeSpan.FromHours(4))],
            lastEvent: Now.AddHours(-4));

        Assert.Equal("Nothing needs you", result.Headline);
    }

    [Fact]
    public void AWaitingPullRequestOutranksAWorkingAgent()
    {
        // Work in progress is information; a decision only the owner can make is
        // the reason they opened the page.
        var result = Build(
            openPullRequests: [Pr(105, "SUCCESS")],
            agentActivity: [Activity("Still going.", TimeSpan.FromMinutes(1))]);

        Assert.Equal("One thing is waiting on you", result.Headline);
    }

    [Fact]
    public void IdleIsClear_NotAProblem()
    {
        // The plane spends most of its life here. Calling it a fault would be wrong
        // and would train the owner to stop reading the page.
        var result = Build(lastEvent: Now.AddHours(-3));

        Assert.Equal(OwnerAttentionSummary.LevelClear, result.Level);
        Assert.Equal("Nothing needs you", result.Headline);
        Assert.Contains("idle by construction", result.Detail);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void WorkingIsClear_Too()
    {
        var result = Build(running: 2);

        Assert.Equal(OwnerAttentionSummary.LevelClear, result.Level);
        Assert.Equal("Working on 2 issues", result.Headline);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void AnEscalationThatPostedNeedsADecision_NotAnAlarm()
    {
        var result = Build(escalated: [Escalated("#63", posted: true)]);

        Assert.Equal(OwnerAttentionSummary.LevelAttention, result.Level);
        Assert.Equal("One thing is waiting on you", result.Headline);
        var item = Assert.Single(result.Items);
        Assert.Contains("#63", item.Label);
        Assert.Contains("symphony:directive", item.Detail);
    }

    [Fact]
    public void AnEscalationThatFailedToPostIsBlocking()
    {
        // Worse than needing a decision: the owner was never told, so it would sit
        // silently. That is a failure of the notification path itself.
        var result = Build(escalated: [Escalated("#82", posted: false)]);

        Assert.Equal(OwnerAttentionSummary.LevelDown, result.Level);
        Assert.Equal(OwnerAttentionSummary.LevelDown, Assert.Single(result.Items).Severity);
    }

    [Fact]
    public void AnUnhealthyEngineOutranksEverythingElse()
    {
        var result = Build(healthy: false, escalated: [Escalated("#63", posted: true)]);

        Assert.Equal(OwnerAttentionSummary.LevelDown, result.Level);
        Assert.Contains("will not clear itself", result.Headline);
        Assert.Equal("The engine is not answering", result.Items[0].Label);
    }

    [Fact]
    public void APhaseStoppedAtTheMergeGateIsSurfaced()
    {
        var result = Build(phases: [new PhaseLedgerEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = "#95",
            Stage = PhaseStages.Escalated,
            PrNumber = 96,
        }]);

        Assert.Equal(OwnerAttentionSummary.LevelAttention, result.Level);
        var item = Assert.Single(result.Items);
        Assert.Contains("#95", item.Label);
        Assert.Contains("protected path", item.Detail);
    }

    [Fact]
    public void MergedAndReadyPhasesAreNotSurfaced()
    {
        // Only the escalated stage needs a person; the rest of the machine moves on
        // by itself and must not clutter the summary.
        var result = Build(phases: [
            new PhaseLedgerEntity { IssueId = "a", IssueIdentifier = "#95", Stage = PhaseStages.Merged, PrNumber = 96 },
            new PhaseLedgerEntity { IssueId = "b", IssueIdentifier = "#97", Stage = PhaseStages.Ready,  PrNumber = 98 }
        ]);

        Assert.Equal(OwnerAttentionSummary.LevelClear, result.Level);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void ItemsAccumulateAndThePluralReads()
    {
        var result = Build(
            escalated: [Escalated("#63", posted: true), Escalated("#82", posted: true)],
            retrying: 3);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal("3 things are waiting on you", result.Headline);
        Assert.Contains(result.Items, i => i.Label.StartsWith("3 runs waiting", StringComparison.Ordinal));
    }

    // The 27-hour blind spot. Every other input to this summary is the engine's
    // own state, and the engine's state is identical whether the queue is empty
    // because there is no work or because the scheduler that finds work stopped
    // firing. Before this, the page said "nothing needs you" throughout.
    [Fact]
    public void AScheduleThatStoppedFiringIsNotAQuietPlane()
    {
        var result = Build(watchedTasks: [Task("ADCP Commander", WatchedTaskReport.HealthLate)]);

        Assert.Equal(OwnerAttentionSummary.LevelAttention, result.Level);
        var item = Assert.Single(result.Items);
        Assert.Equal("ADCP Commander is not running as scheduled", item.Label);
    }

    // Disabled and failing will not recover on their own, so they outrank a run
    // that is merely late - which a busy host can cause without anything at all
    // being wrong.
    [Theory]
    [InlineData(WatchedTaskReport.HealthDisabled)]
    [InlineData(WatchedTaskReport.HealthFailing)]
    public void ATaskThatCannotRecoverOnItsOwnReadsAsDown(string health)
    {
        var result = Build(watchedTasks: [Task("ADCP Event Watcher", health)]);

        Assert.Equal(OwnerAttentionSummary.LevelDown, result.Level);
    }

    [Fact]
    public void TheWorstSchedulerLeads()
    {
        var result = Build(watchedTasks:
        [
            Task("Late one", WatchedTaskReport.HealthLate),
            Task("Stopped one", WatchedTaskReport.HealthDisabled)
        ]);

        Assert.Equal(OwnerAttentionSummary.LevelDown, result.Level);
        Assert.StartsWith("Stopped one", result.Items[0].Label);
    }

    // The point of the whole feature is that silence is legible - which means a
    // healthy scheduler must stay silent, or the page fills with cron noise and
    // stops being read.
    [Fact]
    public void HealthySchedulersSayNothing()
    {
        var result = Build(watchedTasks:
        [
            Task("ADCP Commander", WatchedTaskReport.HealthOk),
            Task("ADCP Event Watcher", WatchedTaskReport.HealthOk)
        ]);

        Assert.Equal(OwnerAttentionSummary.LevelClear, result.Level);
        Assert.Empty(result.Items);
    }
}
