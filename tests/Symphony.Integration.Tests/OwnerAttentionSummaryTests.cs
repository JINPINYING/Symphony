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
        TrackerReachabilitySnapshot? tracker = null,
        DateTimeOffset? lastEvent = null,
        string? primaryRepository = null,
        IReadOnlyCollection<string>? activeIssueIds = null) =>
        OwnerAttentionSummary.Build(healthy, escalated ?? [], running, retrying, phases ?? [], openPullRequests ?? [], agentActivity ?? [], watchedTasks ?? [], tracker, lastEvent, Now, primaryRepository, activeIssueIds);

    // Once the plane watches more than one repository, "#115" stops being an answer:
    // both can have one, and a panel that names it without saying which is telling
    // the reader to go and find out.
    [Fact]
    public void IdentifiersAreQualifiedOnceMoreThanOneRepositoryIsWatched()
    {
        var escalated = Escalated("#115", posted: true);
        escalated.Repository = "JINPINYING/Symphony";

        var qualified = Build(escalated: [escalated], primaryRepository: "JINPINYING/CyberMed-AI-Receptionist");
        Assert.Contains(qualified.Items, item => item.Label.StartsWith("Symphony#115", StringComparison.Ordinal));

        // And a single-repository plane keeps reading exactly as it did.
        var plain = Build(escalated: [escalated], primaryRepository: null);
        Assert.Contains(plain.Items, item => item.Label.StartsWith("#115", StringComparison.Ordinal));
    }

    // Rows written before multi-repository tracking carry no repository, and they
    // all belong to the repository that was the only one at the time. Labelling
    // them from the primary keeps them from being the one ambiguous line left on
    // an otherwise unambiguous panel.
    [Fact]
    public void ARowFromBeforeMultiRepositoryTrackingIsLabelledFromThePrimary()
    {
        var escalated = Escalated("#115", posted: true);
        escalated.Repository = string.Empty;

        var result = Build(escalated: [escalated], primaryRepository: "JINPINYING/CyberMed-AI-Receptionist");

        Assert.Contains(
            result.Items,
            item => item.Label.StartsWith("CyberMed-AI-Receptionist#115", StringComparison.Ordinal));
    }

    [Fact]
    public void APullRequestNumberIsQualifiedToo()
    {
        var pr = new OpenPullRequest(
            122, "Change 122", "https://example.invalid/pull/122", "someone", false, "SUCCESS", "MERGEABLE",
            Now.AddHours(-6), "JINPINYING/Symphony");

        var result = Build(openPullRequests: [pr], primaryRepository: "JINPINYING/CyberMed-AI-Receptionist");

        Assert.Contains(result.Items, item => item.Label.Contains("Symphony PR #122", StringComparison.Ordinal));
    }

    private static WatchedTaskReport Task(string name, string health, bool scheduledAgain = true) =>
        new(name, "\\" + name, "Enabled", "Ready", Now.AddMinutes(-5), 0,
            scheduledAgain ? Now.AddMinutes(10) : null, 15, health, $"{name} is {health}.");

    private static AgentActivityReport Activity(string summary, TimeSpan ago) =>
        new("Claude", summary, null, null, Now - ago);

    private static OpenPullRequest Pr(int number, string? checks, bool draft = false, string? branch = null, string? headSha = null) =>
        new(number, $"Change {number}", $"https://example.invalid/pull/{number}", "someone", draft, checks, "MERGEABLE", Now.AddHours(-6), "", branch, headSha);

    private static RunEntity Escalated(
        string issue,
        bool posted,
        string? repository = null,
        string? lastMessage = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        IssueId = "issue-" + issue,
        IssueIdentifier = issue,
        Repository = repository ?? string.Empty,
        LastMessage = lastMessage,
        Status = RunStatusNames.NeedsCommandCenter,
        EscalationPostedAtUtc = posted ? Now.AddMinutes(-5) : null,
    };

    private static PhaseLedgerEntity Ledger(string issue, string stage, int pr = 1) => new()
    {
        IssueId = "issue-" + issue,
        IssueIdentifier = issue,
        Stage = stage,
        PrNumber = pr
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
        Assert.Equal("The engine is not answering", result.Items[0].Label);

        // The headline no longer promises persistence for everything that is `down`;
        // it says what stopped and counts only what is actually the owner's. The
        // "will not clear on their own" claim moved to the detail, where it is made
        // about the listed items rather than about the whole page.
        Assert.Contains("stopped", result.Headline);
        Assert.Contains("will not clear on their own", result.Detail);
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
        var result = Build(watchedTasks:
            [Task("ADCP Commander", WatchedTaskReport.HealthLate, scheduledAgain: false)]);

        Assert.Equal(OwnerAttentionSummary.LevelDown, result.Level);
        var item = Assert.Single(result.Items);
        Assert.Equal("ADCP Commander is not running as scheduled", item.Label);

        // Nobody decides a scheduled task. Someone runs it, and the panel says how.
        Assert.Equal(AttentionActors.Operator, item.Actor);
        Assert.Equal("schtasks /run /tn \"ADCP Commander\"", item.Action?.Command);
    }

    // "Cannot recover on its own" is about whether another run is booked, not about
    // the last exit code. A disabled task has nothing coming; so does one whose
    // schedule has no next run. Those are the two that need a person.
    [Theory]
    [InlineData(WatchedTaskReport.HealthDisabled, true)]
    [InlineData(WatchedTaskReport.HealthFailing, false)]
    public void ATaskThatCannotRecoverOnItsOwnReadsAsDown(string health, bool scheduledAgain)
    {
        var result = Build(watchedTasks: [Task("ADCP Event Watcher", health, scheduledAgain)]);

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

    // 2026-09-01: the page led with "2 things are waiting on you" for #115 and
    // #118 while its own event stream, further down the same page, reported both
    // as "resolved and no longer needs attention". The ledger had been closed and
    // the runs had not. Reporting a decision that is not needed costs more trust
    // than missing one, because the reader stops believing the number.
    [Theory]
    [InlineData(PhaseStages.Closed)]
    [InlineData(PhaseStages.Merged)]
    public void AnEscalationWhosePhaseHasSettledIsNotStillWaitingOnYou(string stage)
    {
        var result = Build(
            escalated: [Escalated("#115", posted: true)],
            phases: [Ledger("#115", stage)]);

        Assert.Equal(OwnerAttentionSummary.LevelClear, result.Level);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void AnEscalationWhosePhaseIsStillEscalatedDoesStillWait()
    {
        var result = Build(
            escalated: [Escalated("#115", posted: true)],
            phases: [Ledger("#115", PhaseStages.Escalated)]);

        Assert.Contains(result.Items, i => i.Label.Contains("#115") && i.Label.Contains("needs a decision"));
    }

    // The engine already knows why it stopped. "Escalated and posted to GitHub"
    // sent the reader off to find what was in hand the whole time.
    [Fact]
    public void TheReasonTheRunStoppedIsTheDetail()
    {
        var result = Build(escalated:
        [
            Escalated("#115", posted: true,
                lastMessage: "Phase orchestration: VERIFY failed for PR #122: CI rollup is FAILURE at head 214a4406.")
        ]);

        var item = Assert.Single(result.Items);
        Assert.Contains("CI rollup is FAILURE at head 214a4406", item.Detail);
        Assert.Contains("symphony:directive", item.Detail);
    }

    [Fact]
    public void AnEscalationWithNoRecordedReasonSaysSoRatherThanInventingOne()
    {
        var result = Build(escalated: [Escalated("#115", posted: true, lastMessage: null)]);

        Assert.Contains("did not record a reason", Assert.Single(result.Items).Detail);
    }

    // An item that names a decision and makes the reader hunt for the issue is
    // half an answer. The pull-request items had links; these did not.
    [Fact]
    public void AnEscalationLinksToItsIssue()
    {
        var result = Build(escalated:
            [Escalated("#115", posted: true, repository: "JINPINYING/CyberMed-AI-Receptionist")]);

        Assert.Equal(
            "https://github.com/JINPINYING/CyberMed-AI-Receptionist/issues/115",
            Assert.Single(result.Items).Url);
    }

    // A wrong link is worse than none: none is obviously absent, a wrong one is
    // discovered only after following it.
    [Fact]
    public void AnEscalationWithNoRepositoryHasNoLinkRatherThanAGuessedOne()
    {
        var result = Build(escalated: [Escalated("#115", posted: true, repository: null)]);

        Assert.Null(Assert.Single(result.Items).Url);
    }

    // EscalateAsync marks BOTH records - the ledger goes to escalated and the
    // newest run goes to needs_command_center - so every phase escalation raised
    // two items for one fact and the headline counted six things waiting when
    // four were. Inflating the only number the reader is asked to trust is worse
    // than the redundancy looks.
    [Fact]
    public void OnePhaseEscalationIsOneThingWaiting()
    {
        var result = Build(
            escalated: [Escalated("#126", posted: true, lastMessage: "Phase orchestration: merge gate refused.")],
            phases: [Ledger("#126", PhaseStages.Escalated, pr: 131)]);

        var item = Assert.Single(result.Items);
        Assert.Contains("needs a decision", item.Label);
        // The kept item is the one carrying the reason the phase recorded.
        Assert.Contains("merge gate refused", item.Detail);
    }

    // A ledger escalated with no run behind it must still be reported, or
    // deduplicating quietly drops a real alarm.
    [Fact]
    public void APhaseEscalationWithNoRunBehindItStillReports()
    {
        var result = Build(phases: [Ledger("#126", PhaseStages.Escalated, pr: 131)]);

        Assert.Contains("stopped at the merge gate", Assert.Single(result.Items).Label);
    }

    // The owner asked whether "waiting on you" really meant them. For PR #127 it
    // did not: the plane had opened it, then dropped it - the ledger still pointed
    // at the previous, closed pull request, so no review or merge would ever run.
    // "Decide this" and "the plane lost this" ask completely different things of
    // the reader, and the page called both the first.
    [Fact]
    public void AGreenPullRequestThePipelineIsNotTrackingIsAFaultNotADecision()
    {
        var result = Build(
            openPullRequests: [Pr(127, "SUCCESS", branch: "symphony/115")],
            phases: [Ledger("#115", PhaseStages.Closed, pr: 122)]);

        var item = Assert.Single(result.Items);
        Assert.Contains("fell out of the pipeline", item.Label);
        Assert.Contains("fault to repair, not a decision", item.Detail);
    }

    [Fact]
    public void AGreenPullRequestThePipelineHoldsIsStillYourDecision()
    {
        var result = Build(
            openPullRequests: [Pr(127, "SUCCESS", branch: "symphony/115")],
            phases: [Ledger("#115", PhaseStages.Ready, pr: 127)]);

        var item = Assert.Single(result.Items);
        Assert.Contains("is waiting on you", item.Label);
        Assert.Contains("Nothing will merge it without you", item.Detail);
    }

    // TRUE. "Waiting on you" was open + CI not failing, which is true of every pull
    // request in flight - so the owner's list grew during healthy activity, exactly
    // when they should be left alone. The stage is the pipeline's own statement of
    // who is holding it, and four of them mean "the plane moves this next".
    [Theory]
    [InlineData(PhaseStages.AwaitingVerify)]
    [InlineData(PhaseStages.AwaitingReview)]
    [InlineData(PhaseStages.Reviewing)]
    [InlineData(PhaseStages.WaitForRepair)]
    public void APullRequestThePipelineIsStillMovingIsNotYours(string stage)
    {
        var result = Build(
            openPullRequests: [Pr(147, "SUCCESS", branch: "symphony/146")],
            phases: [Ledger("#146", stage, pr: 147)]);

        Assert.DoesNotContain(result.Items, i => i.Label.Contains("waiting on you"));
    }

    // The dangerous one. "PR #147 was approved but not merged" was said of a pull
    // request that had never been reviewed at its head at all. A verdict recorded
    // against an earlier commit is not a verdict about the code that would merge,
    // and stating otherwise invites merging unreviewed work on the panel's word.
    [Fact]
    public void AVerdictAtAnEarlierHeadIsNotAnApprovalOfThisOne()
    {
        var ledger = Ledger("#146", PhaseStages.Ready, pr: 147);
        ledger.LastVerdict = ReviewVerdicts.Approved;
        ledger.LastVerdictHeadSha = "old00000";

        var result = Build(
            openPullRequests: [Pr(147, "SUCCESS", branch: "symphony/146", headSha: "new11111")],
            phases: [ledger]);

        Assert.DoesNotContain(result.Items, i => i.Label.Contains("waiting on you"));
    }

    [Fact]
    public void AVerdictAtThisExactHeadIsYours()
    {
        var ledger = Ledger("#146", PhaseStages.Ready, pr: 147);
        ledger.LastVerdict = ReviewVerdicts.Approved;
        ledger.LastVerdictHeadSha = "same2222";

        var result = Build(
            openPullRequests: [Pr(147, "SUCCESS", branch: "symphony/146", headSha: "same2222")],
            phases: [ledger]);

        var item = Assert.Single(result.Items);
        Assert.Contains("waiting on you", item.Label);
        Assert.Equal(AttentionActors.Owner, item.Actor);
        Assert.Equal("merge", item.Action?.Kind);
    }

    // An issue the plane is mid-run on is not waiting on anyone.
    [Fact]
    public void APullRequestWhoseIssueIsRunningIsNotWaitingOnYou()
    {
        var result = Build(
            openPullRequests: [Pr(147, "SUCCESS", branch: "symphony/146")],
            phases: [Ledger("#146", PhaseStages.Ready, pr: 147)],
            activeIssueIds: ["issue-#146"]);

        Assert.DoesNotContain(result.Items, i => i.Label.Contains("waiting on you"));
    }

    // NOT YOURS, NOT SAID TO BE YOURS. A pull request the plane dropped is a fault
    // to repair - its own detail text always said so while the panel filed it under
    // the owner's decisions anyway.
    [Fact]
    public void AFaultToRepairIsNotFiledAsTheOwnersDecision()
    {
        var result = Build(
            openPullRequests: [Pr(127, "SUCCESS", branch: "symphony/115")],
            phases: [Ledger("#115", PhaseStages.Closed, pr: 122)]);

        var item = Assert.Single(result.Items);
        Assert.Equal(AttentionActors.Operator, item.Actor);
        Assert.NotNull(item.Action?.Command);
    }

    // And when nothing on the list is the owner's, the headline must not claim
    // otherwise. This is the exact sentence the owner objected to.
    [Fact]
    public void AListWithNothingOfYoursDoesNotSayThingsAreWaitingOnYou()
    {
        var result = Build(
            openPullRequests: [Pr(127, "SUCCESS", branch: "symphony/115")],
            phases: [Ledger("#115", PhaseStages.Closed, pr: 122)]);

        Assert.Equal("Nothing needs you", result.Headline);
        Assert.DoesNotContain("waiting on you", result.Headline);
    }

    // ACTIONABLE. Every item the owner is shown carries something they can do.
    [Fact]
    public void EveryOwnerItemCarriesAnAction()
    {
        var ledger = Ledger("#146", PhaseStages.Ready, pr: 147);
        ledger.LastVerdict = ReviewVerdicts.Approved;
        ledger.LastVerdictHeadSha = "same2222";

        var result = Build(
            openPullRequests: [Pr(147, "SUCCESS", branch: "symphony/146", headSha: "same2222")],
            phases: [ledger],
            watchedTasks: [Task("ADCP Commander", WatchedTaskReport.HealthFailing, scheduledAgain: false)]);

        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, item =>
        {
            Assert.NotNull(item.Action);
            Assert.False(string.IsNullOrWhiteSpace(item.Action!.Label));
            Assert.True(
                item.Action.Url is not null || item.Action.Command is not null,
                $"'{item.Label}' offers no way to act on it.");
        });
    }

    // Red CI outranks both: the gate will not take it whoever it belongs to.
    [Fact]
    public void FailingChecksOutrankTheTrackingQuestion()
    {
        var result = Build(
            openPullRequests: [Pr(127, "FAILURE", branch: "symphony/115")],
            phases: [Ledger("#115", PhaseStages.Closed, pr: 122)]);

        Assert.Contains("has failing checks", Assert.Single(result.Items).Label);
    }

    // The observed failures were DNS blips that cleared within a tick or two and
    // cost nothing. Reporting each of them would train the reader to ignore red,
    // which is the same failure this page keeps trying not to commit.
    [Fact]
    public void ABriefLossOfTheTrackerIsNotWorthWakingAnyone()
    {
        var tracker = new TrackerReachabilitySnapshot(
            ConsecutiveFailures: 3,
            LastSuccessUtc: Now.AddMinutes(-2),
            UnreachableSinceUtc: Now.AddMinutes(-2),
            LastFailureReason: "No such host is known. (api.github.com:443)",
            LastFailureTransient: true);

        var result = Build(tracker: tracker);

        Assert.Equal(OwnerAttentionSummary.LevelClear, result.Level);
        Assert.Empty(result.Items);
    }

    // Sustained, though, means the plane is blind: nothing is found, nothing is
    // dispatched, and from the inside that is indistinguishable from a quiet
    // queue - so it has to be said out loud.
    [Fact]
    public void ATrackerThatStaysUnreachableIsABlindPlane()
    {
        var tracker = new TrackerReachabilitySnapshot(
            ConsecutiveFailures: 90,
            LastSuccessUtc: Now.AddMinutes(-25),
            UnreachableSinceUtc: Now.AddMinutes(-25),
            LastFailureReason: "No such host is known. (api.github.com:443)",
            LastFailureTransient: true);

        var result = Build(tracker: tracker);

        Assert.Equal(OwnerAttentionSummary.LevelDown, result.Level);
        var item = Assert.Single(result.Items);
        Assert.Equal("The issue tracker cannot be reached", item.Label);
        // The cause must travel with the alarm. Reporting only that a scan failed
        // is what sent the real answer to a 64 MB log file in the first place.
        Assert.Contains("api.github.com", item.Detail);
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
