using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symphony.Core.Abstractions;
using Symphony.Core.Configuration;
using Symphony.Core.Models;
using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Integration.Tests;

public sealed class OrchestrationTickServiceTests
{
    [Fact]
    public async Task RunTickAsync_ShouldFinalizeSuccessfulDispatchWithoutSchedulingContinuation()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.Succeeded, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Equal(RunPhaseNames.Implementation, run.Phase);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Equal(RunStatusNames.Succeeded, (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldDrainLegacyContinuationEntriesWithoutRedispatching()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", delayType: RetryDelayTypes.Continuation);

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Equal(RunStatusNames.Succeeded, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
    }

    [Fact]
    public async Task RunTickAsync_ShouldEscalateMissingRetryCandidateWithUnfinishedWork()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Open"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", sessionId: "session-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Equal(RunStatusNames.NeedsCommandCenter, (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "needs_command_center");
    }

    [Fact]
    public async Task RunTickAsync_ShouldPublishEscalationCommentWithinSameTick()
    {
        var tracker = new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
        {
            ["issue-1"] = "Open"
        });
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", sessionId: "session-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
        Assert.NotNull(run.EscalationPostedAtUtc);

        var posted = Assert.Single(tracker.PostedComments);
        Assert.Equal("issue-1", posted.IssueId);
        Assert.Contains(EscalationPublisher.MarkerFor(run.Id), posted.Body);
        Assert.Contains("needs command center", posted.Body);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "escalation_posted");
    }

    [Fact]
    public async Task RunTickAsync_ShouldNotDuplicateEscalationCommentOnRetick()
    {
        var tracker = new FakeTrackerClient([]);
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.NeedsCommandCenter);

        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(tracker.PostedComments);
        Assert.NotNull((await harness.DbContext.Runs.SingleAsync()).EscalationPostedAtUtc);
    }

    [Fact]
    public async Task RunTickAsync_ShouldKeepEscalationPendingWhenPostFails()
    {
        var tracker = new FakeTrackerClient([]) { ThrowOnPostComment = true };
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.NeedsCommandCenter);

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(tracker.PostedComments);
        Assert.Null((await harness.DbContext.Runs.SingleAsync()).EscalationPostedAtUtc);

        // The escalation is not lost: once the tracker recovers, the next tick posts it.
        tracker.ThrowOnPostComment = false;
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(tracker.PostedComments);
        Assert.NotNull((await harness.DbContext.Runs.SingleAsync()).EscalationPostedAtUtc);
    }

    [Fact]
    public async Task RunTickAsync_ShouldMarkPostedWithoutPostingWhenMarkerAlreadyPresent()
    {
        var tracker = new FakeTrackerClient([]) { MarkerAlreadyPresent = true };
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.NeedsCommandCenter);

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(tracker.PostedComments);
        Assert.NotNull((await harness.DbContext.Runs.SingleAsync()).EscalationPostedAtUtc);
    }

    [Fact]
    public async Task RunTickAsync_ShouldKeepEscalationPendingWhenIssueCannotBeResolved()
    {
        var tracker = new FakeTrackerClient([]) { ReturnNullCommentMarkerSnapshot = true };
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.NeedsCommandCenter);

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(tracker.PostedComments);
        Assert.Null((await harness.DbContext.Runs.SingleAsync()).EscalationPostedAtUtc);
    }

    [Fact]
    public async Task RunTickAsync_ShouldExecuteResumeDirectiveOnEscalatedIssue()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesById["issue-1"] = BuildIssue("issue-1", "#1", "Open", null);
        tracker.CommentsByIssueId["issue-1"] =
        [
            new NormalizedIssueComment(
                "directive-1",
                "symphony:directive\naction: resume\ninstructions: continue from the open PR",
                "owner-login",
                "OWNER",
                DateTimeOffset.UtcNow.AddMinutes(-1))
        ];
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.NeedsCommandCenter);

        await harness.Service.RunTickAsync(CancellationToken.None);

        var request = Assert.Single(harness.Coordinator.StartRequests);
        Assert.Equal("continue from the open PR", request.DirectiveInstructions);
        Assert.Equal(DirectiveActions.Resume, request.DirectiveAction);

        var runs = (await harness.DbContext.Runs.ToListAsync())
            .OrderBy(run => run.StartedAtUtc)
            .ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal(RunStatusNames.ResolvedByDirective, runs[0].Status);
        Assert.Equal(RunStatusNames.Succeeded, runs[1].Status);

        Assert.Contains(
            tracker.PostedComments,
            comment => comment.Body.Contains(DirectiveProcessor.AckMarkerFor("directive-1"), StringComparison.Ordinal));
        var ledger = Assert.Single(await harness.DbContext.DirectiveLog.ToListAsync());
        Assert.Equal("consumed_dispatched", ledger.Outcome);
    }

    // Symphony#50. A directive asked for `review`, the ack said `review`, and the
    // run that followed was `implementation` attempt 2 on an issue that already had
    // an open PR - the exact condition that had escalated it. The phase survived the
    // directive path intact; the retry threw it away, because a retry passes neither
    // a directive nor a phase dispatch and the phase fell through to implementation.
    // Nobody outside could tell "review ran and found nothing" from "review never
    // ran", which is why the ack was worse than the wasted run.
    [Fact]
    public async Task RunTickAsync_ShouldResumeTheDirectivePhaseWhenTheDispatchIsRetried()
    {
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-7", 7, "OPEN", null, "symphony/1", "main")]);
        var tracker = new FakeTrackerClient([issue]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.CommentsByIssueId["issue-1"] =
        [
            new NormalizedIssueComment(
                "directive-1",
                "symphony:directive\naction: resume\nphase: review\ninstructions: review the open PR",
                "owner-login",
                "OWNER",
                DateTimeOffset.UtcNow.AddMinutes(-1))
        ];
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Failure));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.NeedsCommandCenter);

        // Tick 1: the directive dispatches the review, and it fails into the retry queue.
        await harness.Service.RunTickAsync(CancellationToken.None);

        var dispatched = Assert.Single(harness.Coordinator.StartRequests);
        Assert.Equal(RunPhaseNames.Review, dispatched.DirectivePhase);

        var retryEntry = await harness.DbContext.RetryQueue.SingleAsync();
        retryEntry.DueAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        await harness.DbContext.SaveChangesAsync();

        // Tick 2: the retry re-dispatches the same run row.
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal(2, harness.Coordinator.StartRequests.Count);
        var resumed = harness.Coordinator.StartRequests[1];
        Assert.Equal(RunPhaseNames.Review, resumed.DirectivePhase);
        Assert.Equal(DirectiveActions.Resume, resumed.DirectiveAction);
        Assert.Equal("review the open PR", resumed.DirectiveInstructions);

        // The record agrees with what was dispatched: still a review, never
        // relabelled as a second implementation attempt.
        var reviewRun = Assert.Single(
            await harness.DbContext.Runs
                .Where(run => run.Status != RunStatusNames.ResolvedByDirective)
                .ToListAsync());
        Assert.Equal(RunPhaseNames.Review, reviewRun.Phase);
        Assert.Equal(DirectiveActions.Resume, reviewRun.DirectiveAction);
        Assert.Equal("review the open PR", reviewRun.DirectiveInstructions);
    }

    // The resume must not reach further than the run it is retrying: an ordinary
    // implementation retry still gets the ordinary prompt and no directive block.
    [Fact]
    public async Task RunTickAsync_ShouldRetryAnOrdinaryImplementationWithoutInventingDispatchContext()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Failure));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var retryEntry = await harness.DbContext.RetryQueue.SingleAsync();
        retryEntry.DueAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal(2, harness.Coordinator.StartRequests.Count);
        var retried = harness.Coordinator.StartRequests[1];
        Assert.Equal(1, retried.Attempt);
        Assert.Equal(RunPhaseNames.Implementation, retried.DirectivePhase);
        Assert.Null(retried.DirectiveAction);
        Assert.Null(retried.DirectiveInstructions);
        Assert.Null(retried.PromptOverride);
        Assert.Null(retried.RunnerOverride);
        Assert.Equal(RunPhaseNames.Implementation, (await harness.DbContext.Runs.SingleAsync()).Phase);
    }

    // Rows written before the dispatch context was durable carry a phase name and
    // nothing behind it. Resuming one would hand the worker the ordinary
    // implementation prompt while the run still reported `review` - the same lie the
    // other way round, and harder to spot. It stops and asks for a person instead.
    [Fact]
    public async Task RunTickAsync_ShouldEscalateARetryOfAPhaseItCannotReproduce()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1");
        var stranded = await harness.DbContext.Runs.SingleAsync();
        stranded.Phase = RunPhaseNames.Review;
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
        Assert.Equal(RunPhaseNames.Review, run.Phase);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "phase_retry_unresumable");
    }

    [Fact]
    public async Task RunTickAsync_ShouldConsumeDirectiveExactlyOnceAcrossTicks()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesById["issue-1"] = BuildIssue("issue-1", "#1", "Open", null);
        tracker.CommentsByIssueId["issue-1"] =
        [
            new NormalizedIssueComment(
                "directive-1",
                "symphony:directive\naction: resume",
                "owner-login",
                "OWNER",
                DateTimeOffset.UtcNow.AddMinutes(-1))
        ];
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.NeedsCommandCenter);

        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.Coordinator.StartRequests);
        Assert.Single(await harness.DbContext.DirectiveLog.ToListAsync());
        Assert.Single(
            tracker.PostedComments.Where(comment =>
                comment.Body.Contains(DirectiveProcessor.AckMarkerFor("directive-1"), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RunTickAsync_ShouldReplyAndConsumeMalformedDirectiveWithoutDispatching()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesById["issue-1"] = BuildIssue("issue-1", "#1", "Open", null);
        tracker.CommentsByIssueId["issue-1"] =
        [
            new NormalizedIssueComment(
                "directive-1",
                "symphony:directive\naction: banana",
                "owner-login",
                "OWNER",
                DateTimeOffset.UtcNow.AddMinutes(-1))
        ];
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.NeedsCommandCenter);

        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Equal(
            RunStatusNames.NeedsCommandCenter,
            (await harness.DbContext.Runs.SingleAsync()).Status);
        var ledger = Assert.Single(await harness.DbContext.DirectiveLog.ToListAsync());
        Assert.Equal("consumed_invalid", ledger.Outcome);
        Assert.Single(
            tracker.PostedComments.Where(comment =>
                comment.Body.Contains("could not be executed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RunTickAsync_ShouldCloseIssueOnCloseDirective()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.CommentsByIssueId["issue-1"] =
        [
            new NormalizedIssueComment(
                "directive-1",
                "symphony:directive\naction: close",
                "owner-login",
                "OWNER",
                DateTimeOffset.UtcNow.AddMinutes(-1))
        ];
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.NeedsCommandCenter);

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Contains("issue-1", tracker.ClosedIssueIds);
        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Equal(
            RunStatusNames.ResolvedByDirective,
            (await harness.DbContext.Runs.SingleAsync()).Status);
        var ledger = Assert.Single(await harness.DbContext.DirectiveLog.ToListAsync());
        Assert.Equal("consumed_closed", ledger.Outcome);
    }

    [Fact]
    public async Task RunTickAsync_ShouldIgnoreDirectiveFromUnauthorizedAuthor()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesById["issue-1"] = BuildIssue("issue-1", "#1", "Open", null);
        tracker.CommentsByIssueId["issue-1"] =
        [
            new NormalizedIssueComment(
                "directive-1",
                "symphony:directive\naction: close",
                "drive-by-account",
                "NONE",
                DateTimeOffset.UtcNow.AddMinutes(-1))
        ];
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.NeedsCommandCenter);

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(tracker.ClosedIssueIds);
        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Empty(await harness.DbContext.DirectiveLog.ToListAsync());
        Assert.Equal(
            RunStatusNames.NeedsCommandCenter,
            (await harness.DbContext.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldSeedLedgerVerifyAndDispatchCrossVendorReview()
    {
        var tracker = new FakeTrackerClient([]);
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");
        tracker.OpenPullRequestNumberByHeadBranch["symphony/1"] = 5;

        await harness.Service.RunTickAsync(CancellationToken.None); // seed + verify passes
        await harness.Service.RunTickAsync(CancellationToken.None); // review dispatch

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.Reviewing, ledger.Stage);
        Assert.Equal("aaa111", ledger.HeadSha);
        Assert.Equal("codex", ledger.ImplementerRunner);

        var request = Assert.Single(harness.Coordinator.StartRequests);
        Assert.Equal("claude", request.RunnerOverride);
        Assert.NotNull(request.PromptOverride);
        Assert.Contains(PhaseOrchestrator.ReviewVerdictMarker(5, "aaa111"), request.PromptOverride);
        Assert.Contains("VERDICT: APPROVED", request.PromptOverride);
    }

    [Fact]
    public async Task RunTickAsync_ShouldSeedLedgerFromSymphonyBranchWhenIssueHasNoLinkedPullRequest()
    {
        // Regression: the first live M4 run never entered the phases because the
        // issue had no GitHub PR linkage (no closing keyword) and the workflow
        // sets include_pull_requests: false. Symphony created the branch itself,
        // so an open PR on that head must be discovered from the workspace record.
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesById["issue-1"] = BuildIssue("issue-1", "#1", "Open", null, pullRequests: []);
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        tracker.OpenPullRequestNumberByHeadBranch["symphony/1"] = 5;
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(5, ledger.PrNumber);
        Assert.Equal(PhaseStages.AwaitingReview, ledger.Stage);
        Assert.Equal("aaa111", ledger.HeadSha);
    }

    [Fact]
    public async Task RunTickAsync_ShouldClearExecutionLabelsWhenTheMergeGateMerges()
    {
        // Regression: the merge gate merged the PR but nothing removed the
        // execution label, so the issue still matched the candidate query and was
        // re-dispatched on the next tick - burning a whole agent run on finished
        // work before reconciliation cancelled it. Observed live on #97 at
        // 2026-08-31T01:38Z, three minutes after its PR was merged.
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesById["issue-1"] = BuildIssue("issue-1", "#1", "Open", null, pullRequests: []);
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        tracker.PullRequestFilesByNumber[5] = ["docs/notes.md"];

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1, mergePolicyEnabled: true, trackerLabels: ["symphony-ready"]),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await SeedReadyLedgerAsync(harness, prNumber: 5, headSha: "aaa111");

        await harness.Service.RunTickAsync(CancellationToken.None);

        var merged = Assert.Single(tracker.MergedPullRequests);
        Assert.Equal(5, merged.Number);
        Assert.Equal("aaa111", merged.HeadSha);

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.Merged, ledger.Stage);

        var removed = Assert.Contains("issue-1", tracker.RemovedLabelsByIssue);
        Assert.Contains("symphony-ready", removed);
    }

    [Fact]
    public async Task RunTickAsync_ShouldNotClearExecutionLabelsWhenTheMergeIsRefused()
    {
        // The label is the only thing keeping the issue eligible for a retry, so
        // it must survive any path that does not actually merge.
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesById["issue-1"] = BuildIssue("issue-1", "#1", "Open", null, pullRequests: []);
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        tracker.PullRequestFilesByNumber[5] = ["docs/notes.md"];
        tracker.MergeRefusal = "head moved";

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1, mergePolicyEnabled: true, trackerLabels: ["symphony-ready"]),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await SeedReadyLedgerAsync(harness, prNumber: 5, headSha: "aaa111");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(tracker.MergedPullRequests);
        Assert.Empty(tracker.RemovedLabelsByIssue);
    }

    // The multi-repository tracker. Until this, WORKFLOW.md had a single owner/repo
    // and the plane could only ever be pointed at one backlog - which is why every
    // control-plane repair had to be done by hand.
    [Fact]
    public async Task RunTickAsync_ShouldFindWorkInEveryTrackedRepository()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesByRepository["JINPINYING/Product"] = [BuildIssue("issue-p", "#1", "Open", null, repository: "JINPINYING/Product")];
        tracker.IssuesByRepository["JINPINYING/Symphony"] = [BuildIssue("issue-s", "#1", "Open", null, repository: "JINPINYING/Symphony")];

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(
                maxConcurrentAgents: 1,
                repositories: [("JINPINYING", "Product"), ("JINPINYING", "Symphony")]),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.Service.RunTickAsync(CancellationToken.None);

        // Both repositories were asked, and both issues are known - note they share
        // the identifier "#1", which is exactly the collision a single owner/repo
        // never had to handle.
        Assert.Equal(
            ["JINPINYING/Product", "JINPINYING/Symphony"],
            tracker.CandidateFetchRepositories.Distinct().OrderBy(name => name).ToArray());
        Assert.Equal(2, await harness.DbContext.IssueCache.CountAsync());

        // One slot, so exactly one ran - and it recorded which repository it came
        // from, because "#1" alone cannot say.
        var run = Assert.Single(await harness.DbContext.Runs.ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(run.Repository));
        Assert.Equal(run.Repository, Assert.Single(harness.Coordinator.StartRequests).Issue.Repository);
    }

    [Fact]
    public async Task RunTickAsync_ShouldKeepWorkingWhenOneRepositoryIsUnreachable()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesByRepository["JINPINYING/Product"] = [BuildIssue("issue-p", "#1", "Open", null, repository: "JINPINYING/Product")];
        tracker.RepositoriesThatFail.Add("JINPINYING/Symphony");

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(
                maxConcurrentAgents: 1,
                repositories: [("JINPINYING", "Product"), ("JINPINYING", "Symphony")]),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.Service.RunTickAsync(CancellationToken.None);

        // An outage on the plane's own backlog must not stop the product queue.
        Assert.Equal("issue-p", Assert.Single(harness.Coordinator.StartRequests).Issue.Id);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "candidate_scan_failed" && entry.Message.Contains("Symphony"));
    }

    [Fact]
    public async Task RunTickAsync_ShouldAskTheRightRepositoryAboutAPullRequestNumber()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.PullRequestStatusByNumber[122] = new PullRequestStatus(122, "MERGED", false, "aaa111", "SUCCESS", null);

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(
                maxConcurrentAgents: 1,
                repositories: [("JINPINYING", "Product"), ("JINPINYING", "Symphony")]),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        harness.DbContext.PhaseLedger.Add(new PhaseLedgerEntity
        {
            IssueId = "issue-s",
            IssueIdentifier = "#1",
            Repository = "JINPINYING/Symphony",
            Stage = PhaseStages.Escalated,
            PrNumber = 122,
            HeadSha = "aaa111",
            ImplementerRunner = "claude",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        // Both repositories can have a PR #122. Asking the wrong one would read
        // another repository's pull request and act on it.
        Assert.Contains(("JINPINYING/Symphony", 122), tracker.PullRequestStatusRequests);
        Assert.DoesNotContain(("JINPINYING/Product", 122), tracker.PullRequestStatusRequests);
    }

    // ADCP#22. An escalated ledger is deliberately parked so the phase machine does
    // not resume it - but "parked" was implemented as "never looked at again", so
    // #111 was still listed on the owner's panel as stopped at the merge gate long
    // after PR #112 had been merged and the issue closed. A resolved alarm that
    // never clears teaches the reader the panel is not worth reading.
    [Fact]
    public async Task RunTickAsync_ShouldClearAMergeGateEscalationOnceItsPullRequestIsResolved()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.PullRequestStatusByNumber[112] = new PullRequestStatus(112, "MERGED", false, "dbbbae5c", "SUCCESS", null);
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await SeedEscalatedLedgerAsync(harness, prNumber: 112);

        await harness.Service.RunTickAsync(CancellationToken.None);

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.Closed, ledger.Stage);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "phase_escalation_cleared");
    }

    // The half ADCP#22 missed. Clearing the LEDGER was not enough, because the
    // owner's attention panel is built from `runs` - so on 2026-09-01 the page led
    // with "2 things are waiting on you" for #115 and #118 while its own event
    // stream, further down the same page, reported both as resolved. The two
    // records have to move together or the page contradicts itself on the one
    // line that matters most.
    [Fact]
    public async Task RunTickAsync_ShouldResolveTheStrandedRunWhenItClearsTheEscalation()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.PullRequestStatusByNumber[112] = new PullRequestStatus(112, "MERGED", false, "dbbbae5c", "SUCCESS", null);
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await SeedEscalatedLedgerAsync(harness, prNumber: 112);
        harness.DbContext.Runs.Add(new RunEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            IssueId = "issue-1",
            IssueIdentifier = "#111",
            Status = RunStatusNames.NeedsCommandCenter,
            EscalationPostedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            StartedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = Assert.Single(await harness.DbContext.Runs.ToListAsync());
        Assert.Equal(RunStatusNames.ResolvedByPhaseClear, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
    }

    // Resolving the run at the moment the ledger closes fixes every future case
    // and no past one - that loop only visits ledgers still at stage escalated.
    // #115 and #118 had already been cleared to closed before that code existed,
    // so their runs would have stayed needs_command_center forever, invisible on
    // the panel (which suppresses settled issues) but still counted by the
    // commander's sweep, which reads the runs.
    [Fact]
    public async Task RunTickAsync_ShouldRepairARunLeftStrandedAgainstAnAlreadySettledLedger()
    {
        var tracker = new FakeTrackerClient([]);
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        // Already settled - nothing here is at stage Escalated, so the clearing
        // loop never looks at it.
        harness.DbContext.PhaseLedger.Add(new PhaseLedgerEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = "#115",
            Stage = PhaseStages.Closed,
            PrNumber = 122,
            HeadSha = "214a4406",
            ImplementerRunner = "claude",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        harness.DbContext.Runs.Add(new RunEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            IssueId = "issue-1",
            IssueIdentifier = "#115",
            Status = RunStatusNames.NeedsCommandCenter,
            EscalationPostedAtUtc = DateTimeOffset.UtcNow.AddHours(-5),
            StartedAtUtc = DateTimeOffset.UtcNow.AddHours(-6),
        });
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = Assert.Single(await harness.DbContext.Runs.ToListAsync());
        Assert.Equal(RunStatusNames.ResolvedByPhaseClear, run.Status);
    }

    // The sweep must not touch an escalation that is genuinely still open, or it
    // becomes a machine for silently clearing real alarms.
    [Fact]
    public async Task RunTickAsync_ShouldLeaveAStrandedRunAloneWhileItsPhaseIsStillEscalated()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.PullRequestStatusByNumber[112] = new PullRequestStatus(112, "OPEN", false, "dbbbae5c", "SUCCESS", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await SeedEscalatedLedgerAsync(harness, prNumber: 112);
        harness.DbContext.Runs.Add(new RunEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            IssueId = "issue-1",
            IssueIdentifier = "#111",
            Status = RunStatusNames.NeedsCommandCenter,
            EscalationPostedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            StartedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
        });
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = Assert.Single(await harness.DbContext.Runs.ToListAsync());
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldLeaveAMergeGateEscalationUpWhileItsPullRequestIsStillOpen()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.PullRequestStatusByNumber[112] = new PullRequestStatus(112, "OPEN", false, "dbbbae5c", "SUCCESS", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await SeedEscalatedLedgerAsync(harness, prNumber: 112);

        await harness.Service.RunTickAsync(CancellationToken.None);

        // Still waiting on a person, and the phase machine must not have resumed it.
        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.Escalated, ledger.Stage);
        Assert.Empty(harness.Coordinator.StartRequests);
    }

    [Fact]
    public async Task RunTickAsync_ShouldLeaveAMergeGateEscalationUpWhenItCannotBeVerified()
    {
        // No status for PR 112 at all: clearing an alarm we could not verify is
        // worse than leaving one up a little longer.
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await SeedEscalatedLedgerAsync(harness, prNumber: 112);

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal(
            PhaseStages.Escalated,
            (await harness.DbContext.PhaseLedger.SingleAsync()).Stage);
    }

    private static async Task SeedEscalatedLedgerAsync(TestHarness harness, int prNumber)
    {
        harness.DbContext.PhaseLedger.Add(new PhaseLedgerEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = "#111",
            Stage = PhaseStages.Escalated,
            PrNumber = prNumber,
            HeadSha = "dbbbae5c",
            ImplementerRunner = "codex",
            RepairCount = 0,
            LastVerdict = ReviewVerdicts.Approved,
            LastVerdictHeadSha = "dbbbae5c",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        await harness.DbContext.SaveChangesAsync();
    }

    private static async Task SeedReadyLedgerAsync(TestHarness harness, int prNumber, string headSha)
    {
        harness.DbContext.PhaseLedger.Add(new PhaseLedgerEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = "#1",
            Stage = PhaseStages.Ready,
            PrNumber = prNumber,
            HeadSha = headSha,
            ImplementerRunner = "claude",
            RepairCount = 0,
            LastVerdict = ReviewVerdicts.Approved,
            LastVerdictHeadSha = headSha,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        await harness.DbContext.SaveChangesAsync();
    }

    // An implementation that reports success and produces no pull request used to
    // vanish here: no ledger, no event, no escalation, and on the next scan the
    // redispatch guard refused to try again. The run said "succeeded" and the work
    // did not exist. This asserts the postcondition instead - no pull request and
    // no statement that none was needed is a contract violation, and it is said
    // out loud.
    [Fact]
    public async Task RunTickAsync_ShouldEscalateWhenImplementationSucceedsWithoutAPullRequest()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesById["issue-1"] = BuildIssue("issue-1", "#1", "Open", null, pullRequests: []);
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        // Still no ledger - there is no pull request to track. The difference is
        // that the run no longer reads as successful work.
        Assert.Empty(await harness.DbContext.PhaseLedger.ToListAsync());

        var run = Assert.Single(await harness.DbContext.Runs.ToListAsync());
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
        Assert.Contains("no pull request", run.LastMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var escalation = Assert.Single(
            (await harness.DbContext.EventLog.ToListAsync())
                .Where(entry => entry.EventName == "needs_command_center"));
        Assert.Contains("#1", escalation.IssueIdentifier ?? string.Empty, StringComparison.Ordinal);
    }

    // The escape hatch. An implementation may legitimately conclude that nothing
    // needed changing - but it has to say so, in durable tracker truth, the same
    // way a review has to post its verdict. Then it is a reported outcome rather
    // than an absence indistinguishable from failure.
    [Fact]
    public async Task RunTickAsync_ShouldAcceptAnExplicitNoChangeStatementInsteadOfAPullRequest()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesById["issue-1"] = BuildIssue("issue-1", "#1", "Open", null, pullRequests: []);
        tracker.CommentsByIssueId["issue-1"] =
        [
            new NormalizedIssueComment(
                "impl-1",
                PhaseOrchestrator.NoChangeNeededMarker("issue-1") + "\nNothing to change: the guard already exists.",
                "claude", "OWNER", DateTimeOffset.UtcNow)
        ];
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(await harness.DbContext.PhaseLedger.ToListAsync());

        var run = Assert.Single(await harness.DbContext.Runs.ToListAsync());
        Assert.Equal(RunStatusNames.Succeeded, run.Status);
        Assert.DoesNotContain(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "needs_command_center");
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "phase_implementation_no_change");
    }

    // Escalating must happen once. The scan runs every tick over every succeeded
    // implementation, so an escalation that left the run succeeded would re-fire
    // for as long as the issue stayed open.
    [Fact]
    public async Task RunTickAsync_ShouldEscalateAMissingPullRequestOnlyOnce()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesById["issue-1"] = BuildIssue("issue-1", "#1", "Open", null, pullRequests: []);
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");

        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(
            (await harness.DbContext.EventLog.ToListAsync())
                .Where(entry => entry.EventName == "needs_command_center"));
    }

    // A pull request closed and then reopened was orphaned forever. The ledger
    // recorded closed, and re-seeding skipped it because the pull request number
    // matched - read as "nothing new to enter", when a settled ledger naming an
    // OPEN pull request is a reopen. #135 lived this: closed 11:57, reopened
    // 12:11, and nothing would have looked at it again.
    [Fact]
    public async Task RunTickAsync_ShouldPickUpAPullRequestThatWasClosedAndReopened()
    {
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        var tracker = new FakeTrackerClient([issue]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        tracker.OpenPullRequestNumberByHeadBranch["symphony/1"] = 5;
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");

        // The ledger settled while the pull request was closed; it is open again now.
        harness.DbContext.PhaseLedger.Add(new PhaseLedgerEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = "#1",
            Stage = PhaseStages.Closed,
            PrNumber = 5,
            HeadSha = "old-head",
            LastVerdict = ReviewVerdicts.ChangesRequired,
            RejectedHeadSha = "old-head",
            RepairCount = 1,
            ImplementerRunner = "claude",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
        });
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        var ledger = await harness.DbContext.PhaseLedger.SingleAsync();
        Assert.Equal(5, ledger.PrNumber);
        Assert.NotEqual(PhaseStages.Closed, ledger.Stage);
        // The old verdict belonged to the closed life of this pull request and must
        // not fence the reopened one.
        Assert.Null(ledger.LastVerdict);
        Assert.Null(ledger.RejectedHeadSha);
        Assert.Equal(0, ledger.RepairCount);
    }

    // The same hole as the reviewing stage, in its sibling: a repair that ended
    // without failing matched none of the escalation statuses and fell through to
    // "keep waiting", so the issue parked permanently behind an unmoved fence.
    [Fact]
    public async Task RunTickAsync_ShouldReturnToReviewWhenTheRepairEndedWithoutMovingTheHead()
    {
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        var tracker = new FakeTrackerClient([issue]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        harness.DbContext.PhaseLedger.Add(new PhaseLedgerEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = "#1",
            Stage = PhaseStages.WaitForRepair,
            PrNumber = 5,
            HeadSha = "aaa111",
            RejectedHeadSha = "aaa111",
            RepairCount = 1,
            ImplementerRunner = "claude",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.CanceledByReconciliation);
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        var ledger = await harness.DbContext.PhaseLedger.SingleAsync();
        Assert.Equal(PhaseStages.AwaitingReview, ledger.Stage);
    }

    // The backstop. Enumerating statuses fixes the holes already met and nothing
    // about the next one, and the stages legitimately wait on several unbounded
    // things - a draft pull request, a CI check that never reports, a claim that
    // keeps being refused. This notices the absence of progress instead.
    [Fact]
    public async Task RunTickAsync_ShouldEscalateAPhaseThatHasNotMovedForHours()
    {
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        var tracker = new FakeTrackerClient([issue]);
        tracker.IssuesById["issue-1"] = issue;
        // Draft with CI still pending: legitimate to wait on, unbounded before now.
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", true, "aaa111", "PENDING", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        harness.DbContext.PhaseLedger.Add(new PhaseLedgerEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = "#1",
            Stage = PhaseStages.AwaitingVerify,
            PrNumber = 5,
            HeadSha = "aaa111",
            ImplementerRunner = "claude",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-5),
            UpdatedAtUtc = DateTimeOffset.UtcNow - PhaseOrchestrator.StuckStageTimeout - TimeSpan.FromMinutes(30),
        });
        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal(PhaseStages.Escalated, (await harness.DbContext.PhaseLedger.SingleAsync()).Stage);
    }

    // ...and must not fire on a phase that is merely young, or every slow CI run
    // becomes an escalation and the owner learns to ignore them.
    [Fact]
    public async Task RunTickAsync_ShouldLeaveARecentlyUpdatedPhaseAlone()
    {
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        var tracker = new FakeTrackerClient([issue]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "PENDING", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        harness.DbContext.PhaseLedger.Add(new PhaseLedgerEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = "#1",
            Stage = PhaseStages.AwaitingVerify,
            PrNumber = 5,
            HeadSha = "aaa111",
            ImplementerRunner = "claude",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
        });
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal(PhaseStages.AwaitingVerify, (await harness.DbContext.PhaseLedger.SingleAsync()).Stage);
    }

    // The tick advances phases and reconciles runs, all of it local and cheap, so
    // it has to stay fast. The candidate scan asks GitHub and is the expensive
    // part - three GraphQL queries a tick once multi-repository tracking landed,
    // which exhausted the hourly budget and blinded the plane. Slowing the whole
    // tick fixed the spend and slowed every phase transition with it. Separate
    // clocks: the tick keeps running, the scan does not repeat.
    [Fact]
    public async Task RunTickAsync_ShouldNotRescanGitHubOnEveryTick()
    {
        var tracker = new FakeTrackerClient([]);
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.Service.RunTickAsync(CancellationToken.None);
        var afterFirst = tracker.CandidateFetchRepositories.Count;

        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.True(afterFirst > 0, "the first tick should scan");
        Assert.Equal(afterFirst, tracker.CandidateFetchRepositories.Count);
    }

    // An outage must retry on the next tick rather than wait out the slow clock -
    // recovering as soon as it can is the whole point of a fast tick.
    [Fact]
    public async Task RunTickAsync_ShouldRetryTheScanImmediatelyAfterAnOutage()
    {
        var tracker = new FakeTrackerClient([], throwOnFetchCandidates: true);
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.Service.RunTickAsync(CancellationToken.None);
        var afterFirst = tracker.CandidateFetchRepositories.Count;

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.True(tracker.CandidateFetchRepositories.Count > afterFirst,
            "a failed scan should be retried on the next tick, not held off on the slow clock");
    }

    // The reviewing stage waited forever on any terminal status it did not name.
    // #128 sat at `reviewing` with a canceled_by_reconciliation review run - the
    // pipeline claimed Codex was reviewing while the staff panel correctly showed
    // it idle, and the owner spotted the contradiction. A cancellation is the
    // engine's doing, not the reviewer's, so the recovery is to ask again rather
    // than escalate a reviewer that never got its turn.
    [Fact]
    public async Task RunTickAsync_ShouldRedispatchAReviewThatWasCancelledBeforeItCouldReport()
    {
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        var tracker = new FakeTrackerClient([issue]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        tracker.OpenPullRequestNumberByHeadBranch["symphony/1"] = 5;
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");

        // Seed the ledger, pass verify, dispatch the review.
        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Equal(PhaseStages.Reviewing,
            (await harness.DbContext.PhaseLedger.SingleAsync()).Stage);

        // The review run is cancelled out from under the phase, as restart
        // reconciliation does, and never posts a verdict.
        foreach (var run in await harness.DbContext.Runs.Where(r => r.Phase == RunPhaseNames.Review).ToListAsync())
        {
            run.Status = RunStatusNames.CanceledByReconciliation;
        }
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        // Recovered rather than parked: back to awaiting_review so the next tick
        // asks the reviewer again.
        var ledger = await harness.DbContext.PhaseLedger.SingleAsync();
        Assert.Equal(PhaseStages.AwaitingReview, ledger.Stage);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "phase_review_redispatch");
    }

    [Fact]
    public async Task RunTickAsync_ShouldNotDispatchIssueOwnedByThePhaseOrchestrator()
    {
        // Regression: on the first live M4 review the ordinary candidate loop
        // claimed the same still-labelled issue in the same tick and overwrote the
        // review run's phase and runner, so the cross-vendor review never ran.
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        var tracker = new FakeTrackerClient([issue]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        tracker.OpenPullRequestNumberByHeadBranch["symphony/1"] = 5;
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");

        // Tick 1 seeds the ledger and passes verify; tick 2 dispatches the review.
        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.Reviewing, ledger.Stage);

        // The issue is still an eligible candidate, but the phase machine owns it.
        var reviewRequest = Assert.Single(harness.Coordinator.StartRequests);
        Assert.Equal("claude", reviewRequest.RunnerOverride);

        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);

        // Still exactly one dispatch: no implementation hijacked the review.
        Assert.Single(harness.Coordinator.StartRequests);
        Assert.DoesNotContain(
            await harness.DbContext.Runs.ToListAsync(),
            run => run.Phase == RunPhaseNames.Implementation && run.Status == RunStatusNames.Running);
    }

    [Fact]
    public async Task RunTickAsync_ShouldRedispatchReviewWhenItsRunDisappeared()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesById["issue-1"] = BuildIssue("issue-1", "#1", "Open", null, pullRequests: []);
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        tracker.OpenPullRequestNumberByHeadBranch["symphony/1"] = 5;
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");

        await harness.Service.RunTickAsync(CancellationToken.None); // seed + verify
        await harness.Service.RunTickAsync(CancellationToken.None); // review dispatched

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.Reviewing, ledger.Stage);

        // Simulate the run row being taken over: no review-phase run remains.
        foreach (var run in await harness.DbContext.Runs.Where(r => r.Phase == RunPhaseNames.Review).ToListAsync())
        {
            run.Phase = RunPhaseNames.Implementation;
        }
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.AwaitingReview, ledger.Stage);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "phase_review_redispatch");
    }

    [Fact]
    public async Task RunTickAsync_ShouldMarkReadyOnApprovedVerdict()
    {
        var tracker = new FakeTrackerClient([]);
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");
        tracker.OpenPullRequestNumberByHeadBranch["symphony/1"] = 5;

        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);

        tracker.CommentsByIssueId.TryAdd("issue-1", []);
        tracker.CommentsByIssueId["issue-1"].Add(new NormalizedIssueComment(
            "review-1",
            PhaseOrchestrator.ReviewVerdictMarker(5, "aaa111") + "\nLooks correct and bounded.\nVERDICT: APPROVED",
            "reviewer", "OWNER", DateTimeOffset.UtcNow));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.Ready, ledger.Stage);
        Assert.Equal("APPROVED", ledger.LastVerdict);
        Assert.Contains(
            tracker.PostedComments,
            comment => comment.Body.Contains("READY_FOR_MERGE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTickAsync_ShouldEscalateWhenRepairNeverMovesHead()
    {
        var tracker = new FakeTrackerClient([]);
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");
        tracker.OpenPullRequestNumberByHeadBranch["symphony/1"] = 5;

        await harness.Service.RunTickAsync(CancellationToken.None); // seed + verify
        await harness.Service.RunTickAsync(CancellationToken.None); // review dispatch

        tracker.CommentsByIssueId.TryAdd("issue-1", []);
        tracker.CommentsByIssueId["issue-1"].Add(new NormalizedIssueComment(
            "review-1",
            PhaseOrchestrator.ReviewVerdictMarker(5, "aaa111") + "\nFinding: null check missing.\nVERDICT: CHANGES_REQUIRED",
            "reviewer", "OWNER", DateTimeOffset.UtcNow));

        await harness.Service.RunTickAsync(CancellationToken.None); // repair dispatch

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.WaitForRepair, ledger.Stage);
        Assert.Equal(1, ledger.RepairCount);
        Assert.Equal("aaa111", ledger.RejectedHeadSha);
        Assert.Equal(2, harness.Coordinator.StartRequests.Count);
        var repairRequest = harness.Coordinator.StartRequests[1];
        Assert.Equal("codex", repairRequest.RunnerOverride);
        Assert.Contains("SINGLE BOUNDED REPAIR", repairRequest.PromptOverride);

        // Repair run succeeded (fake) but the PR head never moved: the fence
        // refuses to re-review unchanged rejected code and escalates.
        await harness.Service.RunTickAsync(CancellationToken.None);
        ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.Escalated, ledger.Stage);
    }

    // The fence compared the live head against the rejected one with
    // string.Equals, and string.Equals(head, null) is false - so a ledger with no
    // rejected head recorded read as "the head moved" and waved the repair
    // through onto unchanged rejected code. A fence that cannot be evaluated is
    // not a fence that passes.
    [Fact]
    public async Task RunTickAsync_ShouldNotPassTheRepairFenceWhenNoRejectedHeadWasRecorded()
    {
        var tracker = new FakeTrackerClient([]);
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");

        harness.DbContext.PhaseLedger.Add(new PhaseLedgerEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = "#1",
            Stage = PhaseStages.WaitForRepair,
            PrNumber = 5,
            HeadSha = "aaa111",
            ImplementerRunner = "claude",
            RepairCount = 1,
            RejectedHeadSha = null,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.NotEqual(PhaseStages.AwaitingVerify, ledger.Stage);
        Assert.Equal(PhaseStages.Escalated, ledger.Stage);
    }

    // #28: the plane escalated a pull request for not moving past a commit it had
    // already moved past. One read of the head decided it, and a read taken just
    // after a push can lag. Escalating is not recoverable on its own, so the last
    // read before saying so is taken fresh - if it disagrees, the head moved.
    [Fact]
    public async Task RunTickAsync_ShouldConfirmTheHeadBeforeEscalatingARepairThatDidMoveIt()
    {
        var tracker = new FakeTrackerClient([]);
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");

        harness.DbContext.PhaseLedger.Add(new PhaseLedgerEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = "#1",
            Stage = PhaseStages.WaitForRepair,
            PrNumber = 5,
            HeadSha = "aaa111",
            ImplementerRunner = "claude",
            RepairCount = 1,
            RejectedHeadSha = "aaa111",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await harness.DbContext.SaveChangesAsync();

        // The first read of the tick still shows the rejected head; by the time the
        // fence is about to escalate, the push has landed. That is the lag #28 saw.
        tracker.PullRequestStatusOverridesAfterFirstRead[5] =
            new PullRequestStatus(5, "OPEN", false, "bbb222", "SUCCESS", "MERGEABLE");

        await harness.Service.RunTickAsync(CancellationToken.None);

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.AwaitingVerify, ledger.Stage);
        Assert.DoesNotContain(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "needs_command_center");
    }

    [Fact]
    public async Task RunTickAsync_ShouldAdvanceToFinalReviewWhenRepairMovesHeadAndEscalateOnSecondRejection()
    {
        var tracker = new FakeTrackerClient([]);
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");
        tracker.OpenPullRequestNumberByHeadBranch["symphony/1"] = 5;

        await harness.Service.RunTickAsync(CancellationToken.None); // seed + verify
        await harness.Service.RunTickAsync(CancellationToken.None); // review dispatch

        tracker.CommentsByIssueId.TryAdd("issue-1", []);
        tracker.CommentsByIssueId["issue-1"].Add(new NormalizedIssueComment(
            "review-1",
            PhaseOrchestrator.ReviewVerdictMarker(5, "aaa111") + "\nVERDICT: CHANGES_REQUIRED",
            "reviewer", "OWNER", DateTimeOffset.UtcNow));

        await harness.Service.RunTickAsync(CancellationToken.None); // repair dispatch

        // The repair moves the head before the next tick.
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "bbb222", "SUCCESS", "MERGEABLE");

        await harness.Service.RunTickAsync(CancellationToken.None); // wait_for_repair -> awaiting_verify
        await harness.Service.RunTickAsync(CancellationToken.None); // verify passes at new head
        await harness.Service.RunTickAsync(CancellationToken.None); // final review dispatch

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.Reviewing, ledger.Stage);
        Assert.Equal("bbb222", ledger.HeadSha);
        Assert.Equal(3, harness.Coordinator.StartRequests.Count);
        var finalReviewRequest = harness.Coordinator.StartRequests[2];
        Assert.Equal("claude", finalReviewRequest.RunnerOverride);
        Assert.Contains("FINAL review", finalReviewRequest.PromptOverride);
        Assert.Contains(PhaseOrchestrator.ReviewVerdictMarker(5, "bbb222"), finalReviewRequest.PromptOverride);

        // A second CHANGES_REQUIRED at the new head escalates: one repair only.
        tracker.CommentsByIssueId["issue-1"].Add(new NormalizedIssueComment(
            "review-2",
            PhaseOrchestrator.ReviewVerdictMarker(5, "bbb222") + "\nVERDICT: CHANGES_REQUIRED",
            "reviewer", "OWNER", DateTimeOffset.UtcNow));

        await harness.Service.RunTickAsync(CancellationToken.None);
        ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.Escalated, ledger.Stage);
        Assert.Equal(3, harness.Coordinator.StartRequests.Count); // no second repair
    }

    [Fact]
    public async Task RunTickAsync_ShouldEscalateWhenVerifyFindsFailingChecks()
    {
        var tracker = new FakeTrackerClient([]);
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "FAILURE", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");
        tracker.OpenPullRequestNumberByHeadBranch["symphony/1"] = 5;

        await harness.Service.RunTickAsync(CancellationToken.None);

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.Escalated, ledger.Stage);
        Assert.Equal(
            RunStatusNames.NeedsCommandCenter,
            (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Empty(harness.Coordinator.StartRequests);
    }

    [Fact]
    public async Task RunTickAsync_ShouldDeferDispatchingDirectiveWhenNoAgentSlotIsFree()
    {
        var tracker = new FakeTrackerClient([]);
        tracker.IssuesById["issue-1"] = BuildIssue("issue-1", "#1", "Open", null);
        tracker.CommentsByIssueId["issue-1"] =
        [
            new NormalizedIssueComment(
                "directive-1",
                "symphony:directive\naction: resume",
                "owner-login",
                "OWNER",
                DateTimeOffset.UtcNow.AddMinutes(-1))
        ];
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunningRunAsync("issue-2", "#2", "Open", "instance-1", lastEventAtUtc: DateTimeOffset.UtcNow);
        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.NeedsCommandCenter);

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(await harness.DbContext.DirectiveLog.ToListAsync());
        Assert.DoesNotContain(
            tracker.PostedComments,
            comment => comment.Body.Contains("symphony:directive-ack", StringComparison.Ordinal));
        Assert.Equal(
            RunStatusNames.NeedsCommandCenter,
            (await harness.DbContext.Runs.SingleAsync(run => run.IssueId == "issue-1")).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldReleaseMissingRetryCandidateWhenIssueIsTerminal()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", sessionId: "session-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal(RunStatusNames.ReleasedIneligible, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
    }

    [Fact]
    public async Task RunTickAsync_ShouldKeepRetryReservationWhenCandidateReloadFails()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], throwOnFetchStatesByIds: true),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", sessionId: "session-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal(RunStatusNames.Retrying, (await harness.DbContext.Runs.SingleAsync()).Status);
        var retryEntry = await harness.DbContext.RetryQueue.SingleAsync();
        Assert.True(retryEntry.DueAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RunTickAsync_ShouldEscalateAbandonedReleasedRunForOpenUnlabeledIssue()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-88"] = "Open"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync(
            "issue-88",
            "#88",
            "Open",
            "instance-1",
            status: RunStatusNames.ReleasedIneligible,
            sessionId: "session-88",
            completedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "needs_command_center");
    }

    [Fact]
    public async Task RunTickAsync_ShouldBlockImplementationRedispatchWhileSucceededRunHasOpenPullRequest()
    {
        var issueWithOpenPr = BuildIssue(
            "issue-1",
            "#1",
            "Open",
            null,
            pullRequests: [new PullRequestRef("pr-1", 89, "OPEN", null, null, null)]);

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([issueWithOpenPr]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            status: RunStatusNames.Succeeded,
            sessionId: "session-1",
            completedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "implementation_redispatch_blocked");
    }

    [Fact]
    public async Task RunTickAsync_ShouldEscalateWhenRedispatchStaysBlockedAndNoPhaseAdvancesThePullRequest()
    {
        // #51: the guard refuses because a pull request is open, and refusing is
        // right - but nothing else is positioned to advance that pull request, so
        // the two halves close every exit. The refusal has to stop being silent
        // and permanent.
        var issueWithOpenPr = BuildIssue(
            "issue-1",
            "#1",
            "Open",
            null,
            pullRequests: [new PullRequestRef("pr-1", 89, "OPEN", null, null, null)]);

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([issueWithOpenPr]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            status: RunStatusNames.Succeeded,
            sessionId: "session-1",
            completedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        // A fresh block is patience, not a fault: recorded, not escalated.
        Assert.Equal(RunStatusNames.Succeeded, (await harness.DbContext.Runs.SingleAsync()).Status);
        var blocked = Assert.Single(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "implementation_redispatch_blocked");

        // Age the block past the point where a repair would plausibly have run.
        blocked.OccurredAtUtc =
            DateTimeOffset.UtcNow - OrchestrationTickService.RedispatchBlockTimeout - TimeSpan.FromMinutes(10);
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        // Reported, with both remedies named - and still not reimplemented, because
        // saying why nothing moves is not permission to open a competing branch.
        Assert.Equal(RunStatusNames.NeedsCommandCenter, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Empty(harness.Coordinator.StartRequests);

        var escalation = Assert.Single(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "needs_command_center");
        Assert.Contains("close or merge PR #89", escalation.Message);
        Assert.Contains("command-center directive", escalation.Message);
        Assert.Contains("no phase is advancing it", escalation.Message);
    }

    [Fact]
    public async Task RunTickAsync_ShouldReportWhenTheBoundedRepairCannotBeDispatched()
    {
        // The verdict landed and no repair run started, which used to be recorded
        // nowhere at all: the plane showed nothing running and everything idle
        // while the repair was in fact being attempted and deferred every tick.
        var tracker = new FakeTrackerClient([]);
        var issue = BuildIssue("issue-1", "#1", "Open", null,
            pullRequests: [new PullRequestRef("pr-5", 5, "OPEN", null, "symphony/1", "main")]);
        tracker.IssuesById["issue-1"] = issue;
        tracker.PullRequestStatusByNumber[5] = new PullRequestStatus(5, "OPEN", false, "aaa111", "SUCCESS", "MERGEABLE");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync("issue-1", "#1", "Open", "instance-1", RunStatusNames.Succeeded);
        await harness.InsertWorkspaceRecordAsync("issue-1", "#1", "symphony/1");
        tracker.OpenPullRequestNumberByHeadBranch["symphony/1"] = 5;

        await harness.Service.RunTickAsync(CancellationToken.None); // seed + verify
        await harness.Service.RunTickAsync(CancellationToken.None); // review dispatched, and it keeps the only slot

        tracker.CommentsByIssueId.TryAdd("issue-1", []);
        tracker.CommentsByIssueId["issue-1"].Add(new NormalizedIssueComment(
            "review-1",
            PhaseOrchestrator.ReviewVerdictMarker(5, "aaa111") + "\nVERDICT: CHANGES_REQUIRED",
            "reviewer", "OWNER", DateTimeOffset.UtcNow));

        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);

        var ledger = Assert.Single(await harness.DbContext.PhaseLedger.ToListAsync());
        Assert.Equal(PhaseStages.Reviewing, ledger.Stage);
        Assert.Single(harness.Coordinator.StartRequests); // the review only; no repair started

        // Said once for the stage, not once per tick.
        var deferral = Assert.Single(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "phase_repair_deferred");
        Assert.Contains("PR #5", deferral.Message);
        Assert.Contains("no agent slot is free", deferral.Message);
    }

    [Fact]
    public async Task RunTickAsync_ShouldAllowRedispatchOfSucceededIssueOncePullRequestIsNoLongerOpen()
    {
        var issueWithMergedPr = BuildIssue(
            "issue-1",
            "#1",
            "Open",
            null,
            pullRequests: [new PullRequestRef("pr-1", 89, "MERGED", null, null, null)]);

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([issueWithMergedPr]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            status: RunStatusNames.Succeeded,
            sessionId: "session-1",
            completedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.Coordinator.StartRequests);
    }

    [Fact]
    public async Task RunTickAsync_ShouldNotReimplementRecoveredOrphanRetryWhenIssueHasOpenPullRequest()
    {
        var issueWithOpenPr = BuildIssue(
            "issue-1",
            "#1",
            "Open",
            null,
            pullRequests: [new PullRequestRef("pr-1", 89, "OPEN", null, null, null)]);

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([issueWithOpenPr]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        // Implementation already produced the open PR, but the owning host died before
        // the final success state was persisted: a running run with a live Codex
        // session owned by a dead instance.
        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-2", sessionId: "session-1");

        // Restart tick: orphan recovery converts the run into a backoff retry.
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        var recoveredRetry = await harness.DbContext.RetryQueue.SingleAsync();
        Assert.Equal(RetryDelayTypes.Backoff, recoveredRetry.DelayType);
        Assert.Equal(RunStatusNames.Retrying, (await harness.DbContext.Runs.SingleAsync()).Status);

        // Next tick with the retry due: the open PR must suppress any new
        // implementation dispatch and escalate instead.
        recoveredRetry.DueAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal(RunStatusNames.NeedsCommandCenter, (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
        var events = await harness.DbContext.EventLog.ToListAsync();
        Assert.Contains(events, entry => entry.EventName == "implementation_redispatch_blocked");
        Assert.Contains(events, entry => entry.EventName == "needs_command_center");
    }

    [Fact]
    public async Task RunTickAsync_ShouldNotReimplementRetryWhenPullRequestEvidenceUnavailable()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1, includePullRequests: false),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", sessionId: "session-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Equal(RunStatusNames.NeedsCommandCenter, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "implementation_redispatch_blocked" &&
                     entry.Message.Contains("include_pull_requests"));
    }

    [Fact]
    public async Task RunTickAsync_ShouldStillRetryWithoutDurableEvidenceWhenPullRequestDataDisabled()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1, includePullRequests: false),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.Coordinator.StartRequests);
        Assert.Equal(RunStatusNames.Running, (await harness.DbContext.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldNotReimplementRetryWhenPullRequestLinkageDisappears()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        // Durable evidence that a PR was linked earlier, even though the live tracker
        // data no longer reports any linkage.
        await harness.InsertIssueCacheAsync(
            "issue-1",
            "#1",
            "Open",
            pullRequestsJson: "[{\"id\":\"pr-1\",\"number\":89,\"state\":\"OPEN\"}]");
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", sessionId: "session-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Equal(RunStatusNames.NeedsCommandCenter, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "implementation_redispatch_blocked" &&
                     entry.Message.Contains("no longer reports any pull request linkage"));
    }

    [Fact]
    public async Task RunTickAsync_ShouldBlockSucceededImplementationRedispatchWhenPullRequestEvidenceUnavailable()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1, includePullRequests: false),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            status: RunStatusNames.Succeeded,
            sessionId: "session-1",
            completedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Equal(RunStatusNames.Succeeded, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "implementation_redispatch_blocked" &&
                     entry.Message.Contains("include_pull_requests"));
    }

    [Fact]
    public async Task RunTickAsync_ShouldBlockSucceededImplementationRedispatchWithoutPullRequestLinkage()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            status: RunStatusNames.Succeeded,
            sessionId: "session-1",
            completedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "implementation_redispatch_blocked" &&
                     entry.Message.Contains("no pull request linkage"));
    }

    [Fact]
    public async Task RunTickAsync_ShouldEscalateAbandonedReleasedRunMissingFromTrackerReload()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync(
            "issue-88",
            "#88",
            "Open",
            "instance-1",
            status: RunStatusNames.ReleasedIneligible,
            sessionId: "session-88",
            completedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "needs_command_center" &&
                     entry.Message.Contains("could not be reloaded"));
    }

    [Fact]
    public async Task RunTickAsync_ShouldUseBackoffRetryAfterFailure()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Failure));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var retryEntry = await harness.DbContext.RetryQueue.SingleAsync();
        Assert.Equal(1, retryEntry.Attempt);
        Assert.Equal(RetryDelayTypes.Backoff, retryEntry.DelayType);
        Assert.True(retryEntry.DueAtUtc > DateTimeOffset.UtcNow.AddSeconds(9));
    }

    [Fact]
    public async Task RunTickAsync_ShouldPersistCandidateDiscoveryAndClaimEventsForNewEligibleIssue()
    {
        var now = DateTimeOffset.Parse("2026-08-29T16:32:14Z");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-86", "#86", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: new FixedTimeProvider(now));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var cachedIssue = await harness.DbContext.IssueCache.SingleAsync();
        Assert.Null(cachedIssue.EligibleSeenAtUtc);

        var eventNames = (await harness.DbContext.EventLog.ToListAsync())
            .Select(entry => entry.EventName)
            .ToList();
        Assert.Contains("candidate_discovered", eventNames);
        Assert.Contains("claim_attempted", eventNames);
        Assert.Contains("claim_succeeded", eventNames);
        Assert.Single(harness.Coordinator.StartRequests);
    }

    [Fact]
    public async Task RunTickAsync_ShouldNotRecordContradictoryRefusalAfterRetryClaimSucceeds()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 5),
            tracker: new FakeTrackerClient([BuildIssue("issue-86", "#86", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-86", "#86", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        var events = await harness.DbContext.EventLog.ToListAsync();
        Assert.Contains(events, entry => entry.EventName == "claim_succeeded");
        Assert.DoesNotContain(
            events,
            entry => entry.EventName == "claim_refused" && entry.Message.Contains("already_running"));
    }

    [Fact]
    public async Task RunTickAsync_ShouldDeduplicateCapacityRefusalsWithinEligibilityEpisode()
    {
        var now = DateTimeOffset.Parse("2026-08-29T18:35:00Z");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-86", "#86", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true),
            timeProvider: new FixedTimeProvider(now));

        await harness.InsertIssueCacheAsync(
            "issue-86",
            "#86",
            "Open",
            cachedAtUtc: now.AddMinutes(-3),
            eligibleSeenAtUtc: now.AddMinutes(-3));
        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);

        var refusalCount = await harness.DbContext.EventLog.CountAsync(
            entry => entry.EventName == "claim_refused" && entry.Message.Contains("concurrency_limit"));
        var warningCount = await harness.DbContext.EventLog.CountAsync(
            entry => entry.EventName == "candidate_acquisition_delayed");
        Assert.Equal(1, refusalCount);
        Assert.Equal(1, warningCount);
    }

    [Fact]
    public async Task RunTickAsync_ShouldReconcileStaleClaimBeforeAcquiringEligibleIssue()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-86", "#86", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertActiveClaimAsync("issue-86", "#86", "instance-1", DateTimeOffset.UtcNow.AddHours(-2));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.Coordinator.StartRequests);
        Assert.Contains(
            await harness.DbContext.DispatchClaims.ToListAsync(),
            claim => claim.IssueId == "issue-86" && claim.Status == "active");
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "stale_reservation_reconciled");
    }

    [Fact]
    public async Task RunTickAsync_ShouldAcquireNextEligibleIssueAfterPriorIssueBecomesTerminal()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient(
                [BuildIssue("issue-2", "#2", "Open", null)],
                issueStatesById: new Dictionary<string, string>
                {
                    ["issue-1"] = "Closed"
                }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.Coordinator.StartRequests);
        Assert.Equal("issue-2", harness.Coordinator.StartRequests.Single().Issue.Id);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.Runs.SingleAsync(run => run.IssueId == "issue-1")).Status);
        Assert.Equal(RunStatusNames.Running, (await harness.DbContext.Runs.SingleAsync(run => run.IssueId == "issue-2")).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldAcquireQueuedIssueAfterRestartWithoutDuplicateRun()
    {
        var now = DateTimeOffset.Parse("2026-08-29T18:32:24Z");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-86", "#86", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: new FixedTimeProvider(now));

        await harness.InsertIssueCacheAsync(
            "issue-86",
            "#86",
            "Open",
            cachedAtUtc: now.AddMinutes(-1),
            eligibleSeenAtUtc: now.AddMinutes(-1));

        await harness.Service.RunTickAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.Coordinator.StartRequests);
        Assert.Single(await harness.DbContext.Runs.Where(run => run.IssueId == "issue-86").ToListAsync());
        Assert.Single(await harness.DbContext.DispatchClaims.Where(claim => claim.IssueId == "issue-86" && claim.Status == "active").ToListAsync());
    }

    [Fact]
    public async Task RunTickAsync_ShouldAcquireQueuedIssueWhenCapacityFrees()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-86", "#86", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Empty(harness.Coordinator.StartRequests);

        var priorRun = await harness.DbContext.Runs.SingleAsync(run => run.IssueId == "issue-1");
        priorRun.Status = RunStatusNames.Succeeded;
        priorRun.CompletedAtUtc = DateTimeOffset.UtcNow;
        var priorClaim = await harness.DbContext.DispatchClaims.SingleAsync(claim => claim.IssueId == "issue-1");
        priorClaim.Status = RunStatusNames.Succeeded;
        priorClaim.ReleasedAtUtc = DateTimeOffset.UtcNow;
        priorClaim.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.Coordinator.StartRequests);
        Assert.Equal("issue-86", harness.Coordinator.StartRequests.Single().Issue.Id);
    }

    [Fact]
    public async Task RunTickAsync_ShouldWarnWhenEligibleIssueIsDelayedByCapacity()
    {
        var now = DateTimeOffset.Parse("2026-08-29T18:35:00Z");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-86", "#86", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: new FixedTimeProvider(now));

        await harness.InsertIssueCacheAsync(
            "issue-86",
            "#86",
            "Open",
            cachedAtUtc: now.AddMinutes(-3),
            eligibleSeenAtUtc: now.AddMinutes(-3));
        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        var events = await harness.DbContext.EventLog.ToListAsync();
        Assert.Contains(events, entry => entry.EventName == "claim_refused" && entry.Message.Contains("concurrency_limit"));
        Assert.Contains(events, entry => entry.EventName == "candidate_acquisition_delayed");
    }

    [Fact]
    public async Task RunTickAsync_ShouldPersistCandidateScanFailure()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], throwOnFetchCandidates: true),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "candidate_scan_failed" && entry.Level == LogLevel.Error.ToString());
    }

    [Fact]
    public async Task RunTickAsync_ShouldAcquireIssueAfterTwoHourDelaySignature()
    {
        var now = DateTimeOffset.Parse("2026-08-29T18:32:24Z");
        var coordinator = new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true);
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-86", "#86", "Open", null)]),
            coordinator,
            timeProvider: new FixedTimeProvider(now));

        await harness.InsertIssueCacheAsync(
            "issue-86",
            "#86",
            "Open",
            cachedAtUtc: now.AddHours(-2),
            eligibleSeenAtUtc: now.AddHours(-2));
        await harness.InsertRunningRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            startedAtUtc: now.AddHours(-2),
            lastEventAtUtc: now.AddHours(-2));

        await harness.Service.RunTickAsync(CancellationToken.None);

        // The incumbent stalls and schedules a retry. Its slot is NOT handed to the
        // waiting issue mid-flight: that hand-off is what made two ready issues take
        // turns destroying each other's runs every three minutes (ADCP#25). The waiting
        // issue is still visibly waiting, and the SLO diagnostic still says so.
        var retryingRun = await harness.DbContext.Runs.SingleAsync(run => run.IssueId == "issue-1");
        Assert.Equal(RunStatusNames.Retrying, retryingRun.Status);
        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "candidate_acquisition_delayed" && entry.IssueId == "issue-86");

        // Once the incumbent's run is genuinely over, the long-waiting issue is acquired.
        retryingRun.Status = RunStatusNames.CanceledByReconciliation;
        retryingRun.CompletedAtUtc = now;
        harness.DbContext.RetryQueue.RemoveRange(await harness.DbContext.RetryQueue.ToListAsync());
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.Coordinator.StartRequests);
        Assert.Equal("issue-86", harness.Coordinator.StartRequests.Single().Issue.Id);
        var run = await harness.DbContext.Runs.SingleAsync(run => run.IssueId == "issue-86");
        Assert.Equal(RunStatusNames.Running, run.Status);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "claim_succeeded" && entry.IssueId == "issue-86");
    }

    [Fact]
    public async Task RunTickAsync_ShouldHonorPerStateConcurrencyLimits()
    {
        var workflow = BuildWorkflowDefinition(
            maxConcurrentAgents: 5,
            maxConcurrentByState: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["open"] = 1
            });

        await using var harness = await TestHarness.CreateAsync(
            workflow,
            tracker: new FakeTrackerClient([BuildIssue("issue-2", "#2", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
    }

    [Fact]
    public async Task RunTickAsync_ShouldRejectTodoIssuesWithActiveBlockers()
    {
        var workflow = BuildWorkflowDefinition(
            maxConcurrentAgents: 1,
            activeStates: ["Todo"]);

        var todoIssue = BuildIssue(
            "issue-1",
            "#1",
            "Todo",
            [new BlockerRef("issue-0", "#0", "Open")]);

        await using var harness = await TestHarness.CreateAsync(
            workflow,
            tracker: new FakeTrackerClient([todoIssue]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
    }

    [Fact]
    public async Task RunTickAsync_ShouldStopTerminalRunsAndCleanupWorkspace()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldPersistTerminalStopBeforeCancelingLiveRun()
    {
        var coordinator = new FakeIssueExecutionCoordinator(
            FakeDispatchOutcome.LeaveRunning,
            observeStopStateWithFreshContext: true);

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator);

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.NotNull(coordinator.ObservedStopState);
        Assert.Equal(RunStopReasons.Terminal, coordinator.ObservedStopState.Value.RequestedStopReason);
        Assert.True(coordinator.ObservedStopState.Value.CleanupWorkspaceOnStop);
    }

    [Fact]
    public async Task RunTickAsync_ShouldRefreshTrackedIssueCacheStateForClosedIssues()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        var initialCachedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open", initialCachedAtUtc);

        await harness.Service.RunTickAsync(CancellationToken.None);

        var cachedIssue = await harness.DbContext.IssueCache.SingleAsync();
        Assert.Equal("Closed", cachedIssue.State);
        Assert.True(cachedIssue.CachedAtUtc > initialCachedAtUtc);
    }

    [Fact]
    public async Task RunTickAsync_ShouldCleanupRetryWorkspaceWhenTrackedIssueBecomesClosed()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open", DateTimeOffset.UtcNow.AddMinutes(-5));
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        var cleanupRequest = Assert.Single(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal("#1", cleanupRequest.IssueIdentifier);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
        Assert.NotNull((await harness.DbContext.WorkspaceRecords.SingleAsync()).LastCleanedAtUtc);
    }

    [Fact]
    public async Task RunTickAsync_ShouldResetLastReportedTokenTotalsWhenRetryStartsNewAttempt()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            inputTokens: 100,
            outputTokens: 50,
            totalTokens: 150,
            lastReportedInputTokens: 100,
            lastReportedOutputTokens: 50,
            lastReportedTotalTokens: 150);

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.Running, run.Status);
        Assert.Equal(100, run.InputTokens);
        Assert.Equal(50, run.OutputTokens);
        Assert.Equal(150, run.TotalTokens);
        Assert.Equal(0, run.LastReportedInputTokens);
        Assert.Equal(0, run.LastReportedOutputTokens);
        Assert.Equal(0, run.LastReportedTotalTokens);
    }

    [Fact]
    public async Task RunTickAsync_ShouldStopNonActiveRunsWithoutCleanup()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Blocked"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldDetectStalledRunsFromLastActivity()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            startedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            lastEventAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        var retry = await harness.DbContext.RetryQueue.SingleAsync();
        Assert.Equal(RunStatusNames.Retrying, run.Status);
        Assert.Equal(1, retry.Attempt);
    }

    [Fact]
    public async Task RunTickAsync_ShouldReconcileBeforeSkippingInvalidDispatch()
    {
        var workflow = BuildWorkflowDefinition(maxConcurrentAgents: 1, apiKey: "$MISSING_TOKEN");

        await using var harness = await TestHarness.CreateAsync(
            workflow,
            tracker: new FakeTrackerClient([]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            startedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            lastEventAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.False(harness.Tracker.FetchCandidateIssuesCalled);
        Assert.Equal(RunStatusNames.Retrying, (await harness.DbContext.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldNotRecoverRunsOwnedByInstancesWithLiveLease()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-2");
        await harness.InsertLeaseAsync("poll-dispatch", "instance-2", DateTimeOffset.UtcNow.AddMinutes(5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        var claim = await harness.DbContext.DispatchClaims.SingleAsync();

        Assert.Equal("instance-2", run.OwnerInstanceId);
        Assert.Equal(RunStatusNames.Running, run.Status);
        Assert.Equal("instance-2", claim.ClaimedByInstanceId);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
    }

    // ADCP#24. Every queued implementer starved in the same minute when the shared
    // Claude account hit its session limit, and each one retried into the same wall.
    [Fact]
    public async Task RunTickAsync_ShouldRetryOnTheOtherVendorWhenTheDispatchedOneIsOutOfQuota()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1, defaultRunner: "claude", fallbackRunner: "codex"),
            tracker: new FakeTrackerClient(
                [BuildIssue("issue-1", "#1", "Open", null)],
                issueStatesById: new Dictionary<string, string> { ["issue-1"] = "Open" }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: new FixedTimeProvider(now));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", nowUtcOverride: now);
        var run = await harness.DbContext.Runs.SingleAsync();
        run.Runner = "claude";
        (await harness.DbContext.RetryQueue.SingleAsync()).Error =
            "You've hit your session limit · resets 1:40am (America/New_York)";
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        var request = Assert.Single(harness.Coordinator.StartRequests);
        Assert.Equal("codex", request.RunnerOverride);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "quota_fallback_dispatched");
    }

    [Fact]
    public async Task RunTickAsync_ShouldKeepAnOrdinaryFailureWithTheVendorThatProducedIt()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1, defaultRunner: "claude", fallbackRunner: "codex"),
            tracker: new FakeTrackerClient(
                [BuildIssue("issue-1", "#1", "Open", null)],
                issueStatesById: new Dictionary<string, string> { ["issue-1"] = "Open" }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: new FixedTimeProvider(now));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", nowUtcOverride: now);
        var run = await harness.DbContext.Runs.SingleAsync();
        run.Runner = "claude";
        (await harness.DbContext.RetryQueue.SingleAsync()).Error = "stall timeout exceeded";
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        // Handing a failing implementation to a different vendor substitutes its
        // judgement for the work already done. Only exhaustion justifies that.
        var request = Assert.Single(harness.Coordinator.StartRequests);
        Assert.Null(request.RunnerOverride);
        Assert.DoesNotContain(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "quota_fallback_dispatched");
    }

    [Fact]
    public async Task RunTickAsync_ShouldNotFallBackWhenNoOtherVendorIsConfigured()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1, defaultRunner: "claude"),
            tracker: new FakeTrackerClient(
                [BuildIssue("issue-1", "#1", "Open", null)],
                issueStatesById: new Dictionary<string, string> { ["issue-1"] = "Open" }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: new FixedTimeProvider(now));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", nowUtcOverride: now);
        var run = await harness.DbContext.Runs.SingleAsync();
        run.Runner = "claude";
        (await harness.DbContext.RetryQueue.SingleAsync()).Error = "You've hit your session limit";
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Null(Assert.Single(harness.Coordinator.StartRequests).RunnerOverride);
    }

    // ADCP#25. Replays the observed alternation: two issues carrying symphony-ready,
    // max_concurrent_agents 1, one agent that keeps stalling. Before the fix the two
    // issues took it in turns to destroy each other's runs every ~3 minutes and nothing
    // ever finished - released_ineligible became the most common status in the database.
    [Fact]
    public async Task RunTickAsync_ShouldNotDestroyADueRetryThatMerelyLostTheSlotToAnotherIssue()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-01T10:00:00Z"));
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient(
                [BuildIssue("issue-1", "#1", "Open", null), BuildIssue("issue-2", "#2", "Open", null)],
                issueStatesById: new Dictionary<string, string>
                {
                    ["issue-1"] = "Open",
                    ["issue-2"] = "Open"
                }),
            coordinator: new FakeIssueExecutionCoordinator(
                FakeDispatchOutcome.LeaveRunning,
                stopReturnsFalse: true),
            timeProvider: clock);

        // #1 is dispatched; #2 waits for the slot.
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Equal(
            RunStatusNames.Running,
            (await harness.DbContext.Runs.SingleAsync(run => run.IssueId == "issue-1")).Status);
        Assert.False(await harness.DbContext.Runs.AnyAsync(run => run.IssueId == "issue-2"));

        // #1 stalls and schedules a retry. Its reservation survives: work in flight is
        // not surrendered to a competitor just because no process is live this instant.
        clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Service.RunTickAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Service.RunTickAsync(CancellationToken.None);

        var issueOneRun = await harness.DbContext.Runs.SingleAsync(run => run.IssueId == "issue-1");
        Assert.NotEqual(RunStatusNames.ReleasedIneligible, issueOneRun.Status);
        Assert.Null(issueOneRun.CompletedAtUtc);

        // #2 was never started into the gap, so nothing was spent on work that would
        // have been thrown away three minutes later.
        Assert.DoesNotContain(harness.Coordinator.StartRequests, request => request.Issue.Id == "issue-2");
    }

    [Fact]
    public async Task RunTickAsync_ShouldRescheduleRatherThanReleaseARetryRefusedForCapacity()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient(
                [BuildIssue("issue-1", "#1", "Open", null)],
                issueStatesById: new Dictionary<string, string> { ["issue-1"] = "Open" }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: new FixedTimeProvider(now));

        // #1 has a due retry; a different issue is occupying the only slot.
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", nowUtcOverride: now);
        await harness.InsertRunningRunAsync("issue-2", "#2", "Open", "instance-1", startedAtUtc: now);

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync(entity => entity.IssueId == "issue-1");
        Assert.Equal(RunStatusNames.Retrying, run.Status);
        Assert.Null(run.CompletedAtUtc);

        // The reservation is kept and pushed forward, and waiting does not spend the
        // retry budget of work that has not been tried yet.
        var reservation = await harness.DbContext.RetryQueue.SingleAsync(entry => entry.IssueId == "issue-1");
        Assert.Equal(1, reservation.Attempt);
        Assert.True(reservation.DueAtUtc > now);
        Assert.Equal("no available orchestrator slots", reservation.Error);
    }

    [Fact]
    public async Task RunTickAsync_ShouldStillReleaseARetryWhoseIssueIsGenuinelyIneligible()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient(
                [BuildIssue("issue-1", "#1", "Closed", null)],
                issueStatesById: new Dictionary<string, string> { ["issue-1"] = "Closed" }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: new FixedTimeProvider(now));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", nowUtcOverride: now);

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.ReleasedIneligible, run.Status);

        // And it now says WHICH kind of ineligible. "Issue no longer eligible for
        // dispatch" read the same whether the label was removed, the issue was closed,
        // or the scheduler simply changed its mind - and only one of those was a bug.
        Assert.Contains("terminal_state", run.LastMessage);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
    }

    [Fact]
    public async Task RunTickAsync_ShouldEndARetryThatHasSpentItsAttemptBudget()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient(
                [BuildIssue("issue-1", "#1", "Open", null)],
                issueStatesById: new Dictionary<string, string> { ["issue-1"] = "Open" }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: new FixedTimeProvider(now));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", nowUtcOverride: now);
        var reservation = await harness.DbContext.RetryQueue.SingleAsync();
        reservation.Attempt = 99;
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);

        // Without a ceiling, honouring a reservation for the life of the run would let
        // one issue that never recovers hold the queue against everything behind it.
        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Empty(harness.Coordinator.StartRequests);
    }

    // ADCP#23. The reproduction is the whole point: before the fix these three tests
    // leave a run in 'retrying' forever, with an elapsed due_at and no running run,
    // which is exactly the state the plane was found in - reporting itself idle with
    // a free slot while refusing to dispatch anything at all.
    [Fact]
    public async Task RunTickAsync_ShouldEndStartupExhaustedRunInsteadOfParkingItInRetryingForever()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-01T10:00:00Z"));
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient(
                [BuildIssue("issue-1", "#1", "Open", null)],
                issueStatesById: new Dictionary<string, string> { ["issue-1"] = "Open" }),
            coordinator: new FakeIssueExecutionCoordinator(
                FakeDispatchOutcome.LeaveRunning,
                stopReturnsFalse: true),
            timeProvider: clock);

        // The agent starts but never reports a session. Any CLI that exits without a
        // handshake looks like this from here.
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Equal(RunStatusNames.Running, (await harness.DbContext.Runs.SingleAsync()).Status);

        // First startup timeout: stalled, one retry scheduled. This part is correct.
        clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Equal(RunStatusNames.Retrying, (await harness.DbContext.Runs.SingleAsync()).Status);

        // Second attempt, which also never reaches a session.
        clock.Advance(TimeSpan.FromSeconds(30));
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Equal(RunStatusNames.Running, (await harness.DbContext.Runs.SingleAsync()).Status);

        // Budget exhausted. Before the fix this was stopped as "stalled", which
        // scheduled a third retry that the claim store then refused forever with
        // startup_attempt_fence.
        clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());

        // The slot is free again: the claim is no longer active, so the plane is not
        // holding the only agent slot against a run that can never resume.
        Assert.Equal(
            RunStatusNames.NeedsCommandCenter,
            (await harness.DbContext.DispatchClaims.SingleAsync()).Status);

        // And it stays terminal - no later tick resurrects it into 'retrying'.
        clock.Advance(TimeSpan.FromMinutes(10));
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Equal(RunStatusNames.NeedsCommandCenter, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
    }

    [Fact]
    public async Task RunTickAsync_ShouldKeepAnActiveRateLimitPauseAcrossAProcessRestart()
    {
        // A rate limit is on the token, not on the process. Restarting therefore
        // cannot clear it, and the plane must not behave as though it might: the
        // pause used to live only in a field that starts at MinValue, so a restart
        // made the next tick due immediately and it scanned every repository straight
        // back into the limit, re-arming it.
        //
        // This is not a rare corner. Restarting is exactly what a person does when the
        // dashboard says things need attention, and a rate limit is precisely when it
        // will say so.
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var clock = new MutableTimeProvider(now);
        var tracker = new FakeTrackerClient([]) { RateLimitOnFetchCandidates = true };

        var before = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: tracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: clock);

        string dbPath;
        try
        {
            await before.Service.RunTickAsync(CancellationToken.None);
            Assert.Single(tracker.CandidateFetchRepositories);
            Assert.Single(await before.DbContext.EventLog
                .Where(e => e.EventName == "candidate_scan_paused")
                .ToListAsync());
            dbPath = before.DbPath;
        }
        finally
        {
            // Dispose rather than `await using`: the replacement harness has to open
            // the same database, and a restart means the first process is gone.
            await before.DisposeAsync();
        }

        // The restart. A minute later - far inside the ten-minute pause - a brand new
        // service opens the same database with a fresh in-memory schedule.
        clock.Advance(TimeSpan.FromMinutes(1));
        var afterTracker = new FakeTrackerClient([]) { RateLimitOnFetchCandidates = true };
        await using var after = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: afterTracker,
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: clock,
            reuseDbPath: dbPath);

        await after.Service.RunTickAsync(CancellationToken.None);

        // The assertion that matters: GitHub was not asked again.
        Assert.Empty(afterTracker.CandidateFetchRepositories);

        // And the pause still ends when it was always going to, rather than being
        // extended by the restart.
        clock.Advance(TimeSpan.FromMinutes(10));
        await after.Service.RunTickAsync(CancellationToken.None);
        Assert.Single(afterTracker.CandidateFetchRepositories);
    }

    [Fact]
    public async Task RunTickAsync_ShouldEscalateRetryThatStaysDueAndUndispatchedPastTheWedgeThreshold()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var clock = new MutableTimeProvider(now);
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            // Not a candidate and not resolvable by id: nothing in the ordinary paths
            // can act on this reservation, which is what "wedged" means.
            tracker: new FakeTrackerClient([], throwOnFetchStatesByIds: true),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: clock);

        await harness.InsertRetryingRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            nowUtcOverride: now,
            dueAtUtc: now.AddMinutes(-90));

        // One tick only observes it; a single overdue reading is not a wedge, because
        // after downtime every pending reservation reads as overdue.
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Equal(RunStatusNames.Retrying, (await harness.DbContext.Runs.SingleAsync()).Status);

        // Still due, still undispatched, a full grace period later.
        clock.Advance(TimeSpan.FromMinutes(25));
        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "wedged_retry_reconciled");
    }

    [Fact]
    public async Task RunTickAsync_ShouldEscalateRetryingRunThatHasNoReservationAtAll()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var clock = new MutableTimeProvider(now);
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], throwOnFetchStatesByIds: true),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: clock);

        await harness.InsertRetryingRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            nowUtcOverride: now,
            withReservation: false);

        await harness.Service.RunTickAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(25));
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal(RunStatusNames.NeedsCommandCenter, (await harness.DbContext.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldNotEscalateRetryThatIsSimplyWaitingItsTurn()
    {
        var now = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        var clock = new MutableTimeProvider(now);
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], throwOnFetchStatesByIds: true),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning),
            timeProvider: clock);

        await harness.InsertRetryingRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            nowUtcOverride: now,
            dueAtUtc: now.AddHours(4));

        await harness.Service.RunTickAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(90));
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal(RunStatusNames.Retrying, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Single(await harness.DbContext.RetryQueue.ToListAsync());
    }

    private static WorkflowDefinition BuildWorkflowDefinition(
        int maxConcurrentAgents,
        IReadOnlyList<string>? activeStates = null,
        IReadOnlyDictionary<string, int>? maxConcurrentByState = null,
        string apiKey = "test-token",
        bool includePullRequests = true,
        bool mergePolicyEnabled = false,
        IReadOnlyList<string>? protectedPaths = null,
        IReadOnlyList<string>? trackerLabels = null,
        string defaultRunner = "codex",
        string? fallbackRunner = null,
        IReadOnlyList<(string Owner, string Repo)>? repositories = null)
    {
        var runtime = new WorkflowRuntimeSettings(
            new WorkflowTrackerSettings(
                Kind: "github",
                Endpoint: "https://api.github.com/graphql",
                ApiKey: apiKey,
                Owner: "released",
                Repo: "symphony",
                Milestone: null,
                IncludePullRequests: includePullRequests,
                Labels: trackerLabels ?? [],
                ActiveStates: activeStates ?? ["Open"],
                TerminalStates: ["Closed"],
                Repositories: repositories?
                    .Select((entry, index) => new WorkflowRepositorySettings(
                        entry.Owner,
                        entry.Repo,
                        index == 0 ? "./workspaces/repo" : $"./workspaces/repos/{entry.Repo.ToLowerInvariant()}",
                        index == 0 ? "./workspaces/worktrees" : $"./workspaces/worktrees-{entry.Repo.ToLowerInvariant()}",
                        $"https://github.com/{entry.Owner}/{entry.Repo}.git"))
                    .ToList()),
            new WorkflowPollingSettings(600_000),
            new WorkflowAgentSettings(
                MaxConcurrentAgents: maxConcurrentAgents,
                MaxTurns: 20,
                MaxRetryBackoffMs: 300_000,
                MaxConcurrentAgentsByState: maxConcurrentByState ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                DefaultRunner: defaultRunner,
                RunnerByLabel: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                FallbackRunner: fallbackRunner),
            new WorkflowServerSettings(Port: null),
            new WorkflowWorkspaceSettings("./workspaces", "./workspaces/repo", "./workspaces/worktrees", "main", null),
            new WorkflowHooksSettings(null, null, null, null, 60_000),
            new WorkflowCodexSettings("codex app-server", 30_000, "never", "danger-full-access", "danger-full-access", 5_000, 300_000),
            new WorkflowClaudeSettings("claude", 30_000, "bypassPermissions", null, 600_000),
            new WorkflowMergePolicySettings(
                Enabled: mergePolicyEnabled,
                Method: "squash",
                ProtectedPaths: protectedPaths ?? ["**/*.csproj", ".github/**"],
                MaxChangedFiles: 50),
            new WorkflowEventLogRetentionSettings(
                Enabled: false,
                ProtocolRetentionDays: 3,
                OperationalRetentionDays: 180,
                MaxRows: 250_000));

        return new WorkflowDefinition(new Dictionary<string, object?>(), "Prompt body", runtime, "WORKFLOW.md", DateTimeOffset.UtcNow);
    }

    private static NormalizedIssue BuildIssue(
        string id,
        string identifier,
        string state,
        IReadOnlyList<BlockerRef>? blockedBy,
        IReadOnlyList<PullRequestRef>? pullRequests = null,
        string repository = "")
    {
        return new NormalizedIssue(
            id,
            identifier,
            $"Issue {identifier}",
            null,
            1,
            state,
            null,
            null,
            null,
            [],
            pullRequests ?? [],
            blockedBy ?? [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            repository);
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly string dbPath;

        private TestHarness(
            string dbPath,
            SymphonyDbContext dbContext,
            FakeTrackerClient tracker,
            FakeWorkspaceManager workspaceManager,
            FakeIssueExecutionCoordinator coordinator,
            OrchestrationTickService service)
        {
            this.dbPath = dbPath;
            DbContext = dbContext;
            Tracker = tracker;
            WorkspaceManager = workspaceManager;
            Coordinator = coordinator;
            Service = service;
        }

        public string DbPath => dbPath;

        public SymphonyDbContext DbContext { get; }
        public FakeTrackerClient Tracker { get; }
        public FakeWorkspaceManager WorkspaceManager { get; }
        public FakeIssueExecutionCoordinator Coordinator { get; }
        public OrchestrationTickService Service { get; }

        public static async Task<TestHarness> CreateAsync(
            WorkflowDefinition workflowDefinition,
            FakeTrackerClient tracker,
            FakeIssueExecutionCoordinator coordinator,
            TimeProvider? timeProvider = null,
            string? reuseDbPath = null)
        {
            var dbPath = reuseDbPath ?? Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-orchestration.db");
            var options = new DbContextOptionsBuilder<SymphonyDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            var dbContext = new SymphonyDbContext(options);
            if (reuseDbPath is null)
            {
                await dbContext.Database.EnsureDeletedAsync();
                await dbContext.Database.EnsureCreatedAsync();
            }

            // Reusing a path means the schema is already there - this models a process
            // restart against an existing database, not a new install. Calling
            // EnsureCreated again is not harmless: on Linux it threw
            // `table "directive_log" already exists`, while on Windows it happened to
            // no-op, so the mistake passed locally and failed only in CI.

            var workspaceManager = new FakeWorkspaceManager();
            coordinator.Attach(dbContext, dbPath);
            var clock = timeProvider ?? TimeProvider.System;

            var service = new OrchestrationTickService(
                new FakeWorkflowDefinitionProvider(workflowDefinition),
                tracker,
                new OrchestrationCoordinationStore(dbContext, clock),
                dbContext,
                workspaceManager,
                coordinator,
                new EscalationPublisher(
                    dbContext,
                    tracker,
                    TimeProvider.System,
                    NullLogger<EscalationPublisher>.Instance),
                new DirectiveProcessor(
                    dbContext,
                    tracker,
                    TimeProvider.System,
                    NullLogger<DirectiveProcessor>.Instance),
                new PhaseOrchestrator(
                    dbContext,
                    tracker,
                    TimeProvider.System,
                    NullLogger<PhaseOrchestrator>.Instance),
                new EventLogRetentionService(
                    dbContext,
                    clock,
                    NullLogger<EventLogRetentionService>.Instance),
                new TrackerReachability(clock),
                Options.Create(new OrchestrationOptions
                {
                    InstanceId = "instance-1",
                    LeaseName = "poll-dispatch",
                    LeaseTtlSeconds = 900
                }),
                clock,
                NullLogger<OrchestrationTickService>.Instance);

            return new TestHarness(dbPath, dbContext, tracker, workspaceManager, coordinator, service);
        }

        public async Task InsertRunningRunAsync(
            string issueId,
            string identifier,
            string state,
            string instanceId,
            DateTimeOffset? startedAtUtc = null,
            DateTimeOffset? lastEventAtUtc = null,
            string? sessionId = null)
        {
            var run = new RunEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                IssueId = issueId,
                IssueIdentifier = identifier,
                OwnerInstanceId = instanceId,
                Status = RunStatusNames.Running,
                State = state,
                SessionId = sessionId,
                StartedAtUtc = startedAtUtc ?? DateTimeOffset.UtcNow
            };
            run.LastEventAtUtc = lastEventAtUtc;

            DbContext.Runs.Add(run);
            DbContext.RunAttempts.Add(new RunAttemptEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                RunId = run.Id,
                IssueId = issueId,
                Status = RunStatusNames.Running,
                StartedAtUtc = run.StartedAtUtc
            });
            DbContext.DispatchClaims.Add(new DispatchClaimEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                ClaimedByInstanceId = instanceId,
                ClaimedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Status = "active"
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertLeaseAsync(string leaseName, string ownerInstanceId, DateTimeOffset expiresAtUtc)
        {
            DbContext.InstanceLeases.Add(new InstanceLeaseEntity
            {
                LeaseName = leaseName,
                OwnerInstanceId = ownerInstanceId,
                AcquiredAtUtc = expiresAtUtc.AddMinutes(-5),
                ExpiresAtUtc = expiresAtUtc,
                UpdatedAtUtc = expiresAtUtc.AddMinutes(-1)
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertIssueCacheAsync(
            string issueId,
            string identifier,
            string state,
            DateTimeOffset? cachedAtUtc = null,
            DateTimeOffset? eligibleSeenAtUtc = null,
            string pullRequestsJson = "[]")
        {
            var nowUtc = cachedAtUtc ?? DateTimeOffset.UtcNow;
            DbContext.IssueCache.Add(new IssueCacheEntity
            {
                IssueId = issueId,
                Identifier = identifier,
                Title = $"Issue {identifier}",
                State = state,
                LabelsJson = "[]",
                PullRequestsJson = pullRequestsJson,
                BlockedByJson = "[]",
                EligibleSeenAtUtc = eligibleSeenAtUtc,
                CachedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertActiveClaimAsync(
            string issueId,
            string identifier,
            string instanceId,
            DateTimeOffset claimedAtUtc)
        {
            DbContext.DispatchClaims.Add(new DispatchClaimEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                ClaimedByInstanceId = instanceId,
                ClaimedAtUtc = claimedAtUtc,
                UpdatedAtUtc = claimedAtUtc,
                Status = "active"
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertWorkspaceRecordAsync(string issueId, string identifier, string branchName)
        {
            DbContext.WorkspaceRecords.Add(new WorkspaceRecordEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                WorkspacePath = $"C:\\tmp\\{identifier}",
                BranchName = branchName,
                LastPreparedAtUtc = DateTimeOffset.UtcNow
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertRunAsync(
            string issueId,
            string identifier,
            string state,
            string instanceId,
            string status,
            string? sessionId = null,
            DateTimeOffset? completedAtUtc = null)
        {
            DbContext.Runs.Add(new RunEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                IssueId = issueId,
                IssueIdentifier = identifier,
                OwnerInstanceId = instanceId,
                Status = status,
                State = state,
                SessionId = sessionId,
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAtUtc = completedAtUtc
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertRetryingRunAsync(
            string issueId,
            string identifier,
            string state,
            string instanceId,
            int inputTokens = 0,
            int outputTokens = 0,
            int totalTokens = 0,
            int lastReportedInputTokens = 0,
            int lastReportedOutputTokens = 0,
            int lastReportedTotalTokens = 0,
            string delayType = RetryDelayTypes.Backoff,
            string? sessionId = null,
            DateTimeOffset? nowUtcOverride = null,
            DateTimeOffset? dueAtUtc = null,
            bool withReservation = true)
        {
            var nowUtc = nowUtcOverride ?? DateTimeOffset.UtcNow;
            var run = new RunEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                IssueId = issueId,
                IssueIdentifier = identifier,
                OwnerInstanceId = instanceId,
                Status = RunStatusNames.Retrying,
                State = state,
                CurrentRetryAttempt = 1,
                SessionId = sessionId,
                StartedAtUtc = nowUtc.AddMinutes(-1),
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = totalTokens,
                LastReportedInputTokens = lastReportedInputTokens,
                LastReportedOutputTokens = lastReportedOutputTokens,
                LastReportedTotalTokens = lastReportedTotalTokens
            };

            DbContext.Runs.Add(run);
            if (withReservation)
            {
                DbContext.RetryQueue.Add(new RetryQueueEntity
                {
                    IssueId = issueId,
                    IssueIdentifier = identifier,
                    RunId = run.Id,
                    OwnerInstanceId = instanceId,
                    Attempt = 1,
                    DueAtUtc = dueAtUtc ?? nowUtc.AddSeconds(-1),
                    DelayType = delayType,
                    MaxBackoffMs = 300_000,
                    CreatedAtUtc = nowUtc.AddMinutes(-1),
                    UpdatedAtUtc = nowUtc.AddMinutes(-1)
                });
            }
            DbContext.DispatchClaims.Add(new DispatchClaimEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                ClaimedByInstanceId = instanceId,
                ClaimedAtUtc = nowUtc.AddMinutes(-1),
                UpdatedAtUtc = nowUtc.AddMinutes(-1),
                Status = "active"
            });
            DbContext.WorkspaceRecords.Add(new WorkspaceRecordEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                WorkspacePath = $"C:\\tmp\\{identifier}",
                BranchName = $"symphony/{identifier}",
                LastPreparedAtUtc = nowUtc.AddMinutes(-1)
            });

            await DbContext.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            TryDeleteFile(dbPath);
            TryDeleteFile($"{dbPath}-wal");
            TryDeleteFile($"{dbPath}-shm");
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FakeWorkflowDefinitionProvider(WorkflowDefinition workflowDefinition) : IWorkflowDefinitionProvider
    {
        public Task<WorkflowDefinition> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(workflowDefinition);
    }

    private sealed class FakeTrackerClient(
        IReadOnlyList<NormalizedIssue> issues,
        IReadOnlyDictionary<string, string>? issueStatesById = null,
        bool throwOnFetchStatesByIds = false,
        bool throwOnFetchCandidates = false) : IGitHubTrackerClient
    {
        // Per-repository candidates and per-repository outages, so a fan-out can be
        // told apart from a single fetch that happens to return everything.
        public Dictionary<string, IReadOnlyList<NormalizedIssue>> IssuesByRepository { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RepositoriesThatFail { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CandidateFetchRepositories { get; } = [];
        public List<(string Repository, int Number)> PullRequestStatusRequests { get; } = [];

        private readonly Dictionary<string, string> statesById = issueStatesById is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(issueStatesById, StringComparer.OrdinalIgnoreCase);

        public bool FetchCandidateIssuesCalled { get; private set; }

        /// <summary>Fail candidate fetch the way a real rate limit does.</summary>
        public bool RateLimitOnFetchCandidates { get; set; }

        public Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(TrackerQuery query, CancellationToken cancellationToken = default)
        {
            var repository = $"{query.Owner}/{query.Repo}";
            CandidateFetchRepositories.Add(repository);

            if (RateLimitOnFetchCandidates)
            {
                // The distinction under test. A generic outage retries next tick; a
                // rate limit must pause, because the limit is on the token and asking
                // again only spends the request that would have worked later.
                throw new GitHubTrackerException(
                    GitHubTrackerException.RateLimitedCode,
                    "GitHub GraphQL: API rate limit already exceeded");
            }

            if (throwOnFetchCandidates || RepositoriesThatFail.Contains(repository))
            {
                throw new InvalidOperationException("simulated candidate scan outage");
            }

            FetchCandidateIssuesCalled = true;
            return Task.FromResult(IssuesByRepository.Count > 0
                ? IssuesByRepository.TryGetValue(repository, out var perRepository) ? perRepository : []
                : issues);
        }

        public Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(TrackerQuery query, IReadOnlyList<string> states, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);

        public Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(TrackerQuery query, IReadOnlyList<string> issueIds, CancellationToken cancellationToken = default)
        {
            if (throwOnFetchStatesByIds)
            {
                throw new InvalidOperationException("simulated tracker outage");
            }

            var snapshots = issueIds
                .Where(id => statesById.ContainsKey(id))
                .Select(id => new IssueStateSnapshot(id, statesById[id]))
                .ToList();
            return Task.FromResult<IReadOnlyList<IssueStateSnapshot>>(snapshots);
        }

        public Task<GitHubGraphQlExecutionResult> ExecuteGitHubGraphQlAsync(
            TrackerQuery query,
            string graphQlDocument,
            string? variablesJson,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitHubGraphQlExecutionResult(true, "{\"data\":{}}"));
        }

        public List<(string IssueId, string Body)> PostedComments { get; } = [];
        public bool ThrowOnPostComment { get; set; }
        public bool ThrowOnFetchCommentMarker { get; set; }
        public bool ReturnNullCommentMarkerSnapshot { get; set; }
        public bool MarkerAlreadyPresent { get; set; }

        public Task<IssueCommentMarkerSnapshot?> FetchIssueCommentMarkerAsync(
            TrackerQuery query,
            string issueId,
            string marker,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnFetchCommentMarker)
            {
                throw new InvalidOperationException("simulated tracker outage on comment scan");
            }

            if (ReturnNullCommentMarkerSnapshot)
            {
                return Task.FromResult<IssueCommentMarkerSnapshot?>(null);
            }

            // Behaves like the real scan: a comment posted earlier through this fake
            // is visible to later marker checks.
            var found = MarkerAlreadyPresent ||
                PostedComments.Any(comment =>
                    string.Equals(comment.IssueId, issueId, StringComparison.OrdinalIgnoreCase) &&
                    comment.Body.Contains(marker, StringComparison.Ordinal));
            var state = statesById.TryGetValue(issueId, out var issueState) ? issueState : "Open";
            return Task.FromResult<IssueCommentMarkerSnapshot?>(
                new IssueCommentMarkerSnapshot(issueId, state, null, found));
        }

        public Task<string?> PostIssueCommentAsync(
            TrackerQuery query,
            string issueId,
            string body,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnPostComment)
            {
                throw new InvalidOperationException("simulated comment post failure");
            }

            PostedComments.Add((issueId, body));
            // Mirror the real tracker: posted comments become visible to later
            // comment fetches (marker scans, directive ack detection).
            CommentsByIssueId.TryAdd(issueId, []);
            CommentsByIssueId[issueId].Add(new NormalizedIssueComment(
                $"posted-{PostedComments.Count}",
                body,
                "symphony-bot",
                "OWNER",
                DateTimeOffset.UtcNow));
            return Task.FromResult<string?>($"https://example.test/comments/{PostedComments.Count}");
        }

        public Dictionary<string, List<NormalizedIssueComment>> CommentsByIssueId { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, NormalizedIssue> IssuesById { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> ClosedIssueIds { get; } = [];

        public Task<IReadOnlyList<NormalizedIssueComment>> FetchIssueCommentsAsync(
            TrackerQuery query,
            string issueId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NormalizedIssueComment>>(
                CommentsByIssueId.TryGetValue(issueId, out var comments) ? [.. comments] : []);
        }

        public Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByIdsAsync(
            TrackerQuery query,
            IReadOnlyList<string> issueIds,
            CancellationToken cancellationToken = default)
        {
            var result = issueIds
                .Where(id => IssuesById.ContainsKey(id))
                .Select(id => IssuesById[id])
                .ToList();
            return Task.FromResult<IReadOnlyList<NormalizedIssue>>(result);
        }

        public Task CloseIssueAsync(
            TrackerQuery query,
            string issueId,
            CancellationToken cancellationToken = default)
        {
            ClosedIssueIds.Add(issueId);
            return Task.CompletedTask;
        }

        public Dictionary<int, PullRequestStatus> PullRequestStatusByNumber { get; } = [];

        public Dictionary<string, int> OpenPullRequestNumberByHeadBranch { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<int, List<string>> PullRequestFilesByNumber { get; } = [];
        public List<(int Number, string HeadSha, string Method)> MergedPullRequests { get; } = [];
        public string? MergeRefusal { get; set; }

        public Task<IReadOnlyList<string>> FetchPullRequestFilesAsync(
            TrackerQuery query,
            int pullRequestNumber,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(
                PullRequestFilesByNumber.TryGetValue(pullRequestNumber, out var files) ? [.. files] : []);
        }

        public Dictionary<string, List<string>> RemovedLabelsByIssue { get; } = [];

        public Task RemoveIssueLabelsAsync(
            TrackerQuery query,
            string issueId,
            IReadOnlyList<string> labelNames,
            CancellationToken cancellationToken = default)
        {
            if (!RemovedLabelsByIssue.TryGetValue(issueId, out var removed))
            {
                removed = [];
                RemovedLabelsByIssue[issueId] = removed;
            }

            removed.AddRange(labelNames);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OpenPullRequest>> FetchOpenPullRequestsAsync(TrackerQuery query, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpenPullRequest>>(OpenPullRequests);

        public IReadOnlyList<OpenPullRequest> OpenPullRequests { get; set; } = [];


        public Task<string?> MergePullRequestAsync(
            TrackerQuery query,
            int pullRequestNumber,
            string expectedHeadSha,
            string method,
            CancellationToken cancellationToken = default)
        {
            if (MergeRefusal is not null)
            {
                return Task.FromResult<string?>(MergeRefusal);
            }

            MergedPullRequests.Add((pullRequestNumber, expectedHeadSha, method));
            if (PullRequestStatusByNumber.TryGetValue(pullRequestNumber, out var status))
            {
                PullRequestStatusByNumber[pullRequestNumber] = status with { State = "MERGED" };
            }

            return Task.FromResult<string?>(null);
        }

        public Task<PullRequestStatus?> FetchOpenPullRequestByHeadBranchAsync(
            TrackerQuery query,
            string headRefName,
            CancellationToken cancellationToken = default)
        {
            if (OpenPullRequestNumberByHeadBranch.TryGetValue(headRefName, out var number) &&
                PullRequestStatusByNumber.TryGetValue(number, out var status))
            {
                return Task.FromResult<PullRequestStatus?>(status);
            }

            return Task.FromResult<PullRequestStatus?>(null);
        }


        // Models a tracker read that lags a push: the first read of a pull request
        // returns what PullRequestStatusByNumber holds, and every read after it
        // returns the override. Real GitHub does this for a second or two after a
        // force-push, which is exactly when the repair fence looks.
        public Dictionary<int, PullRequestStatus> PullRequestStatusOverridesAfterFirstRead { get; } = [];

        private readonly HashSet<int> readPullRequests = [];

        public Task<PullRequestStatus?> FetchPullRequestStatusAsync(
            TrackerQuery query,
            int pullRequestNumber,
            CancellationToken cancellationToken = default)
        {
            PullRequestStatusRequests.Add(($"{query.Owner}/{query.Repo}", pullRequestNumber));

            if (!readPullRequests.Add(pullRequestNumber) &&
                PullRequestStatusOverridesAfterFirstRead.TryGetValue(pullRequestNumber, out var later))
            {
                return Task.FromResult<PullRequestStatus?>(later);
            }

            return Task.FromResult(
                PullRequestStatusByNumber.TryGetValue(pullRequestNumber, out var status) ? status : null);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan delta) => current = current.Add(delta);
    }

    private sealed class FakeWorkspaceManager : IWorkspaceManager
    {
        public List<WorkspaceCleanupRequest> CleanupRequests { get; } = [];

        public Task<WorkspacePreparationResult> PrepareIssueWorkspaceAsync(WorkspacePreparationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspacePreparationResult($"C:\\tmp\\{request.IssueIdentifier}", request.SuggestedBranchName ?? "branch", CreatedNow: true));

        public Task<WorkspaceCleanupResult> CleanupIssueWorkspaceAsync(WorkspaceCleanupRequest request, CancellationToken cancellationToken = default)
        {
            CleanupRequests.Add(request);
            return Task.FromResult(new WorkspaceCleanupResult($"C:\\tmp\\{request.IssueIdentifier}", Existed: true, RemovedNow: true));
        }
    }

    private enum FakeDispatchOutcome
    {
        LeaveRunning,
        Success,
        Failure
    }

    private sealed class FakeIssueExecutionCoordinator(
        FakeDispatchOutcome outcome,
        bool stopReturnsFalse = false,
        bool observeStopStateWithFreshContext = false) : IIssueExecutionCoordinator
    {
        private SymphonyDbContext? dbContext;
        private string? dbPath;

        public List<IssueExecutionRequest> StartRequests { get; } = [];
        public List<string> StopRequests { get; } = [];
        public (string? RequestedStopReason, bool CleanupWorkspaceOnStop)? ObservedStopState { get; private set; }

        public void Attach(SymphonyDbContext dbContext, string dbPath)
        {
            this.dbContext = dbContext;
            this.dbPath = dbPath;
        }

        public async Task<bool> TryStartAsync(IssueExecutionRequest request, CancellationToken cancellationToken = default)
        {
            StartRequests.Add(request);
            if (dbContext is null || outcome == FakeDispatchOutcome.LeaveRunning)
            {
                return true;
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var run = await dbContext.Runs.SingleAsync(runEntity => runEntity.Id == request.RunId, cancellationToken);
            var attempt = await dbContext.RunAttempts.SingleAsync(attemptEntity => attemptEntity.Id == request.AttemptId, cancellationToken);

            if (outcome == FakeDispatchOutcome.Success)
            {
                // Mirrors IssueExecutionCoordinator: a successful bounded execution is
                // terminal for the dispatch â€” no continuation retry, claim released.
                run.Status = RunStatusNames.Succeeded;
                run.CurrentRetryAttempt = null;
                run.CompletedAtUtc = nowUtc;
                attempt.Status = RunStatusNames.Succeeded;
                attempt.CompletedAtUtc = nowUtc;

                var claim = await dbContext.DispatchClaims.SingleOrDefaultAsync(
                    entity => entity.IssueId == request.Issue.Id && entity.Status == "active",
                    cancellationToken);
                if (claim is not null)
                {
                    claim.Status = RunStatusNames.Succeeded;
                    claim.ReleasedAtUtc = nowUtc;
                    claim.UpdatedAtUtc = nowUtc;
                }
            }
            else
            {
                var retryAttempt = request.Attempt.HasValue ? request.Attempt.Value + 1 : 1;
                run.Status = RunStatusNames.Retrying;
                run.CurrentRetryAttempt = retryAttempt;
                attempt.Status = RunStatusNames.Failed;
                attempt.Error = "simulated failure";
                attempt.CompletedAtUtc = nowUtc;
                dbContext.RetryQueue.Add(new RetryQueueEntity
                {
                    IssueId = request.Issue.Id,
                    IssueIdentifier = request.Issue.Identifier,
                    RunId = request.RunId,
                    OwnerInstanceId = request.InstanceId,
                    Attempt = retryAttempt,
                    DueAtUtc = nowUtc.AddSeconds(10),
                    DelayType = RetryDelayTypes.Backoff,
                    Error = "simulated failure",
                    MaxBackoffMs = request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task<bool> TryStopAsync(string issueId, CancellationToken cancellationToken = default)
        {
            StopRequests.Add(issueId);

            if (observeStopStateWithFreshContext && dbPath is not null)
            {
                var options = new DbContextOptionsBuilder<SymphonyDbContext>()
                    .UseSqlite($"Data Source={dbPath}")
                    .Options;
                await using var freshDbContext = new SymphonyDbContext(options);
                var run = await freshDbContext.Runs.SingleAsync(
                    runEntity => runEntity.IssueId == issueId,
                    cancellationToken);
                ObservedStopState = (run.RequestedStopReason, run.CleanupWorkspaceOnStop);
            }

            return !stopReturnsFalse;
        }
    }
}
