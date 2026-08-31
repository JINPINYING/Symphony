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
        DateTimeOffset? lastEvent = null) =>
        OwnerAttentionSummary.Build(healthy, escalated ?? [], running, retrying, phases ?? [], lastEvent, Now);

    private static RunEntity Escalated(string issue, bool posted) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        IssueId = "issue-" + issue,
        IssueIdentifier = issue,
        Status = RunStatusNames.NeedsCommandCenter,
        EscalationPostedAtUtc = posted ? Now.AddMinutes(-5) : null,
    };

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
}
