using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

// A phase dispatch the orchestrator asks the tick service to start: a complete
// replacement prompt on a forced runner at a named phase.
public sealed record PhaseDispatchRequest(
    string Phase,
    string RunnerName,
    string Prompt);

public static class PhaseStages
{
    public const string AwaitingVerify = "awaiting_verify";
    public const string AwaitingReview = "awaiting_review";
    public const string Reviewing = "reviewing";
    public const string WaitForRepair = "wait_for_repair";
    public const string Ready = "ready";
    public const string Escalated = "escalated";
    public const string Closed = "closed";
}

// M4 phase orchestration — the routine loop as separate recorded phases:
//
//   implementation succeeds with a linked PR
//     -> VERIFY  (mechanical: CI rollup green at the exact PR head)
//     -> REVIEW  (agent dispatch on the OTHER vendor; verdict posted as an
//                 issue comment with an exact-head marker, parsed by code)
//     -> APPROVED         -> ready (commander merges under the policy gate)
//     -> CHANGES_REQUIRED -> exactly ONE repair dispatch on the implementer;
//                            the PLATFORM-15 fence: the rejected head is
//                            recorded and no further review runs until the PR
//                            head has actually moved (WAIT_FOR_REPAIR)
//     -> second CHANGES_REQUIRED, NEEDS_COMMAND_CENTER, contract violations,
//        CI failure, or a repair that never moves the head -> escalation
//        (surfaced to GitHub by the M1 escalation publisher).
//
// All decisions load the durable phase ledger; a missing or inconsistent ledger
// row fails toward escalation, never toward guessing.
public sealed class PhaseOrchestrator(
    SymphonyDbContext dbContext,
    IGitHubTrackerClient trackerClient,
    TimeProvider timeProvider,
    ILogger<PhaseOrchestrator> logger)
{
    public static string ReviewVerdictMarker(int prNumber, string headSha) =>
        $"<!-- symphony:review-verdict:{prNumber}:{headSha} -->";

    public async Task ProcessPhasesAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        Func<NormalizedIssue, PhaseDispatchRequest, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            await SeedLedgersForCompletedImplementationsAsync(query, cancellationToken);
            await AdvanceLedgersAsync(workflowDefinition, query, dispatchAsync, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Phase orchestration failed this tick; it will retry next tick.");
        }
    }

    private async Task SeedLedgersForCompletedImplementationsAsync(
        TrackerQuery query,
        CancellationToken cancellationToken)
    {
        var succeededImplementations = await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.Succeeded && run.Phase == RunPhaseNames.Implementation)
            .ToListAsync(cancellationToken);
        if (succeededImplementations.Count == 0)
        {
            return;
        }

        var ledgeredIssueIds = new HashSet<string>(
            await dbContext.PhaseLedger.Select(entry => entry.IssueId).ToListAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        foreach (var issueRuns in succeededImplementations.GroupBy(run => run.IssueId, StringComparer.OrdinalIgnoreCase))
        {
            if (ledgeredIssueIds.Contains(issueRuns.Key))
            {
                continue;
            }

            var latestRun = issueRuns.OrderByDescending(run => run.CompletedAtUtc ?? run.StartedAtUtc).First();
            var issues = await trackerClient.FetchIssuesByIdsAsync(query, [issueRuns.Key], cancellationToken);
            var issue = issues.FirstOrDefault();
            if (issue is null || IssueStateMatcher.IsClosedState(issue.State))
            {
                continue;
            }

            var prNumber = await ResolvePullRequestNumberAsync(query, issue, cancellationToken);
            if (prNumber is null)
            {
                // Implementation finished without an open PR; nothing to verify or
                // review. Not an error — e.g. the agent determined no change was
                // needed. Leave the issue to ordinary triage.
                continue;
            }

            var nowUtc = timeProvider.GetUtcNow();
            dbContext.PhaseLedger.Add(new PhaseLedgerEntity
            {
                IssueId = issue.Id,
                IssueIdentifier = issue.Identifier,
                Stage = PhaseStages.AwaitingVerify,
                PrNumber = prNumber.Value,
                ImplementerRunner = AgentRunnerNames.IsKnown(latestRun.Runner) ? latestRun.Runner : AgentRunnerNames.Codex,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });
            AddPhaseEvent(issue.Id, issue.Identifier, "phase_ledger_created",
                $"Implementation durable; PR #{prNumber} enters verify/review phases (implementer: {latestRun.Runner}).");
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    // Finding the PR for a finished implementation, most authoritative first:
    //   1. The branch Symphony itself prepared for the issue (workspace record) —
    //      it created the branch, so an open PR on that head is definitive.
    //   2. GitHub's issue->PR linkage, which only exists when the PR body uses a
    //      closing keyword AND the tracker query includes pull requests
    //      (include_pull_requests). Neither is guaranteed, so it is the fallback.
    private async Task<int?> ResolvePullRequestNumberAsync(
        TrackerQuery query,
        NormalizedIssue issue,
        CancellationToken cancellationToken)
    {
        var workspaceRecord = await dbContext.WorkspaceRecords
            .SingleOrDefaultAsync(record => record.IssueId == issue.Id, cancellationToken);
        var branchName = workspaceRecord?.BranchName ?? issue.BranchName;

        if (!string.IsNullOrWhiteSpace(branchName))
        {
            var byBranch = await trackerClient.FetchOpenPullRequestByHeadBranchAsync(query, branchName, cancellationToken);
            if (byBranch is not null)
            {
                return byBranch.Number;
            }
        }

        return issue.PullRequests
            .Where(pr => pr.Number.HasValue && string.Equals(pr.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pr => pr.Number!.Value)
            .Select(pr => (int?)pr.Number!.Value)
            .FirstOrDefault();
    }

    private async Task AdvanceLedgersAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        Func<NormalizedIssue, PhaseDispatchRequest, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        var activeLedgers = await dbContext.PhaseLedger
            .Where(entry => entry.Stage != PhaseStages.Ready &&
                            entry.Stage != PhaseStages.Escalated &&
                            entry.Stage != PhaseStages.Closed)
            .ToListAsync(cancellationToken);

        foreach (var ledger in activeLedgers)
        {
            try
            {
                await AdvanceOneAsync(workflowDefinition, query, ledger, dispatchAsync, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Phase advance failed for {IssueIdentifier} (stage {Stage}); will retry next tick.",
                    ledger.IssueIdentifier,
                    ledger.Stage);
            }
        }
    }

    private async Task AdvanceOneAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        PhaseLedgerEntity ledger,
        Func<NormalizedIssue, PhaseDispatchRequest, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        var pullRequest = await trackerClient.FetchPullRequestStatusAsync(query, ledger.PrNumber, cancellationToken);
        if (pullRequest is null)
        {
            await EscalateAsync(ledger,
                $"PR #{ledger.PrNumber} could not be loaded from the tracker during phase '{ledger.Stage}'.",
                cancellationToken);
            return;
        }

        if (string.Equals(pullRequest.State, "MERGED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pullRequest.State, "CLOSED", StringComparison.OrdinalIgnoreCase))
        {
            ledger.Stage = PhaseStages.Closed;
            ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
            AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_pr_closed",
                $"PR #{ledger.PrNumber} is {pullRequest.State}; phase tracking closed.");
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        switch (ledger.Stage)
        {
            case PhaseStages.AwaitingVerify:
                await HandleVerifyAsync(ledger, pullRequest, cancellationToken);
                break;
            case PhaseStages.AwaitingReview:
                await HandleDispatchReviewAsync(workflowDefinition, query, ledger, pullRequest, dispatchAsync, cancellationToken);
                break;
            case PhaseStages.Reviewing:
                await HandleReviewVerdictAsync(workflowDefinition, query, ledger, pullRequest, dispatchAsync, cancellationToken);
                break;
            case PhaseStages.WaitForRepair:
                await HandleWaitForRepairAsync(ledger, pullRequest, cancellationToken);
                break;
        }
    }

    private async Task HandleVerifyAsync(
        PhaseLedgerEntity ledger,
        PullRequestStatus pullRequest,
        CancellationToken cancellationToken)
    {
        if (pullRequest.IsDraft)
        {
            return;
        }

        switch (pullRequest.ChecksState?.ToUpperInvariant())
        {
            case "PENDING" or "EXPECTED":
                return; // CI still running; verify re-checks next tick.
            case "SUCCESS":
            case null: // No checks configured — CI gate has nothing to hold.
                ledger.HeadSha = pullRequest.HeadSha;
                ledger.Stage = PhaseStages.AwaitingReview;
                ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
                AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_verify_passed",
                    $"VERIFY passed for PR #{ledger.PrNumber} at head {Short(pullRequest.HeadSha)}" +
                    (pullRequest.ChecksState is null ? " (no CI checks configured)." : " (CI rollup SUCCESS)."));
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            default: // FAILURE / ERROR / anything unknown
                await EscalateAsync(ledger,
                    $"VERIFY failed for PR #{ledger.PrNumber}: CI rollup is {pullRequest.ChecksState} at head {Short(pullRequest.HeadSha)}.",
                    cancellationToken);
                return;
        }
    }

    private async Task HandleDispatchReviewAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        PhaseLedgerEntity ledger,
        PullRequestStatus pullRequest,
        Func<NormalizedIssue, PhaseDispatchRequest, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(pullRequest.HeadSha, ledger.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            // Head moved since verify — re-verify before reviewing (exact-head rule).
            ledger.Stage = PhaseStages.AwaitingVerify;
            ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var runningCount = await dbContext.Runs
            .CountAsync(run => run.Status == RunStatusNames.Running, cancellationToken);
        if (runningCount >= workflowDefinition.Runtime.Agent.MaxConcurrentAgents)
        {
            return; // No slot; retry next tick.
        }

        var issues = await trackerClient.FetchIssuesByIdsAsync(query, [ledger.IssueId], cancellationToken);
        var issue = issues.FirstOrDefault();
        if (issue is null)
        {
            await EscalateAsync(ledger, "The source issue could not be reloaded for review dispatch.", cancellationToken);
            return;
        }

        var reviewerRunner = OtherVendor(ledger.ImplementerRunner);
        var phase = ledger.RepairCount > 0 ? RunPhaseNames.FinalReview : RunPhaseNames.Review;
        var prompt = BuildReviewPrompt(issue, ledger, pullRequest, phase, isFinal: ledger.RepairCount > 0);

        var dispatched = await dispatchAsync(issue, new PhaseDispatchRequest(phase, reviewerRunner, prompt), cancellationToken);
        if (!dispatched)
        {
            return; // Claim refused; retry next tick.
        }

        ledger.Stage = PhaseStages.Reviewing;
        ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
        AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_review_dispatched",
            $"{phase} dispatched to {reviewerRunner} for PR #{ledger.PrNumber} at exact head {Short(ledger.HeadSha)}.");
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleReviewVerdictAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        PhaseLedgerEntity ledger,
        PullRequestStatus pullRequest,
        Func<NormalizedIssue, PhaseDispatchRequest, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        // The verdict is durable GitHub truth: an issue comment carrying the
        // exact-head marker. The review run's local output is not trusted alone.
        var marker = ReviewVerdictMarker(ledger.PrNumber, ledger.HeadSha ?? string.Empty);
        var comments = await trackerClient.FetchIssueCommentsAsync(query, ledger.IssueId, cancellationToken);
        var verdictComment = comments
            .Where(comment => comment.Body.Contains(marker, StringComparison.Ordinal))
            .OrderByDescending(comment => comment.CreatedAtUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (verdictComment is null)
        {
            // No verdict yet. If the review run has terminally failed, escalate;
            // otherwise keep waiting.
            var reviewPhase = ledger.RepairCount > 0 ? RunPhaseNames.FinalReview : RunPhaseNames.Review;
            var reviewRuns = await dbContext.Runs
                .Where(run => run.IssueId == ledger.IssueId && run.Phase == reviewPhase)
                .ToListAsync(cancellationToken);
            var latestReview = reviewRuns
                .OrderByDescending(run => run.StartedAtUtc)
                .FirstOrDefault();
            if (latestReview is null)
            {
                // No review run exists for this stage at all — the dispatch never
                // landed, or its row was taken over by another dispatch. Recover
                // by re-dispatching the review rather than waiting forever.
                ledger.Stage = PhaseStages.AwaitingReview;
                ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
                AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_review_redispatch",
                    $"No {reviewPhase} run found for PR #{ledger.PrNumber}; re-dispatching the review.");
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else if (latestReview.Status is RunStatusNames.Failed or RunStatusNames.TimedOut or RunStatusNames.Stalled)
            {
                await EscalateAsync(ledger,
                    $"The {reviewPhase} run for PR #{ledger.PrNumber} ended '{latestReview.Status}' without posting a verdict comment.",
                    cancellationToken);
            }
            else if (latestReview.Status == RunStatusNames.Succeeded)
            {
                await EscalateAsync(ledger,
                    $"The {reviewPhase} run for PR #{ledger.PrNumber} succeeded but posted no verdict comment with the exact-head marker — contract violation.",
                    cancellationToken);
            }

            return;
        }

        var verdict = ReviewVerdictParser.Parse(verdictComment.Body);
        if (verdict is null)
        {
            await EscalateAsync(ledger,
                $"The review comment for PR #{ledger.PrNumber} carried the marker but no single parseable VERDICT line.",
                cancellationToken);
            return;
        }

        ledger.LastVerdict = verdict;
        ledger.LastVerdictHeadSha = ledger.HeadSha;

        switch (verdict)
        {
            case ReviewVerdicts.Approved:
                ledger.Stage = PhaseStages.Ready;
                ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
                AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_ready",
                    $"Review APPROVED at exact head {Short(ledger.HeadSha)}; PR #{ledger.PrNumber} is ready for the commander merge gate.");
                await dbContext.SaveChangesAsync(cancellationToken);
                await trackerClient.PostIssueCommentAsync(
                    query,
                    ledger.IssueId,
                    $"**READY_FOR_MERGE** — PR #{ledger.PrNumber} approved at exact head `{ledger.HeadSha}` " +
                    $"(implementer: {ledger.ImplementerRunner}, reviewer: {OtherVendor(ledger.ImplementerRunner)}, repairs used: {ledger.RepairCount}). " +
                    "The commander merges under the decision-8 policy gate.",
                    cancellationToken);
                return;

            case ReviewVerdicts.ChangesRequired when ledger.RepairCount == 0:
                await DispatchRepairAsync(workflowDefinition, query, ledger, verdictComment.Body, dispatchAsync, cancellationToken);
                return;

            case ReviewVerdicts.ChangesRequired:
                await EscalateAsync(ledger,
                    $"Second CHANGES_REQUIRED for PR #{ledger.PrNumber} after the single bounded repair — the command center must decide.",
                    cancellationToken);
                return;

            default: // NEEDS_COMMAND_CENTER
                await EscalateAsync(ledger,
                    $"Reviewer returned NEEDS_COMMAND_CENTER for PR #{ledger.PrNumber}.",
                    cancellationToken);
                return;
        }
    }

    private async Task DispatchRepairAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        PhaseLedgerEntity ledger,
        string reviewFindings,
        Func<NormalizedIssue, PhaseDispatchRequest, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        var runningCount = await dbContext.Runs
            .CountAsync(run => run.Status == RunStatusNames.Running, cancellationToken);
        if (runningCount >= workflowDefinition.Runtime.Agent.MaxConcurrentAgents)
        {
            return; // No slot; the verdict comment stays and this retries next tick.
        }

        var issues = await trackerClient.FetchIssuesByIdsAsync(query, [ledger.IssueId], cancellationToken);
        var issue = issues.FirstOrDefault();
        if (issue is null)
        {
            await EscalateAsync(ledger, "The source issue could not be reloaded for the repair dispatch.", cancellationToken);
            return;
        }

        var prompt = BuildRepairPrompt(issue, ledger, reviewFindings);
        var dispatched = await dispatchAsync(
            issue,
            new PhaseDispatchRequest(RunPhaseNames.Implementation, ledger.ImplementerRunner, prompt),
            cancellationToken);
        if (!dispatched)
        {
            return;
        }

        // PLATFORM-15 fence: record the rejected head. Final review cannot run
        // until the PR head has moved past it.
        ledger.RejectedHeadSha = ledger.HeadSha;
        ledger.RepairCount = 1;
        ledger.Stage = PhaseStages.WaitForRepair;
        ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
        AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_repair_dispatched",
            $"CHANGES_REQUIRED at head {Short(ledger.RejectedHeadSha)}; the single bounded repair dispatched to {ledger.ImplementerRunner}. WAIT_FOR_REPAIR until the head moves.");
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleWaitForRepairAsync(
        PhaseLedgerEntity ledger,
        PullRequestStatus pullRequest,
        CancellationToken cancellationToken)
    {
        var repairRuns = await dbContext.Runs
            .Where(run => run.IssueId == ledger.IssueId && run.Phase == RunPhaseNames.Implementation)
            .ToListAsync(cancellationToken);
        var latestRepair = repairRuns.OrderByDescending(run => run.StartedAtUtc).FirstOrDefault();

        if (latestRepair is not null &&
            latestRepair.Status is RunStatusNames.Failed or RunStatusNames.TimedOut or RunStatusNames.Stalled or RunStatusNames.NeedsCommandCenter)
        {
            await EscalateAsync(ledger,
                $"The bounded repair for PR #{ledger.PrNumber} ended '{latestRepair.Status}'.",
                cancellationToken);
            return;
        }

        if (string.Equals(pullRequest.HeadSha, ledger.RejectedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            // Fence holds: the head has not moved past the rejected commit yet.
            if (latestRepair is not null && latestRepair.Status == RunStatusNames.Succeeded)
            {
                await EscalateAsync(ledger,
                    $"The repair run for PR #{ledger.PrNumber} succeeded but the PR head never moved past the rejected commit {Short(ledger.RejectedHeadSha)} — refusing to re-review unchanged rejected code.",
                    cancellationToken);
            }

            return;
        }

        // Head moved: re-verify CI at the new head, then final review follows.
        ledger.Stage = PhaseStages.AwaitingVerify;
        ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
        AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_repair_head_moved",
            $"Repair moved PR #{ledger.PrNumber} head to {Short(pullRequest.HeadSha)}; re-verifying before the final review.");
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EscalateAsync(PhaseLedgerEntity ledger, string reason, CancellationToken cancellationToken)
    {
        ledger.Stage = PhaseStages.Escalated;
        ledger.UpdatedAtUtc = timeProvider.GetUtcNow();

        // Surface through the run-based escalation lane so the M1 publisher posts
        // it to GitHub: mark the newest run for the issue needs_command_center.
        var runs = await dbContext.Runs
            .Where(run => run.IssueId == ledger.IssueId)
            .ToListAsync(cancellationToken);
        var latestRun = runs.OrderByDescending(run => run.StartedAtUtc).FirstOrDefault();
        if (latestRun is not null)
        {
            latestRun.Status = RunStatusNames.NeedsCommandCenter;
            latestRun.CompletedAtUtc ??= timeProvider.GetUtcNow();
            latestRun.LastEvent = "needs_command_center";
            latestRun.LastMessage = $"Phase orchestration: {reason}";
            latestRun.LastEventAtUtc = timeProvider.GetUtcNow();
            latestRun.EscalationPostedAtUtc = null;
        }

        AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "needs_command_center", $"Phase orchestration: {reason}");
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogError("Phase escalation for {IssueIdentifier}: {Reason}", ledger.IssueIdentifier, reason);
    }

    private void AddPhaseEvent(string issueId, string issueIdentifier, string eventName, string message)
    {
        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = issueId,
            IssueIdentifier = issueIdentifier,
            EventName = eventName,
            Level = (eventName == "needs_command_center" ? LogLevel.Error : LogLevel.Information).ToString(),
            Message = message,
            OccurredAtUtc = timeProvider.GetUtcNow()
        });
    }

    private static string OtherVendor(string implementer) =>
        string.Equals(implementer, AgentRunnerNames.Claude, StringComparison.OrdinalIgnoreCase)
            ? AgentRunnerNames.Codex
            : AgentRunnerNames.Claude;

    private static string Short(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? "unknown" : sha.Length > 8 ? sha[..8] : sha;

    private static string BuildReviewPrompt(
        NormalizedIssue issue,
        PhaseLedgerEntity ledger,
        PullRequestStatus pullRequest,
        string phase,
        bool isFinal)
    {
        var marker = ReviewVerdictMarker(ledger.PrNumber, pullRequest.HeadSha);
        return $"""
            You are the INDEPENDENT CROSS-VENDOR REVIEWER for the Autonomous Dev Control Plane.
            You did not write this change; review it adversarially and independently.
            {(isFinal ? "This is the FINAL review after the single bounded repair. A second CHANGES_REQUIRED escalates to the command center automatically — do not soften the verdict because of that." : "")}

            Review target:
            - Source issue: {issue.Identifier} — {issue.Title}
            - Pull request: #{ledger.PrNumber}
            - EXACT head under review: {pullRequest.HeadSha}
            - Phase: {phase}

            Steps:
            1. In this workspace, fetch and check out the exact head: `git fetch origin` then `git checkout {pullRequest.HeadSha}`.
            2. Read the source issue and its comments (gh issue view {IssueNumberFromIdentifier(issue.Identifier)}), the PR diff (gh pr diff {ledger.PrNumber}), AGENTS.md if present, and applicable architecture docs.
            3. Review independently for: issue/spec compliance; correctness; architecture/ADR compliance; security boundaries; regression risk; scope discipline; deterministic verification evidence (run the relevant build/tests yourself if feasible).
            4. Do NOT modify code, do NOT merge, do NOT close anything.

            Verdict contract (MANDATORY, checked by code):
            Post ONE comment on the source issue (gh issue comment {IssueNumberFromIdentifier(issue.Identifier)}) whose body starts with this exact marker line:
            {marker}
            followed by your findings, and containing EXACTLY ONE line of the form:
            VERDICT: APPROVED
            or
            VERDICT: CHANGES_REQUIRED
            or
            VERDICT: NEEDS_COMMAND_CENTER
            Use CHANGES_REQUIRED for fixable defects (list them concretely). Use NEEDS_COMMAND_CENTER only for policy/scope questions no reviewer should decide alone.
            Also print the same VERDICT line as the last line of your final output.
            """;
    }

    private static string BuildRepairPrompt(
        NormalizedIssue issue,
        PhaseLedgerEntity ledger,
        string reviewFindings)
    {
        return $"""
            You are the implementer for the Autonomous Dev Control Plane executing the SINGLE BOUNDED REPAIR cycle.
            The independent review returned CHANGES_REQUIRED for PR #{ledger.PrNumber} (source issue {issue.Identifier}) at head {ledger.HeadSha}.

            Review findings (verbatim):
            ---
            {reviewFindings}
            ---

            Rules:
            1. Fix ONLY the listed findings, within the bounded scope of the source issue. No unrelated changes.
            2. Work on the PR's existing branch in this workspace; run the relevant deterministic build/tests until green.
            3. Commit and push so the PR head MOVES — the final review is fenced and will not run against the old head.
            4. Do NOT merge, do NOT close the issue, do NOT open a new PR.
            5. This is the only repair cycle: a second CHANGES_REQUIRED escalates to the command center automatically.
            """;
    }

    private static string IssueNumberFromIdentifier(string identifier) =>
        identifier.TrimStart('#');
}
