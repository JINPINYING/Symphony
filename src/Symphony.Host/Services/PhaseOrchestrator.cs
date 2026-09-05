using System.Text.Json;
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

    // The bounded repair has been ordered but not yet started. Distinct from
    // `wait_for_repair`, which waits on a repair that IS running: a stage that
    // waits cannot re-ask, and a repair refused for quota has to be asked again
    // once the account's clock is out.
    public const string AwaitingRepair = "awaiting_repair";
    public const string WaitForRepair = "wait_for_repair";
    public const string Ready = "ready";
    public const string Merged = "merged";
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
    /// <summary>
    /// How long a phase may sit without progress before it is reported as stuck.
    ///
    /// Two hours: comfortably past a slow CI run or a long agent turn, and far
    /// short of the days a silently parked issue used to sit for.
    /// </summary>
    public static readonly TimeSpan StuckStageTimeout = TimeSpan.FromHours(2);

    /// <summary>
    /// How long one runner-quota cause may hold a phase before it stops being the
    /// plane's problem and becomes the owner's.
    ///
    /// Six hours: every published usage window resets well inside it, so anything
    /// still holding by then is not a window at all - it is an account with no
    /// credit, and buying credit is an owner decision no directive can make.
    /// </summary>
    public static readonly TimeSpan RunnerQuotaHoldTimeout = TimeSpan.FromHours(6);

    /// <summary>
    /// Recorded on the ledger while a phase is waiting out a runner refusal.
    /// Persisted, so renaming it silently turns every hold into a fresh one.
    /// </summary>
    public const string RunnerQuotaHoldReason = "runner_quota_exhausted";

    /// <summary>The event a quota hold, and its expiry, are recorded under.</summary>
    public const string RunnerQuotaHoldEventName = "phase_runner_quota_hold";

    /// <summary>
    /// The event that says a quota cause has already been raised with the owner.
    ///
    /// One exhausted account blocks every phase at once. On 2026-09-04 that
    /// produced four escalations in fifty minutes for one cause, and a board that
    /// reports one fact four times is a board people stop reading. Every later
    /// ledger blocked by the same runner holds quietly against this row instead of
    /// raising its own, for as long as the runner stays exhausted.
    /// </summary>
    public const string RunnerQuotaEscalatedEventName = "phase_runner_quota_escalated";

    /// <summary>
    /// The event that closes a quota cause: this runner has been seen doing work.
    ///
    /// Suppression keyed on the reset the refusal named expires while the outage
    /// does not - the 2026-09-04 outage spanned two windows, and the second one
    /// would have raised the same dead account a second time. An exhausted runner
    /// stops being an open attention item when it produces work again, and nothing
    /// else is evidence of that, so nothing else clears it.
    /// </summary>
    public const string RunnerQuotaRecoveredEventName = "phase_runner_quota_recovered";

    /// <summary>
    /// The event name a phase escalation and its reason are recorded under.
    ///
    /// Read back by the status page, so it is a persisted value and not just a log
    /// string - renaming it silently leaves the owner-attention panel with a
    /// parked phase and no reason to show for it, which is how that panel came to
    /// print the same invented merge-gate story over every escalation.
    /// </summary>
    public const string EscalationEventName = "needs_command_center";

    /// <summary>
    /// The event an automatic un-park of a LEDGERLESS parked run is recorded under.
    ///
    /// Durable, and load-bearing twice over: it is what the panel shows as the
    /// reason the item went away, and it is the once-per-issue guard. A run that
    /// escalated out of implementation has no pull request behind it, so nothing
    /// external has to change for the sweep to reach the same verdict again -
    /// un-park, re-dispatch, escalate, un-park - which is the loop the phase lane
    /// and the reconciler already fell into once (see
    /// `phase_implementation_no_pull_request`). One automatic attempt per issue is
    /// self-healing; the second time the same issue comes back it is a person's,
    /// and the panel already tells them to post a `symphony:directive`.
    /// </summary>
    public const string ParkedRunReconciledEventName = "phase_parked_run_reconciled";

    /// <summary>
    /// How long a parked run with no ledger row may sit before the sweep clears it
    /// on its own evidence.
    ///
    /// Escalating is a genuine ask of a person, and clearing it on the next tick
    /// would quietly turn every implementation escalation into one more silent
    /// retry - the owner would never see the item at all. The same two hours
    /// everything else in this file waits before something is called stuck: long
    /// enough to be a real ask, short enough that #50's three days on the panel
    /// cannot happen again.
    /// </summary>
    public static readonly TimeSpan ParkedRunReconcileDelay = StuckStageTimeout;

    private static string Humanise(TimeSpan span) =>
        span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} minutes"
        : span.TotalHours < 24 ? $"{(int)span.TotalHours} hours"
        : $"{(int)span.TotalDays} days";

    // Enough to cover every branch an issue could have open at once, without asking
    // the tracker for a page it will never read.
    private const int OpenPullRequestScanLimit = 50;

    public static string ReviewVerdictMarker(int prNumber, string headSha) =>
        $"<!-- symphony:review-verdict:{prNumber}:{headSha} -->";

    // The implementation counterpart of the verdict marker. An implementation may
    // legitimately conclude that nothing needed changing, but "I looked and there
    // was nothing to do" and "I meant to write code and did not" are the same
    // silence, and both used to read as success. This is how the first one says so
    // in durable tracker truth rather than being inferred from an absence.
    public static string NoChangeNeededMarker(string issueId) =>
        $"<!-- symphony:no-change-needed:{issueId} -->";

    public async Task ProcessPhasesAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuerySet queries,
        Func<NormalizedIssue, PhaseDispatchRequest, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            await SeedLedgersForCompletedImplementationsAsync(queries, cancellationToken);
            await AdvanceLedgersAsync(workflowDefinition, queries, dispatchAsync, cancellationToken);
            await ReconcileEscalatedLedgersAsync(workflowDefinition, queries, cancellationToken);
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
        TrackerQuerySet queries,
        CancellationToken cancellationToken)
    {
        var succeededImplementations = await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.Succeeded && run.Phase == RunPhaseNames.Implementation)
            .ToListAsync(cancellationToken);
        if (succeededImplementations.Count == 0)
        {
            return;
        }

        var ledgersByIssue = (await dbContext.PhaseLedger.ToListAsync(cancellationToken))
            .ToDictionary(entry => entry.IssueId, StringComparer.OrdinalIgnoreCase);

        foreach (var issueRuns in succeededImplementations.GroupBy(run => run.IssueId, StringComparer.OrdinalIgnoreCase))
        {
            ledgersByIssue.TryGetValue(issueRuns.Key, out var existingLedger);

            // A ledger that is still working owns the issue; leave it alone.
            //
            // This used to skip any issue that had EVER had a ledger, which
            // silently dropped every second implementation cycle out of the
            // pipeline forever. #115 hit it: PR #122 failed CI and was closed, the
            // issue was reimplemented as PR #127, and because a (terminal) ledger
            // row already existed no new one was seeded - so #127 never reached
            // verify, review or the merge gate, and would have sat open until a
            // person noticed. The owner noticed, by asking whether "waiting on
            // you" really meant them.
            if (existingLedger is not null && !IsTerminalStage(existingLedger.Stage))
            {
                continue;
            }

            var latestRun = issueRuns.OrderByDescending(run => run.CompletedAtUtc ?? run.StartedAtUtc).First();

            // The repository the implementation actually ran in. A pull request
            // number means nothing without it: two repositories can each have a
            // PR #122, and everything after this point asks about one by number.
            var query = queries.For(latestRun.Repository);
            var issues = await trackerClient.FetchIssuesByIdsAsync(
                query,
                [issueRuns.Key],
                IssueIdentifierMap.For(issueRuns.Key, latestRun.IssueIdentifier),
                cancellationToken);
            var issue = issues.FirstOrDefault();

            // A closed issue is a finished one; there is nothing to seed and nothing
            // to say. That branch is silent on purpose.
            if (issue is not null && IssueStateMatcher.IsClosedState(issue.State))
            {
                continue;
            }

            // Not being able to read the issue is different, and it used to share
            // the same silent `continue` as the line above. It is the sibling of the
            // no-pull-request hole: an implementation that finished, a pull request
            // that may well be open, and a ledger that is never seeded - so no
            // verify, no review, no merge, and nothing said.
            //
            // It is recorded rather than escalated because the usual cause is
            // transient (a rate-limited or flaky read that the next tick fixes), and
            // parking work for a person over a blip trains them to ignore parking.
            // The stuck-stage backstop still catches it if it persists. What matters
            // is that it is no longer invisible.
            if (issue is null)
            {
                AddPhaseEvent(issueRuns.Key, latestRun.IssueIdentifier, "phase_seed_issue_unreadable",
                    $"Implementation for {latestRun.IssueIdentifier} succeeded, but the issue could not be read back " +
                    $"from {latestRun.Repository ?? "the primary repository"} to enter it into the phase pipeline. " +
                    "Nothing is tracking its pull request until this read succeeds.");
                await dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            var prNumber = await ResolvePullRequestNumberAsync(query, issue, cancellationToken);
            if (prNumber is null)
            {
                // POSTCONDITION. A phase that reports success has to have produced
                // the thing the next phase consumes; for implementation that is an
                // open pull request.
                //
                // This used to `continue`, on the reasoning that the agent may have
                // decided no change was needed. That reasoning is sound and the
                // silence was not: it made "nothing needed doing" and "the run
                // produced nothing" the same observation. Both left a run reading
                // `succeeded`, no ledger, no event and no escalation - and then the
                // redispatch guard refused to try again, because a pull request was
                // not the thing missing. Work vanished while the plane reported it
                // finished. Three stalls in one hour on 2 September were this.
                //
                // So the benign case now has to say so. An implementation that
                // concludes nothing was needed posts the no-change marker, exactly
                // as a review posts its verdict, and that is a reported outcome.
                // Anything else is a contract violation and is escalated with a
                // reason a person can act on.
                // A settled ledger means the work reached its end by some other
                // route - the pull request was found later, merged, or closed. The
                // missing pull request is then history, not a fault, and escalating
                // it puts the phase lane and the reconciler in a loop.
                if (existingLedger is not null && IsTerminalStage(existingLedger.Stage))
                {
                    continue;
                }

                var comments = await trackerClient.FetchIssueCommentsAsync(query, issue.Id, issue.Identifier, cancellationToken);
                var noChangeMarker = NoChangeNeededMarker(issue.Id);
                var declaredNoChange = comments.Any(
                    comment => comment.Body.Contains(noChangeMarker, StringComparison.Ordinal));

                if (declaredNoChange)
                {
                    AddPhaseEvent(issue.Id, issue.Identifier, "phase_implementation_no_change",
                        "Implementation reported that no change was needed and said so on the issue; " +
                        "there is nothing to verify or review.");
                    await dbContext.SaveChangesAsync(cancellationToken);
                    continue;
                }

                // ONCE. Not once per tick.
                //
                // This escalation and ReconcileEscalatedLedgers were fighting each
                // other in production within an hour of shipping: escalate at :19,
                // "phase_escalation_cleared" at :22, escalate again at :44, around
                // every 25 seconds. Flipping the run off `succeeded` was supposed to
                // make it self-limiting, and it does not - the reconciler resolves
                // the run and the scan finds the next succeeded implementation for
                // the same issue.
                //
                // The dossier that prompted this work warned about exactly this
                // shape: "a rule doing that was shipped and looped within the hour".
                // So the guard is the event log, the same way
                // implementation_redispatch_blocked dedupes itself.
                var alreadyEscalated = await dbContext.EventLog.AnyAsync(
                    entry => entry.IssueId == issue.Id &&
                             entry.EventName == "phase_implementation_no_pull_request",
                    cancellationToken);
                if (alreadyEscalated)
                {
                    continue;
                }

                AddPhaseEvent(issue.Id, issue.Identifier, "phase_implementation_no_pull_request",
                    $"Implementation for {issue.Identifier} reported success and produced no pull request.");

                await EscalateRunAsync(
                    issue.Id,
                    issue.Identifier,
                    $"The implementation run for {issue.Identifier} reported success but produced no pull request, " +
                    "and did not state that no change was needed. Nothing downstream can act on this: there is no " +
                    "head to verify and nothing to review.",
                    cancellationToken);
                continue;
            }

            // No guard on the pull request number here, and the absence is the
            // point.
            //
            // Reaching this line means two things at once: the ledger is terminal
            // (a working one returned above) and ResolvePullRequestNumberAsync
            // found an OPEN pull request, which is the only kind it looks for. A
            // settled ledger naming a pull request that is open again is a reopen,
            // not a duplicate.
            //
            // Skipping when the numbers matched read as "nothing new to enter" and
            // was wrong for exactly that case: PR #135 was closed at 11:57, the
            // ledger recorded closed, the pull request was reopened at 12:11, and
            // nothing would ever have looked at it again. The panel said it had
            // fallen out of the pipeline, which was true and had no way to recover.

            var nowUtc = timeProvider.GetUtcNow();
            var runner = AgentRunnerNames.IsKnown(latestRun.Runner) ? latestRun.Runner : AgentRunnerNames.Codex;

            if (existingLedger is null)
            {
                dbContext.PhaseLedger.Add(new PhaseLedgerEntity
                {
                    IssueId = issue.Id,
                    IssueIdentifier = issue.Identifier,
                    Repository = latestRun.Repository,
                    Stage = PhaseStages.AwaitingVerify,
                    PrNumber = prNumber.Value,
                    ImplementerRunner = runner,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc
                });
                AddPhaseEvent(issue.Id, issue.Identifier, "phase_ledger_created",
                    $"Implementation durable; PR #{prNumber} enters verify/review phases (implementer: {runner}).");
            }
            else
            {
                // The ledger is keyed by issue, so a second cycle resets the row
                // rather than adding one. Every judgement carried by the old row
                // belonged to the OLD pull request and must go with it - a verdict
                // or a rejected head left behind would fence the new PR against a
                // commit from a different branch.
                var settledPrNumber = existingLedger.PrNumber;
                existingLedger.Repository = latestRun.Repository;
                existingLedger.Stage = PhaseStages.AwaitingVerify;
                existingLedger.PrNumber = prNumber.Value;
                existingLedger.ImplementerRunner = runner;
                existingLedger.HeadSha = null;
                existingLedger.LastVerdict = null;
                existingLedger.LastVerdictHeadSha = null;
                existingLedger.RejectedHeadSha = null;
                existingLedger.RepairCount = 0;
                existingLedger.UpdatedAtUtc = nowUtc;
                AddPhaseEvent(issue.Id, issue.Identifier, "phase_ledger_reopened",
                    $"Reimplemented after PR #{settledPrNumber} settled; PR #{prNumber} enters verify/review phases (implementer: {runner}).");
            }

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

            // The branch the plane PREPARED is not always the branch the agent
            // PUSHED. CyberMed #150 prepared `symphony/150`, whose pull request was
            // merged; the second cycle pushed `symphony/150-toolcalls` and opened
            // PR #162. The exact-name lookup found nothing, the ledger never
            // reseeded, and a green pull request sat outside the pipeline with the
            // panel telling the owner to go and repair it by hand.
            //
            // A suffixed branch is still this issue's branch. The separator is
            // required so `symphony/150` cannot claim `symphony/1500`.
            var family = $"{branchName}-";
            var openPullRequests = await trackerClient.FetchOpenPullRequestsAsync(query, OpenPullRequestScanLimit, cancellationToken);
            var byFamily = openPullRequests
                .Where(candidate => candidate.HeadRefName is not null &&
                                    candidate.HeadRefName.StartsWith(family, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.Number)
                .FirstOrDefault();
            if (byFamily is not null)
            {
                AddPhaseEvent(issue.Id, issue.Identifier, "phase_pr_resolved_by_branch_family",
                    $"PR #{byFamily.Number} is on '{byFamily.HeadRefName}', a branch of the '{branchName}' family " +
                    "rather than that exact name; entering it into the pipeline.");
                return byFamily.Number;
            }
        }

        return issue.PullRequests
            .Where(pr => pr.Number.HasValue && string.Equals(pr.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pr => pr.Number!.Value)
            .Select(pr => (int?)pr.Number!.Value)
            .FirstOrDefault();
    }

    // An escalated ledger is deliberately parked: the phase machine must not resume
    // it, because a person or a directive has to resolve it. But "parked" had been
    // implemented as "never looked at again", so once the PR it referred to was
    // merged and its issue closed, the item stayed on the owner's attention panel
    // permanently - #111 was still listed as stopped at the merge gate long after
    // PR #112 was merged. A resolved alarm that never clears does the same damage
    // as a false one: it teaches the reader that the panel is not worth reading
    // (ADCP#22).
    //
    // This asks the terminal question and nothing else - it never re-enters the
    // state machine. The open-PR snapshot the tick already keeps makes it nearly
    // free: a PR still listed as open cannot be terminal, so the common case (a
    // genuinely escalated PR still waiting on a person) costs no call at all. Only
    // a ledger whose PR has dropped out of that list pays for a confirming fetch,
    // and if the snapshot was merely truncated that fetch answers OPEN and nothing
    // happens.
    private async Task ReconcileEscalatedLedgersAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuerySet queries,
        CancellationToken cancellationToken)
    {
        // Repair stranded runs FIRST, and unconditionally.
        //
        // The loop below returns early when no ledger is at stage escalated - and
        // that is precisely the state a stranded run is left in, since its ledger
        // has already moved on to closed or merged. Running the sweep after that
        // return meant it never executed in the only situation it exists for.
        var clearedAgainstSettledLedgers = await ResolveRunsStrandedAgainstSettledLedgersAsync(cancellationToken);

        // And the runs that never had a ledger to be stranded against. Same
        // unconditional placement, for the same reason.
        var clearedWithoutLedgers = await ResolveParkedRunsWithNoLedgerAsync(
            workflowDefinition,
            queries,
            cancellationToken);

        var cleared = clearedAgainstSettledLedgers || clearedWithoutLedgers;

        var escalated = await dbContext.PhaseLedger
            .Where(entry => entry.Stage == PhaseStages.Escalated)
            .ToListAsync(cancellationToken);
        if (escalated.Count == 0)
        {
            if (cleared)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        var openPullRequestNumbers = await ReadOpenPullRequestNumbersAsync(cancellationToken);

        foreach (var ledger in escalated)
        {
            if (openPullRequestNumbers.Contains(ledger.PrNumber))
            {
                continue;
            }

            PullRequestStatus? pullRequest;
            try
            {
                pullRequest = await trackerClient.FetchPullRequestStatusAsync(
                    queries.For(ledger.Repository),
                    ledger.PrNumber,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not check whether the escalation for {IssueIdentifier} (PR #{PrNumber}) is resolved; leaving it up.",
                    ledger.IssueIdentifier,
                    ledger.PrNumber);
                continue;
            }

            // Fail closed. Clearing an alarm we could not verify is worse than
            // leaving one up a little longer.
            if (pullRequest is null || !IsTerminalPullRequestState(pullRequest.State))
            {
                continue;
            }

            var clearedAt = timeProvider.GetUtcNow();
            ledger.Stage = PhaseStages.Closed;
            ledger.UpdatedAtUtc = clearedAt;

            // Clear the RUN too, not just the ledger.
            //
            // This reconciler was written to stop resolved alarms sitting on the
            // owner's panel forever, and it closed the ledger - but the panel is
            // built from `runs`, so the alarm stayed up anyway. On 2026-09-01 the
            // page led with "2 things are waiting on you" for #115 and #118 while
            // its own event stream, further down the same page, said both were
            // "resolved and no longer needs attention". A page that contradicts
            // itself on its most important line is worse than one that is merely
            // late, so the two records are updated together or not at all.
            var strandedRuns = await dbContext.Runs
                .Where(run => run.IssueId == ledger.IssueId
                              && run.Status == RunStatusNames.NeedsCommandCenter)
                .ToListAsync(cancellationToken);
            foreach (var run in strandedRuns)
            {
                run.Status = RunStatusNames.ResolvedByPhaseClear;
                run.CompletedAtUtc = clearedAt;
            }

            AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_escalation_cleared",
                $"PR #{ledger.PrNumber} is {pullRequest.State}, so the merge-gate escalation for {ledger.IssueIdentifier} is resolved and no longer needs attention."
                + (strandedRuns.Count > 0
                    ? $" Also resolved {strandedRuns.Count} stranded run{(strandedRuns.Count == 1 ? string.Empty : "s")}."
                    : string.Empty));
            cleared = true;
        }

        if (cleared)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Resolves runs left at needs_command_center whose phase has ALREADY settled.
    ///
    /// Clearing the run at the moment the ledger closes fixes every future case
    /// and no past one: this loop only visits ledgers still at stage escalated, so
    /// anything that diverged before that code existed stays stranded forever.
    /// #115 and #118 did exactly that - the panel stopped showing them because the
    /// summary suppresses settled issues, but the runs stayed needs_command_center
    /// in the database, and the commander's sweep reads the runs rather than the
    /// panel. A fix that corrects the page while leaving the record wrong just
    /// moves which surface is lying.
    ///
    /// Written as a sweep rather than a migration so it is self-healing: any
    /// future divergence, from any cause, is repaired on the next tick instead of
    /// waiting for someone to notice it.
    /// </summary>
    private async Task<bool> ResolveRunsStrandedAgainstSettledLedgersAsync(
        CancellationToken cancellationToken)
    {
        var stranded = await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.NeedsCommandCenter)
            .ToListAsync(cancellationToken);
        if (stranded.Count == 0)
        {
            return false;
        }

        var settledIssueIds = await dbContext.PhaseLedger
            .Where(entry => entry.Stage == PhaseStages.Closed || entry.Stage == PhaseStages.Merged)
            .Select(entry => entry.IssueId)
            .ToListAsync(cancellationToken);
        if (settledIssueIds.Count == 0)
        {
            return false;
        }

        var settled = settledIssueIds.ToHashSet(StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        var repaired = 0;

        foreach (var run in stranded.Where(run => settled.Contains(run.IssueId)))
        {
            run.Status = RunStatusNames.ResolvedByPhaseClear;
            run.CompletedAtUtc = now;
            repaired++;
            AddPhaseEvent(run.IssueId, run.IssueIdentifier, "phase_escalation_cleared",
                $"{run.IssueIdentifier} was still recorded as needing the command center while its phase had already settled. Resolved to match.");
        }

        return repaired > 0;
    }

    /// <summary>
    /// Resolves runs parked at needs_command_center whose issue has NO ledger row
    /// at all.
    ///
    /// The sweep above repairs a run left behind by a ledger that moved on, so it
    /// starts from the set of settled ledgers - and an issue with no ledger row is
    /// never in that set. A run that escalated out of IMPLEMENTATION escalated
    /// before any pull request existed, so no ledger was ever created for it, and
    /// every other route out is closed too: the label cannot re-dispatch an issue
    /// that already holds a live run, and restart reconciliation treats
    /// needs_command_center as a deliberate park rather than a crash artifact. The
    /// documented escape hatch is a directive, which leaves one hand-typed comment
    /// as the only thing standing between the plane and a permanent attention item.
    /// Symphony #50 sat on the owner's panel for three days, through several
    /// restarts, because of exactly this (#87).
    ///
    /// So a ledgerless park is settled on the same evidence a ledgered one is: the
    /// tracker's own answer. A closed issue is finished whatever the run says. An
    /// open issue with no pull request of its own has nothing left to wait for
    /// either - there is no PR to close, which is the manoeuvre that clears every
    /// other parked run - so the run is resolved and the issue becomes dispatchable
    /// from its label again.
    ///
    /// Fail closed throughout: a tracker read that does not answer leaves the item
    /// up. Clearing an alarm we could not verify is worse than leaving one up a
    /// little longer.
    /// </summary>
    private async Task<bool> ResolveParkedRunsWithNoLedgerAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuerySet queries,
        CancellationToken cancellationToken)
    {
        var parked = await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.NeedsCommandCenter)
            .ToListAsync(cancellationToken);
        if (parked.Count == 0)
        {
            return false;
        }

        // ANY ledger row, at any stage - not just a settled one. A live ledger
        // means the phase machine owns the issue and an escalated one is the
        // reconciler's above; this sweep is only for issues neither of them can
        // see.
        var ledgeredIssueIds = (await dbContext.PhaseLedger
                .Select(entry => entry.IssueId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var now = timeProvider.GetUtcNow();
        var candidates = parked
            .Where(run => !ledgeredIssueIds.Contains(run.IssueId))
            .Where(run => now - ParkedSince(run) >= ParkedRunReconcileDelay)
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        // One automatic un-park per issue, guarded on the event log the same way
        // the implementation lane's escalation is. Nothing external has to change
        // for this sweep to reach the same verdict on the next cycle, so without
        // the guard an issue that escalates for a durable reason would be
        // un-parked, re-dispatched and escalated again for ever.
        var candidateIssueIds = candidates
            .Select(run => run.IssueId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var alreadyUnparked = (await dbContext.EventLog
                .Where(entry => entry.EventName == ParkedRunReconciledEventName &&
                                candidateIssueIds.Contains(entry.IssueId))
                .Select(entry => entry.IssueId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        candidates = candidates
            .Where(run => !alreadyUnparked.Contains(run.IssueId))
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        var branchByIssueId = (await dbContext.WorkspaceRecords
                .Where(record => candidateIssueIds.Contains(record.IssueId))
                .Select(record => new { record.IssueId, record.BranchName })
                .ToListAsync(cancellationToken))
            .Where(record => !string.IsNullOrWhiteSpace(record.BranchName))
            .ToDictionary(record => record.IssueId, record => record.BranchName!, StringComparer.Ordinal);

        var terminalStates = workflowDefinition.Runtime.Tracker.TerminalStates;
        var repaired = false;

        foreach (var perRepository in candidates.GroupBy(run => run.Repository ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var query = queries.For(perRepository.Key);
            var runs = perRepository.ToList();

            IReadOnlyList<IssueStateSnapshot> snapshots;
            IReadOnlyList<OpenPullRequest> openPullRequests;
            try
            {
                snapshots = await trackerClient.FetchIssueStatesByIdsAsync(
                    query,
                    runs.Select(run => run.IssueId).Distinct(StringComparer.Ordinal).ToList(),
                    IssueIdentifierMap.From(runs, run => run.IssueId, run => run.IssueIdentifier),
                    cancellationToken);

                // Scoped to this repository by the query, so a branch match needs no
                // further repository test: "#45" exists in every repository the
                // plane watches, and so does `symphony/45`.
                openPullRequests = await trackerClient.FetchOpenPullRequestsAsync(
                    query,
                    OpenPullRequestScanLimit,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not check whether the parked runs on {Repository} still need the command center; leaving them up.",
                    TrackerQuerySet.KeyOf(query));
                continue;
            }

            var stateByIssueId = snapshots.ToDictionary(
                snapshot => snapshot.Id,
                StringComparer.OrdinalIgnoreCase);

            foreach (var run in runs)
            {
                if (!stateByIssueId.TryGetValue(run.IssueId, out var snapshot))
                {
                    // Asked and not answered. "The tracker did not mention it" is
                    // not evidence that the work is finished.
                    continue;
                }

                string reason;
                if (MatchesTerminalState(snapshot.State, terminalStates))
                {
                    reason =
                        $"{run.IssueIdentifier} is {snapshot.State} on the tracker and has no phase ledger row, " +
                        "so nothing is left for the command center to decide. Resolved the parked run.";
                }
                else
                {
                    branchByIssueId.TryGetValue(run.IssueId, out var branchName);
                    var openPullRequest = FindOpenPullRequestForIssue(openPullRequests, branchName, run.IssueIdentifier);
                    if (openPullRequest is not null)
                    {
                        // There IS something to close, which is the route that
                        // already works. Leave it to the person or to the escalated
                        // ledger reconciler once a ledger exists.
                        continue;
                    }

                    if (branchName is null && !TryReadIssueNumber(run.IssueIdentifier, out _))
                    {
                        // No branch of its own and no issue number to derive one
                        // from: this run cannot be told apart from one whose pull
                        // request the scan simply did not list. Fail closed.
                        continue;
                    }

                    reason =
                        $"{run.IssueIdentifier} has been parked for the command center for " +
                        $"{Humanise(now - ParkedSince(run))} with no phase ledger row and no open pull request, " +
                        "so no action available to anyone could have cleared it. Resolved the parked run; the " +
                        "issue is dispatchable from its label again. A second escalation on this issue will stay " +
                        "parked for a person.";
                }

                run.Status = RunStatusNames.ResolvedByPhaseClear;
                run.CompletedAtUtc = now;
                AddPhaseEvent(run.IssueId, run.IssueIdentifier, ParkedRunReconciledEventName, reason);
                repaired = true;
            }
        }

        return repaired;
    }

    // When the run stopped, as well as it can be known. CompletedAtUtc is set on
    // every escalation path; the other two are fallbacks for a row written before
    // it was, and StartedAtUtc always exists.
    private static DateTimeOffset ParkedSince(RunEntity run) =>
        run.CompletedAtUtc ?? run.LastEventAtUtc ?? run.StartedAtUtc;

    /// <summary>
    /// The open pull request belonging to this issue, if the repository has one.
    ///
    /// Same two rules ResolvePullRequestNumberAsync uses: the branch the plane
    /// prepared for the issue, and any branch of that family ("symphony/150" also
    /// owns "symphony/150-toolcalls"). Without a workspace record the branch is
    /// derived from the issue number, which is the convention the plane names
    /// branches by and the same one the attention panel matches on.
    /// </summary>
    private static OpenPullRequest? FindOpenPullRequestForIssue(
        IReadOnlyList<OpenPullRequest> openPullRequests,
        string? branchName,
        string issueIdentifier)
    {
        var families = new List<string>();
        if (!string.IsNullOrWhiteSpace(branchName))
        {
            families.Add(branchName);
        }

        if (TryReadIssueNumber(issueIdentifier, out var number))
        {
            families.Add($"symphony/{number}");
        }

        if (families.Count == 0)
        {
            return null;
        }

        return openPullRequests.FirstOrDefault(pullRequest =>
            pullRequest.HeadRefName is not null &&
            families.Any(family =>
                pullRequest.HeadRefName.Equals(family, StringComparison.OrdinalIgnoreCase) ||
                // The separator is required so `symphony/15` cannot claim
                // `symphony/150`.
                pullRequest.HeadRefName.StartsWith($"{family}-", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryReadIssueNumber(string issueIdentifier, out string number)
    {
        number = (issueIdentifier ?? string.Empty).Trim().TrimStart('#');
        if (number.Length == 0 || !number.All(char.IsDigit))
        {
            number = string.Empty;
            return false;
        }

        return true;
    }

    // The tracker's terminal states as configured, with the same fallback the
    // dispatch lane uses when none are configured.
    private static bool MatchesTerminalState(string state, IReadOnlyList<string> terminalStates)
    {
        if (terminalStates.Count == 0)
        {
            return IssueStateMatcher.IsClosedState(state);
        }

        return terminalStates.Any(terminalState =>
            terminalState.Trim().Equals(state.Trim(), StringComparison.OrdinalIgnoreCase) ||
            (IssueStateMatcher.IsClosedState(terminalState) && IssueStateMatcher.IsClosedState(state)));
    }

    private async Task<HashSet<int>> ReadOpenPullRequestNumbersAsync(CancellationToken cancellationToken)
    {
        var json = (await dbContext.EventLog
                .AsNoTracking()
                .Where(entry => entry.EventName == OrchestrationTickService.OpenPullRequestsEventName && entry.DataJson != null)
                .ToListAsync(cancellationToken))
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Select(entry => entry.DataJson)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var openPullRequests = JsonSerializer.Deserialize<List<OpenPullRequest>>(json);
            return openPullRequests is null ? [] : [.. openPullRequests.Select(pr => pr.Number)];
        }
        catch (JsonException)
        {
            // No snapshot means no shortcut, not a wrong answer: every escalated
            // ledger simply pays for its own confirming fetch this tick.
            return [];
        }
    }

    // A ledger at one of these stages has finished with its pull request. It is
    // history, not work in progress, so it must not block a later cycle.
    private static bool IsTerminalStage(string? stage) =>
        string.Equals(stage, PhaseStages.Closed, StringComparison.Ordinal) ||
        string.Equals(stage, PhaseStages.Merged, StringComparison.Ordinal);

    private static bool IsTerminalPullRequestState(string? state) =>
        string.Equals(state, "MERGED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase);

    private async Task AdvanceLedgersAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuerySet queries,
        Func<NormalizedIssue, PhaseDispatchRequest, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        var activeLedgers = await dbContext.PhaseLedger
            .Where(entry => entry.Stage != PhaseStages.Merged &&
                            entry.Stage != PhaseStages.Escalated &&
                            entry.Stage != PhaseStages.Closed)
            .ToListAsync(cancellationToken);

        foreach (var ledger in activeLedgers)
        {
            try
            {
                await AdvanceOneAsync(
                    workflowDefinition,
                    queries.For(ledger.Repository),
                    ledger,
                    dispatchAsync,
                    cancellationToken);
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

        if (IsTerminalPullRequestState(pullRequest.State))
        {
            ledger.Stage = PhaseStages.Closed;
            ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
            AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_pr_closed",
                $"PR #{ledger.PrNumber} is {pullRequest.State}; phase tracking closed.");
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        // A phase that is deliberately waiting out a transient runner refusal is not
        // stuck and must not be read as such, so this runs before the backstop
        // below. ADCP#29: an exhausted vendor account is a clock, not a decision.
        if (ledger.HoldUntilUtc is not null &&
            !await ReleaseRunnerHoldIfDueAsync(ledger, cancellationToken))
        {
            return;
        }

        // Backstop: a stage that has not moved in a long time is stuck, whatever it
        // believes it is waiting for.
        //
        // Every fault found in this file so far has been the same shape - a state
        // the code did not recognise as terminal, so it waited on it forever, in
        // silence. Enumerating statuses fixes the ones we have already met and does
        // nothing about the next one. The stages legitimately wait on several
        // things that can hang and none of which is bounded: a pull request left as
        // a draft, a CI check that never reports, a claim that keeps being refused,
        // a slot that never frees. This catches all of them, and the ones nobody
        // has thought of, by noticing the absence of progress rather than the
        // presence of a known failure.
        //
        // Deliberately generous, because the cost of being wrong is asymmetric: too
        // short escalates work that was progressing normally and teaches the owner
        // to ignore escalations, while too long only delays a report about work
        // that has already stopped.
        var stuckFor = timeProvider.GetUtcNow() - ledger.UpdatedAtUtc;
        if (stuckFor > StuckStageTimeout)
        {
            await EscalateAsync(ledger,
                $"PR #{ledger.PrNumber} has been at phase '{ledger.Stage}' for {Humanise(stuckFor)} with no progress. " +
                $"Its pull request is {(pullRequest.IsDraft ? "a draft" : "open")} with CI {pullRequest.ChecksState ?? "not configured"}. " +
                "Nothing in the phase machine is going to move it on its own.",
                cancellationToken);
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
            case PhaseStages.AwaitingRepair:
                await HandleDispatchRepairAsync(workflowDefinition, query, ledger, pullRequest, dispatchAsync, cancellationToken);
                break;
            case PhaseStages.WaitForRepair:
                await HandleWaitForRepairAsync(query, ledger, pullRequest, cancellationToken);
                break;
            case PhaseStages.Ready:
                await HandleReadyAsync(workflowDefinition, query, ledger, pullRequest, cancellationToken);
                break;
        }
    }

    // M6: an approved PR does not sit waiting for a human. The policy gate is
    // evaluated in code and, if every condition holds, the merge happens on the
    // next tick — seconds after approval rather than at the next commander slot.
    private async Task HandleReadyAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        PhaseLedgerEntity ledger,
        PullRequestStatus pullRequest,
        CancellationToken cancellationToken)
    {
        var policy = workflowDefinition.Runtime.MergePolicy;
        if (!policy.Enabled)
        {
            return; // Ready and waiting for a human; nothing to do, nothing to say.
        }

        if (!string.Equals(ledger.LastVerdictHeadSha, pullRequest.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            // The branch moved after approval: re-verify and review the new head.
            ledger.Stage = PhaseStages.AwaitingVerify;
            ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
            AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_head_moved_after_approval",
                $"PR #{ledger.PrNumber} moved past the approved head; re-verifying before it can merge.");
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var changedPaths = await trackerClient.FetchPullRequestFilesAsync(query, ledger.PrNumber, cancellationToken);
        var gate = MergePolicyGate.Evaluate(policy, ledger, pullRequest, changedPaths);
        if (!gate.Allowed)
        {
            if (gate.Escalate)
            {
                await EscalateAsync(ledger, $"Merge gate refused PR #{ledger.PrNumber}: {gate.Reason}.", cancellationToken);
            }

            return;
        }

        var refusal = await trackerClient.MergePullRequestAsync(
            query,
            ledger.PrNumber,
            pullRequest.HeadSha,
            policy.Method,
            cancellationToken);

        if (refusal is not null)
        {
            // GitHub refused (expected-head mismatch, branch protection, ...).
            // Blueprint: treat any refusal as an escalation, never a retry loop.
            await EscalateAsync(ledger, $"GitHub refused the merge of PR #{ledger.PrNumber}: {refusal}", cancellationToken);
            return;
        }

        ledger.Stage = PhaseStages.Merged;
        ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
        AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_merged",
            $"PR #{ledger.PrNumber} merged autonomously at head {Short(pullRequest.HeadSha)} ({gate.Reason}).");
        await dbContext.SaveChangesAsync(cancellationToken);

        await trackerClient.PostIssueCommentAsync(
            query,
            ledger.IssueId,
            $"**MERGED** — PR #{ledger.PrNumber} merged at exact head `{pullRequest.HeadSha}` under the routine merge policy.\n\n" +
            $"Implementer: `{ledger.ImplementerRunner}` · reviewer: `{OtherVendor(ledger.ImplementerRunner)}` · " +
            $"repairs used: {ledger.RepairCount} · files changed: {changedPaths.Count} · gate: {gate.Reason}.\n\n" +
            "Execution labels have been removed, so this issue will not be dispatched again. " +
            "It is left open for the command center to close.",
            cancellationToken);

        // Clear the execution label LAST, after the terminal comment is posted -
        // the workflow contract requires that order. Until this ran, a merged
        // issue still matched the candidate query and was re-dispatched on the
        // next tick, burning a full agent run before reconciliation cancelled it.
        //
        // Failure here must not propagate. The merge has already happened and the
        // stage is already Merged, so an exception would abort the tick without
        // ever getting another chance at the label - trading a spent agent run for
        // a failed tick and the same stale label. Log it loudly instead: the worst
        // case is the original bug, on this one issue, visibly recorded.
        try
        {
            await trackerClient.RemoveIssueLabelsAsync(
                query,
                ledger.IssueId,
                workflowDefinition.Runtime.Tracker.Labels,
                cancellationToken);

            logger.LogInformation(
                "Merged PR #{PrNumber} for {IssueIdentifier} under the routine merge policy and cleared its execution labels.",
                ledger.PrNumber,
                ledger.IssueIdentifier);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Merged PR #{PrNumber} for {IssueIdentifier}, but failed to clear its execution labels. "
                + "The issue will be re-dispatched until the label is removed by hand.",
                ledger.PrNumber,
                ledger.IssueIdentifier);
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

        var issues = await trackerClient.FetchIssuesByIdsAsync(
            query,
            [ledger.IssueId],
            IssueIdentifierMap.For(ledger.IssueId, ledger.IssueIdentifier),
            cancellationToken);
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
        var comments = await trackerClient.FetchIssueCommentsAsync(query, ledger.IssueId, ledger.IssueIdentifier, cancellationToken);
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
            else if (IsRunnerQuotaRefusal(latestReview))
            {
                // ADCP#29. Nobody violated a contract and nothing needs repairing:
                // the reviewer's account was out of quota and the run never
                // started. That clears on a clock, so the phase waits for the clock
                // rather than asking a person for a decision that does not exist.
                await HoldForRunnerQuotaAsync(
                    ledger,
                    latestReview,
                    $"the {reviewPhase} of PR #{ledger.PrNumber}",
                    cancellationToken);
            }
            else if (latestReview.Status is RunStatusNames.Running or RunStatusNames.Retrying)
            {
                // Genuinely still working. This is the only reason to keep waiting.
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
            else
            {
                // The run is over and never produced a verdict, but the review
                // itself did not fail - it was cancelled out from under the phase,
                // most often by restart reconciliation. That is the engine's doing,
                // not the reviewer's, so the recovery is to ask again rather than
                // to escalate a reviewer that was never given its turn.
                //
                // Waiting was the previous behaviour, and it was silent and
                // permanent: every terminal status outside the three named above
                // fell through to "keep waiting", so #128 sat at `reviewing` with a
                // canceled_by_reconciliation review run while the page correctly
                // showed Codex idle. The owner spotted the contradiction - the
                // pipeline said reviewing, the reviewer was doing nothing.
                ledger.Stage = PhaseStages.AwaitingReview;
                ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
                AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_review_redispatch",
                    $"The {reviewPhase} run for PR #{ledger.PrNumber} ended '{latestReview.Status}' before producing a verdict; re-dispatching the review.");
                await dbContext.SaveChangesAsync(cancellationToken);
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

        // A verdict is the phase moving, so whatever it was waiting out is over.
        // Cleared here and only here: a dispatch that merely started again proves
        // nothing, because the next run can refuse on the same empty account.
        ClearRunnerHold(ledger);

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

    /// <summary>
    /// Ask for the bounded repair again, from a stage whose whole job is asking.
    /// </summary>
    /// <remarks>
    /// A repair refused for quota used to hold at `wait_for_repair`, and that stage
    /// waits on the latest repair run rather than starting one - so when the hold
    /// expired the switch came straight back to the same dead run and renewed the
    /// hold. The bounded repair waited out one window, then the next, until the
    /// six-hour bound escalated it: the exact outcome the hold exists to prevent.
    ///
    /// The review path never had this fault because it rewinds to
    /// `awaiting_review`, a stage that dispatches unconditionally. This is that
    /// same shape for the repair.
    ///
    /// The findings are re-read from GitHub rather than carried on the ledger: the
    /// verdict comment is the durable record of what the repair was asked to do,
    /// and a copy of it in a database column is one more thing that can disagree
    /// with the source of truth.
    /// </remarks>
    private async Task HandleDispatchRepairAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        PhaseLedgerEntity ledger,
        PullRequestStatus pullRequest,
        Func<NormalizedIssue, PhaseDispatchRequest, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        // Nothing to ask for if it has already been done. A repair only reaches
        // this stage without having run, but the head can still move while it waits
        // - a hold can span a night, and someone can push during one.
        //
        // The hold goes, because there is no longer anything to hold for; the
        // runner is NOT recorded as recovered, because it is precisely the case
        // where the held runner did not run. A push from outside the plane says
        // nothing about whether its account has credit again.
        if (!string.IsNullOrWhiteSpace(ledger.RejectedHeadSha) &&
            !string.Equals(pullRequest.HeadSha, ledger.RejectedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            ledger.Stage = PhaseStages.AwaitingVerify;
            DropRunnerHold(ledger);
            ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
            AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_repair_head_moved",
                $"PR #{ledger.PrNumber} moved to head {Short(pullRequest.HeadSha)} before its repair was re-dispatched; " +
                "re-verifying instead of asking again for work that has landed.");
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var rejectedHead = string.IsNullOrWhiteSpace(ledger.RejectedHeadSha)
            ? ledger.HeadSha ?? string.Empty
            : ledger.RejectedHeadSha;
        var marker = ReviewVerdictMarker(ledger.PrNumber, rejectedHead);
        var comments = await trackerClient.FetchIssueCommentsAsync(query, ledger.IssueId, ledger.IssueIdentifier, cancellationToken);
        var verdictComment = comments
            .Where(comment => comment.Body.Contains(marker, StringComparison.Ordinal))
            .OrderByDescending(comment => comment.CreatedAtUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (verdictComment is null)
        {
            // The findings the repair is supposed to act on are gone. Dispatching
            // an unbounded "fix it" in their place is worse than saying so.
            await EscalateAsync(ledger,
                $"PR #{ledger.PrNumber} is waiting to re-dispatch its bounded repair, but the review comment " +
                $"carrying the exact-head marker for {Short(rejectedHead)} is no longer on the issue, so there are " +
                "no findings to repair against.",
                cancellationToken);
            return;
        }

        await DispatchRepairAsync(workflowDefinition, query, ledger, verdictComment.Body, dispatchAsync, cancellationToken);
    }

    private async Task DispatchRepairAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        PhaseLedgerEntity ledger,
        string reviewFindings,
        Func<NormalizedIssue, PhaseDispatchRequest, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        // A repair that cannot start says so.
        //
        // Both of the returns below used to be silent, and silence is the whole
        // complaint in #51: the verdict landed, no repair run was ever started, and
        // the only place that fact existed was the absence of a row. The plane
        // reported nothing running and the dashboard showed everything idle, so an
        // operator had to read run records in SQLite to find out that a repair was
        // being attempted and refused every tick.
        //
        // Neither case is a fault on its own - a busy plane and a contended claim
        // both resolve themselves, and the stage backstop already bounds them - so
        // this reports rather than escalates, once per stage entry per cause.
        var runningCount = await dbContext.Runs
            .CountAsync(run => run.Status == RunStatusNames.Running, cancellationToken);
        if (runningCount >= workflowDefinition.Runtime.Agent.MaxConcurrentAgents)
        {
            await ReportRepairDeferredAsync(
                ledger,
                $"no agent slot is free ({runningCount} running, limit {workflowDefinition.Runtime.Agent.MaxConcurrentAgents})",
                cancellationToken);
            return; // No slot; the verdict comment stays and this retries next tick.
        }

        var issues = await trackerClient.FetchIssuesByIdsAsync(
            query,
            [ledger.IssueId],
            IssueIdentifierMap.For(ledger.IssueId, ledger.IssueIdentifier),
            cancellationToken);
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
            await ReportRepairDeferredAsync(ledger, "the dispatch claim was refused", cancellationToken);
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

    // Reported once per stage entry per cause. `UpdatedAtUtc` is the stamp the
    // ledger took when it entered this stage and is not touched while the repair is
    // deferred, so it is exactly the window one report should cover: a plane that
    // is busy for six ticks says so once, and says so again if the cause changes or
    // the stage is re-entered.
    private async Task ReportRepairDeferredAsync(
        PhaseLedgerEntity ledger,
        string cause,
        CancellationToken cancellationToken)
    {
        var message =
            $"CHANGES_REQUIRED is recorded for PR #{ledger.PrNumber} at head {Short(ledger.HeadSha)}, but the bounded " +
            $"repair could not be dispatched: {cause}. It is retried every tick.";

        // Filtered in memory: SQLite does not compare DateTimeOffset reliably.
        var reported = await dbContext.EventLog
            .Where(entry => entry.IssueId == ledger.IssueId && entry.EventName == "phase_repair_deferred")
            .ToListAsync(cancellationToken);
        if (reported.Any(entry => entry.OccurredAtUtc >= ledger.UpdatedAtUtc &&
                                  string.Equals(entry.Message, message, StringComparison.Ordinal)))
        {
            return;
        }

        AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_repair_deferred", message);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Repair dispatch deferred for {IssueIdentifier}: {Cause}",
            ledger.IssueIdentifier,
            cause);
    }

    private async Task HandleWaitForRepairAsync(
        TrackerQuery query,
        PhaseLedgerEntity ledger,
        PullRequestStatus pullRequest,
        CancellationToken cancellationToken)
    {
        var repairRuns = await dbContext.Runs
            .Where(run => run.IssueId == ledger.IssueId && run.Phase == RunPhaseNames.Implementation)
            .ToListAsync(cancellationToken);
        var latestRepair = repairRuns.OrderByDescending(run => run.StartedAtUtc).FirstOrDefault();

        // ADCP#29, same rule as the reviewing stage: a repair that never ran
        // because the implementer's account was out of quota is a wait, not a
        // failure, and the single bounded repair has not been spent on it.
        //
        // The hold is taken here even if the head has since moved by some other
        // route: `awaiting_repair` checks that before it asks for anything, and
        // paying one quota window to reach a check that already exists is cheaper
        // than a second head test on the escalating side of this branch.
        if (latestRepair is not null && IsRunnerQuotaRefusal(latestRepair))
        {
            await HoldForRunnerQuotaAsync(
                ledger,
                latestRepair,
                $"the bounded repair of PR #{ledger.PrNumber}",
                cancellationToken);
            return;
        }

        if (latestRepair is not null &&
            latestRepair.Status is RunStatusNames.Failed or RunStatusNames.TimedOut or RunStatusNames.Stalled or RunStatusNames.NeedsCommandCenter)
        {
            await EscalateAsync(ledger,
                $"The bounded repair for PR #{ledger.PrNumber} ended '{latestRepair.Status}'.",
                cancellationToken);
            return;
        }

        // The same hole the reviewing stage had, in its sibling. A repair that
        // ended without failing - cancelled by restart reconciliation, most often -
        // is over and is never coming back, but it matched none of the statuses
        // above and fell through to "keep waiting". So did the case of no repair
        // run existing at all, which the reviewing stage recovers from by
        // re-dispatching and this one did not.
        //
        // Re-dispatching means returning the ledger to the review that ordered the
        // repair: it is what asks for the work again.
        var repairIsOver = latestRepair is not null &&
            latestRepair.Status is not (RunStatusNames.Running or RunStatusNames.Retrying or RunStatusNames.Succeeded);
        if (latestRepair is null || repairIsOver)
        {
            ledger.Stage = PhaseStages.AwaitingReview;
            ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
            AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_repair_redispatch",
                latestRepair is null
                    ? $"No repair run exists for PR #{ledger.PrNumber}; returning to review rather than waiting on one that was never dispatched."
                    : $"The repair for PR #{ledger.PrNumber} ended '{latestRepair.Status}' without moving the head; returning to review rather than waiting on a run that is over.");
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        // A fence that cannot be evaluated does not pass.
        //
        // The comparison below is string.Equals, and string.Equals(head, null) is
        // false - so a ledger carrying no rejected head read as "the head moved"
        // and waved the repair straight through onto unchanged rejected code, which
        // is the one thing this fence exists to prevent. The row is cleared on
        // reseed and can legitimately be null, so this is reachable without anyone
        // doing anything wrong.
        if (string.IsNullOrWhiteSpace(ledger.RejectedHeadSha))
        {
            await EscalateAsync(ledger,
                $"PR #{ledger.PrNumber} is waiting on a repair but the ledger records no rejected head, " +
                "so there is nothing to check the new head against. Refusing to assume the repair landed.",
                cancellationToken);
            return;
        }

        if (string.Equals(pullRequest.HeadSha, ledger.RejectedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            // Fence holds: the head has not moved past the rejected commit yet.
            if (latestRepair is not null && latestRepair.Status == RunStatusNames.Succeeded)
            {
                // #28: the plane escalated a pull request for not moving past a
                // commit it had already moved past - rejected 1b1db81, actual head
                // 0d99f83. One read decided it, and a read taken moments after a
                // push can still be serving the old head.
                //
                // The costs are not symmetric. Waiting another tick on a repair
                // that really did nothing delays a report; escalating a repair that
                // worked parks live work for a person and is not self-correcting.
                // So the last read before saying so is taken fresh, and if it
                // disagrees with the one this tick opened with, the head moved.
                var confirmed = await trackerClient.FetchPullRequestStatusAsync(
                    query, ledger.PrNumber, cancellationToken);

                if (confirmed is not null &&
                    !string.Equals(confirmed.HeadSha, ledger.RejectedHeadSha, StringComparison.OrdinalIgnoreCase))
                {
                    ledger.Stage = PhaseStages.AwaitingVerify;
                    ClearRunnerHold(ledger);
                    ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
                    AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_repair_head_moved",
                        $"Repair moved PR #{ledger.PrNumber} head to {Short(confirmed.HeadSha)} " +
                        "(seen on a confirming read, after a stale one); re-verifying before the final review.");
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return;
                }

                await EscalateAsync(ledger,
                    $"The repair run for PR #{ledger.PrNumber} succeeded but the PR head never moved past the rejected commit {Short(ledger.RejectedHeadSha)} — refusing to re-review unchanged rejected code.",
                    cancellationToken);
            }

            return;
        }

        // Head moved: re-verify CI at the new head, then final review follows. The
        // repair ran, so an implementer that was out of quota is working again and
        // the hold it was under - and the cause it was part of - is over.
        ledger.Stage = PhaseStages.AwaitingVerify;
        ClearRunnerHold(ledger);
        ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
        AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_repair_head_moved",
            $"Repair moved PR #{ledger.PrNumber} head to {Short(pullRequest.HeadSha)}; re-verifying before the final review.");
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Did this run end because its vendor account was out of quota?
    /// </summary>
    /// <remarks>
    /// Read from what the runner recorded, not from the status: the whole point of
    /// ADCP#29 is that the status was the thing lying. A refusal reaches here as
    /// the run's last message, which is the runner's redacted stderr.
    /// </remarks>
    private static bool IsRunnerQuotaRefusal(RunEntity run) =>
        run.Status is not RunStatusNames.Running &&
        AgentQuotaSignals.IsQuotaExhaustion(run.LastMessage);

    /// <summary>
    /// Park the phase where it is until the refusal's own clock says to ask again.
    /// </summary>
    private async Task HoldForRunnerQuotaAsync(
        PhaseLedgerEntity ledger,
        RunEntity refusedRun,
        string what,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var resumeAtUtc = AgentQuotaReset.Resolve(refusedRun.LastMessage, nowUtc);
        var runner = string.IsNullOrWhiteSpace(refusedRun.Runner) ? "unknown" : refusedRun.Runner;

        // Held since the FIRST refusal of this run of holds, not this one. A window
        // that keeps being renewed is the signal that it is not a window.
        ledger.HoldSinceUtc ??= nowUtc;
        if (nowUtc - ledger.HoldSinceUtc.Value > RunnerQuotaHoldTimeout)
        {
            await EscalateRunnerQuotaAsync(ledger, runner, resumeAtUtc, what, cancellationToken);
            return;
        }

        ledger.HoldUntilUtc = resumeAtUtc;
        ledger.HoldReason = RunnerQuotaHoldReason;
        ledger.HoldRunner = runner;

        RewindToAskingStage(ledger);

        ledger.UpdatedAtUtc = nowUtc;
        AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, RunnerQuotaHoldEventName,
            $"Runner '{runner}' is out of quota, so {what} is held rather than escalated. " +
            $"Retrying after {resumeAtUtc:u} (held since {ledger.HoldSinceUtc.Value:u}). " +
            $"Recorded refusal: {Truncate(refusedRun.LastMessage, 300)}",
            JsonSerializer.Serialize(new RunnerQuotaHoldState(runner, resumeAtUtc)));
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Runner {Runner} is out of quota; {IssueIdentifier} holds at phase '{Stage}' until {ResumeAtUtc:u}.",
            runner,
            ledger.IssueIdentifier,
            ledger.Stage,
            resumeAtUtc);
    }

    /// <summary>
    /// Put the ledger back at the stage that ASKS, so the hold expiring is all it
    /// takes to try again.
    /// </summary>
    /// <remarks>
    /// Every stage that can be refused for quota needs this, and each one needs its
    /// own destination. Left where they are, `reviewing` and `wait_for_repair` both
    /// wait on a run that is already over - and waiting is not asking, so the hold
    /// expires, the switch finds the same dead run, and the hold is renewed. The
    /// review path was rewound from the start; the repair path was not, and it sat
    /// out one quota window after another until the six-hour bound escalated it.
    /// </remarks>
    private static void RewindToAskingStage(PhaseLedgerEntity ledger) =>
        ledger.Stage = ledger.Stage switch
        {
            PhaseStages.Reviewing => PhaseStages.AwaitingReview,
            PhaseStages.WaitForRepair => PhaseStages.AwaitingRepair,
            _ => ledger.Stage
        };

    /// <summary>
    /// True when the hold is over and the caller may go on advancing this ledger.
    /// </summary>
    private async Task<bool> ReleaseRunnerHoldIfDueAsync(PhaseLedgerEntity ledger, CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        if (ledger.HoldUntilUtc > nowUtc)
        {
            return false;
        }

        var runner = ledger.HoldRunner ?? "unknown";
        ledger.HoldUntilUtc = null;

        // HoldSinceUtc deliberately survives. It measures how long this cause has
        // been blocking, and a hold that expires straight into another refusal has
        // not stopped blocking - clearing it here would make the six-hour bound
        // unreachable and the escalation never fire.
        ledger.UpdatedAtUtc = nowUtc;
        AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, "phase_runner_quota_hold_expired",
            $"The hold on runner '{runner}' has expired; {ledger.IssueIdentifier} resumes at phase '{ledger.Stage}'.");
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The phase moved, so whatever it was waiting out is over.
    /// </summary>
    /// <remarks>
    /// If it was waiting on an exhausted account, that account has just been
    /// observed producing work, and that is the only honest evidence the outage
    /// ended. Recorded, because the duplicate suppression has to tell "still
    /// exhausted" from "exhausted again, later" and a reset timestamp cannot: it
    /// expires on schedule whether or not anything was fixed.
    ///
    /// Callers save; this only stages the change alongside theirs.
    /// </remarks>
    private void ClearRunnerHold(PhaseLedgerEntity ledger)
    {
        var wasQuotaHold = string.Equals(ledger.HoldReason, RunnerQuotaHoldReason, StringComparison.Ordinal);
        var runner = ledger.HoldRunner;

        DropRunnerHold(ledger);

        if (!wasQuotaHold || string.IsNullOrWhiteSpace(runner))
        {
            return;
        }

        AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, RunnerQuotaRecoveredEventName,
            $"Runner '{runner}' produced work again on {ledger.IssueIdentifier}, so it is no longer an open cause.",
            JsonSerializer.Serialize(new RunnerQuotaEventState(runner)));
    }

    /// <summary>
    /// This ledger has stopped waiting, but nobody has seen the runner work.
    /// </summary>
    /// <remarks>
    /// For the paths that leave a hold because the work turned out to be
    /// unnecessary rather than because it was done - a head that moved from outside
    /// the plane while a repair was still only being asked for. The ledger must not
    /// keep holding, because there is nothing left to hold for; the runner must not
    /// be recorded as recovered, because a push by someone else is no evidence at
    /// all about a vendor account's credit.
    ///
    /// The distinction is load-bearing: <see cref="IsRunnerQuotaAlreadyEscalatedAsync"/>
    /// takes the newest recovery-or-escalation row for a runner as the truth, so a
    /// recovery written here would close a cause that is still open and let the
    /// next reset window raise the same exhausted runner a second time.
    ///
    /// Callers save; this only stages the change alongside theirs.
    /// </remarks>
    private static void DropRunnerHold(PhaseLedgerEntity ledger)
    {
        ledger.HoldUntilUtc = null;
        ledger.HoldSinceUtc = null;
        ledger.HoldReason = null;
        ledger.HoldRunner = null;
    }

    /// <summary>
    /// Raise an exhausted runner with the owner - once per runner, per window.
    /// </summary>
    private async Task EscalateRunnerQuotaAsync(
        PhaseLedgerEntity ledger,
        string runner,
        DateTimeOffset resumeAtUtc,
        string what,
        CancellationToken cancellationToken)
    {
        if (await IsRunnerQuotaAlreadyEscalatedAsync(runner, cancellationToken))
        {
            // Someone else already said it. Keep holding quietly rather than
            // repeating one cause once per blocked issue - but hold at the stage
            // that asks, exactly as an unsuppressed hold does. A quiet hold that
            // could never re-dispatch would be a silent park, which is the fault
            // this whole path exists to remove.
            ledger.HoldUntilUtc = resumeAtUtc;
            ledger.HoldReason = RunnerQuotaHoldReason;
            ledger.HoldRunner = runner;
            RewindToAskingStage(ledger);
            ledger.UpdatedAtUtc = timeProvider.GetUtcNow();
            AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, RunnerQuotaHoldEventName,
                $"Runner '{runner}' being out of quota is already with the command center; " +
                $"{what} keeps holding until {resumeAtUtc:u} instead of raising the same cause again.",
                JsonSerializer.Serialize(new RunnerQuotaHoldState(runner, resumeAtUtc)));
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        // Recorded BEFORE the escalation, because EscalateAsync saves and the next
        // ledger in this same pass has to be able to see that this cause is taken.
        AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, RunnerQuotaEscalatedEventName,
            $"Runner '{runner}' is out of quota and has been raised with the command center.",
            JsonSerializer.Serialize(new RunnerQuotaHoldState(runner, resumeAtUtc)));

        await EscalateAsync(ledger,
            $"Runner '{runner}' is out of quota, so {what} never ran. It has been held since " +
            $"{ledger.HoldSinceUtc:u} — longer than {Humanise(RunnerQuotaHoldTimeout)} — and the account has not " +
            $"recovered; the next reset it reported is {resumeAtUtc:u}. Nothing here is repairable by a directive: " +
            "the account needs credit before any phase on this runner can move.",
            cancellationToken);
    }

    /// <summary>
    /// Is this runner's exhaustion already an open attention item?
    /// </summary>
    /// <remarks>
    /// Answered from the last thing recorded ABOUT the runner, not from a clock.
    /// The first rule suppressed only while the escalation's own `ResumeAtUtc` was
    /// still ahead - so the moment the reset it named passed, the cause it named
    /// looked resolved and the next window raised the same empty account again.
    /// The 2026-09-04 outage spanned two windows; the acceptance is one attention
    /// item for as long as the runner stays exhausted, which is a condition, not a
    /// duration.
    ///
    /// So the two events are read together and the newer one wins: an escalation
    /// with no later recovery means the cause is still open.
    /// </remarks>
    private async Task<bool> IsRunnerQuotaAlreadyEscalatedAsync(string runner, CancellationToken cancellationToken)
    {
        // The most recent few, not just the last one: two runners can be exhausted
        // at once, and reading only the newest row would let the older runner
        // escalate a second time simply because the other one spoke after it.
        //
        // Ordered by Id, not by OccurredAtUtc: SQLite cannot ORDER BY a
        // DateTimeOffset, and the identity column answers "written last" exactly.
        var recent = await dbContext.EventLog
            .Where(entry => entry.EventName == RunnerQuotaEscalatedEventName ||
                            entry.EventName == RunnerQuotaRecoveredEventName)
            .OrderByDescending(entry => entry.Id)
            .Select(entry => new { entry.EventName, entry.DataJson })
            .Take(RunnerQuotaEscalationScanLimit)
            .ToListAsync(cancellationToken);

        foreach (var entry in recent)
        {
            if (string.IsNullOrWhiteSpace(entry.DataJson))
            {
                continue;
            }

            RunnerQuotaEventState? state;
            try
            {
                state = JsonSerializer.Deserialize<RunnerQuotaEventState>(entry.DataJson);
            }
            catch (JsonException)
            {
                // A row this cannot read is from a different shape of the world.
                // Skipping it fails toward escalating, which is the safe direction:
                // the worst case is the duplicate this exists to prevent, and the
                // alternative is silence about a stopped plane.
                continue;
            }

            if (state is null || !string.Equals(state.Runner, runner, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The newest row for this runner decides, and nothing older is read:
            // an escalation means the cause is still open, a recovery means it was
            // closed and anything after it is a new outage worth naming.
            return entry.EventName == RunnerQuotaEscalatedEventName;
        }

        return false;
    }

    // Enough to cover every runner that could be exhausted at once several times
    // over, without reading an event log that grows forever. Raised when recoveries
    // joined escalations in the same scan: both are written per runner per outage,
    // so the window that has to hold them is twice what it was.
    private const int RunnerQuotaEscalationScanLimit = 50;

    private sealed record RunnerQuotaHoldState(string Runner, DateTimeOffset ResumeAtUtc);

    // The part of a quota event every reader needs. Escalations carry the reset
    // they expected as well; recoveries have nothing to say about a reset, and the
    // suppression rule no longer asks about one.
    private sealed record RunnerQuotaEventState(string Runner);

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] + "…" : value;

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

        AddPhaseEvent(ledger.IssueId, ledger.IssueIdentifier, EscalationEventName, $"Phase orchestration: {reason}");
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogError("Phase escalation for {IssueIdentifier}: {Reason}", ledger.IssueIdentifier, reason);
    }

    // EscalateAsync parks a ledger. A postcondition can fail before any ledger
    // exists - an implementation that produced no pull request never gets one - so
    // this reports through the run lane alone. Flipping the run off `succeeded` is
    // also what makes it fire once: the scan that found it only looks at succeeded
    // implementation runs.
    private async Task EscalateRunAsync(
        string issueId,
        string issueIdentifier,
        string reason,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var runs = await dbContext.Runs
            .Where(run => run.IssueId == issueId)
            .ToListAsync(cancellationToken);
        var latestRun = runs.OrderByDescending(run => run.StartedAtUtc).FirstOrDefault();
        if (latestRun is not null)
        {
            latestRun.Status = RunStatusNames.NeedsCommandCenter;
            latestRun.CompletedAtUtc ??= nowUtc;
            latestRun.LastEvent = "needs_command_center";
            latestRun.LastMessage = $"Phase orchestration: {reason}";
            latestRun.LastEventAtUtc = nowUtc;
            latestRun.EscalationPostedAtUtc = null;
        }

        AddPhaseEvent(issueId, issueIdentifier, EscalationEventName, $"Phase orchestration: {reason}");
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogError("Phase escalation for {IssueIdentifier}: {Reason}", issueIdentifier, reason);
    }

    private void AddPhaseEvent(
        string issueId,
        string issueIdentifier,
        string eventName,
        string message,
        string? dataJson = null)
    {
        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = issueId,
            IssueIdentifier = issueIdentifier,
            EventName = eventName,
            Level = (eventName == EscalationEventName ? LogLevel.Error : LogLevel.Information).ToString(),
            Message = message,
            // Machine-readable state goes here rather than being parsed back out of
            // the message. A timestamp recovered by reading prose breaks the next
            // time somebody improves the wording.
            DataJson = dataJson,
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
