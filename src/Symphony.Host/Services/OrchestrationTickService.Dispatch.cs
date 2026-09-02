using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed partial class OrchestrationTickService
{
    private static readonly TimeSpan AcquisitionDiagnosticThreshold = TimeSpan.FromMinutes(2);

    // Ordinary failures used to retry without limit. That was survivable only while a
    // retrying run quietly surrendered its slot to whoever asked next - which is the
    // starvation in ADCP#25. Now that a reservation is honoured for the life of the
    // run, an unbounded retry would hold the queue against everything behind it, so
    // the run has to be able to give up. Six attempts spans roughly ten minutes of
    // backoff, after which the cause is not transient and a person should see it.
    private const int MaxRetryAttempts = 6;

    private async Task DispatchCandidatesAsync(
        WorkflowDefinition workflowDefinition,
        string apiKey,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var queries = BuildTrackerQueries(workflowDefinition, apiKey);
        var query = queries.Primary;

        // One fetch per tracked repository. A repository that fails is reported and
        // skipped rather than taking the tick down with it: with more than one
        // tracked, an outage on the plane's own backlog must not stop the product
        // queue, and vice versa.
        // Only ask GitHub on the scan clock. Between scans the tick runs in full on
        // the candidates it already knows about, so dispatch, phases, reconciliation
        // and everything local keep their fast cadence - the scan is the only
        // expensive part, and now the only part slowed.
        var now = timeProvider.GetUtcNow();
        var dueForScan = now >= nextCandidateScanUtc;

        var issues = new List<NormalizedIssue>();
        var reachedAnyRepository = false;
        string? lastFailureCause = null;
        var lastFailureTransient = false;

        foreach (var repositoryQuery in dueForScan ? queries.All : [])
        {
            try
            {
                issues.AddRange(await trackerClient.FetchCandidateIssuesAsync(repositoryQuery, cancellationToken));
                reachedAnyRepository = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The old message was the exception's TYPE and nothing else, which
                // produced a dozen identical "GitHubTrackerException." rows and sent
                // the real cause - intermittent DNS - to a 64 MB rotated log where
                // nobody glancing at the page would ever find it. Record the cause.
                lastFailureTransient = TrackerReachability.IsTransientConnectivity(ex);
                lastFailureCause = TrackerReachability.DescribeCause(ex);

                // Connectivity blips recover within a tick or two and cost nothing;
                // logging each at Error teaches the reader that red means nothing.
                // A refusal - bad credentials, a malformed query - will fail the same
                // way forever, and does deserve the louder level.
                AddIssueEvent(
                    null,
                    null,
                    null,
                    null,
                    "candidate_scan_failed",
                    lastFailureTransient ? LogLevel.Warning : LogLevel.Error,
                    $"Candidate fetch failed for {repositoryQuery.Owner}/{repositoryQuery.Repo}: {lastFailureCause}");
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogWarning(
                    ex,
                    "Candidate fetch failed for {Owner}/{Repo}. Its issues are skipped this tick.",
                    repositoryQuery.Owner,
                    repositoryQuery.Repo);
            }
        }

        // Reachability is about GitHub, not about one repository: reaching any of
        // them proves the tracker is up, and only reaching none is an outage.
        if (!dueForScan)
        {
            // No scan this tick, so nothing was learned about GitHub either way -
            // reachability must not be touched, or a quiet tick would look like a
            // successful probe and mask a real outage.
            issues.AddRange(lastCandidates);
        }
        else if (reachedAnyRepository)
        {
            trackerReachability.RecordSuccess();
            nextCandidateScanUtc = now + CandidateScanInterval;
            lastCandidates = issues;
        }
        else
        {
            trackerReachability.RecordFailure(lastFailureCause ?? "unknown", lastFailureTransient);
            // Retry on the next tick rather than waiting out the interval: an
            // outage should recover as soon as it can, not on the slow clock.
            return;
        }

        await UpsertIssueCacheAsync(issues, workflowDefinition, cancellationToken);

        var activeRuns = await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.Running || run.Status == RunStatusNames.Retrying)
            .ToListAsync(cancellationToken);

        // Issues finalized during this tick (legacy continuation drain) must not be
        // re-dispatched in the same tick; the next tick's phase-aware checks govern them.
        var finalizedThisTick = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Two different questions, and conflating them is ADCP#25. "Is this issue
        // executing right now" is about live agent processes. "Is there room to start
        // something new" is about work in flight - and a run waiting for its retry is
        // still in flight: it owns a workspace, a claim and an attempt history.
        // Counting only live processes left the slot apparently free in the gap between
        // a failure and its retry, a competing issue was dispatched into it, and the
        // retry that came back to a full plane was then destroyed rather than made to
        // wait its turn. Two ready issues took it in turns to do that to each other,
        // every three minutes, indefinitely.
        var runningIssueIds = new HashSet<string>(
            activeRuns.Where(run => run.Status == RunStatusNames.Running).Select(run => run.IssueId),
            StringComparer.OrdinalIgnoreCase);
        var reservedStateByIssueId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var activeRun in activeRuns)
        {
            reservedStateByIssueId[activeRun.IssueId] = NormalizeStateKey(activeRun.State);
        }

        var countsByState = CountReservationsByState(reservedStateByIssueId);

        var dueRetries = await dbContext.RetryQueue
            .FromSqlInterpolated($"""
                SELECT *
                FROM retry_queue
                WHERE DueAtUtc <= {timeProvider.GetUtcNow()}
                ORDER BY DueAtUtc
                """)
            .ToListAsync(cancellationToken);

        var candidatesById = issues.ToDictionary(issue => issue.Id, StringComparer.OrdinalIgnoreCase);

        // M4 ownership rule: an issue with an active phase ledger belongs to the
        // phase orchestrator (verify / cross-vendor review / bounded repair). The
        // ordinary candidate and retry paths must not dispatch it — they would
        // re-dispatch it as a plain implementation on the label-routed runner and
        // overwrite the phase run's row, which is exactly what happened to the
        // first live review dispatch.
        var phaseOwnedIssueIds = new HashSet<string>(
            await dbContext.PhaseLedger
                .Where(entry => entry.Stage != PhaseStages.Merged &&
                                entry.Stage != PhaseStages.Escalated &&
                                entry.Stage != PhaseStages.Closed)
                .Select(entry => entry.IssueId)
                .ToListAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        await ReconcileAbandonedReleasedRunsAsync(workflowDefinition, query, candidatesById, cancellationToken);
        var staleReservationIssueIds = await ReconcileStaleReservationsBeforeCandidateSelectionAsync(
            candidatesById,
            instanceId,
            cancellationToken);
        dueRetries = dueRetries
            .Where(retry => !staleReservationIssueIds.Contains(retry.IssueId))
            .ToList();

        foreach (var retryEntry in dueRetries)
        {
            if (phaseOwnedIssueIds.Contains(retryEntry.IssueId))
            {
                // The phase orchestrator owns this issue; it decides whether a
                // failed phase run is retried or escalated.
                continue;
            }

            if (string.Equals(retryEntry.DelayType, RetryDelayTypes.Continuation, StringComparison.OrdinalIgnoreCase))
            {
                await CompleteSuccessfulDispatchAsync(retryEntry, instanceId, cancellationToken);
                finalizedThisTick.Add(retryEntry.IssueId);
                continue;
            }

            if (retryEntry.Attempt > MaxRetryAttempts)
            {
                await EscalateRunToCommandCenterAsync(
                    await FindLatestRunWithStatusAsync(retryEntry.IssueId, RunStatusNames.Retrying, cancellationToken),
                    retryEntry.IssueId,
                    retryEntry.IssueIdentifier,
                    instanceId,
                    $"Issue {retryEntry.IssueIdentifier} has failed {MaxRetryAttempts} bounded attempts and is not " +
                    "recovering on its own. The run is ended here so it stops holding an agent slot against the rest " +
                    $"of the queue. Latest recorded cause: {retryEntry.Error ?? "unknown"}.",
                    cancellationToken);
                continue;
            }

            if (!candidatesById.TryGetValue(retryEntry.IssueId, out var retryIssue))
            {
                await HandleMissingRetryCandidateAsync(
                    retryEntry,
                    query,
                    workflowDefinition,
                    instanceId,
                    cancellationToken);
                continue;
            }

            // Capacity is deliberately NOT part of this check (ADCP#25). It used to be,
            // via IsDispatchEligible, and that made "the plane is busy" indistinguishable
            // from "this issue is closed" - both released the reservation and finalized
            // the run as released_ineligible, which is how work in flight was thrown
            // away for losing a race it should simply have waited out. Capacity is
            // handled below, where it reschedules.
            var retryRefusal = DetermineIneligibilityReasonIgnoringCapacity(
                retryIssue,
                workflowDefinition,
                runningIssueIds);
            if (retryRefusal is not null)
            {
                await RecordCandidateRefusalAsync(retryIssue, retryRefusal, cancellationToken);
                await ReleaseRetryReservationAsync(
                    retryEntry.IssueId,
                    retryEntry.IssueIdentifier,
                    instanceId,
                    $"issue no longer eligible for dispatch: {retryRefusal}",
                    cancellationToken);
                continue;
            }

            if (await TryBlockImplementationRetryRedispatchAsync(
                    retryEntry,
                    retryIssue,
                    workflowDefinition,
                    instanceId,
                    cancellationToken))
            {
                continue;
            }

            // The retry already holds a reservation, so it must not be counted against
            // itself; it is resuming its own slot, not asking for a second one.
            var otherReservations = CountReservationsByState(reservedStateByIssueId, excludeIssueId: retryIssue.Id);
            var otherReservationCount = reservedStateByIssueId.Count - (reservedStateByIssueId.ContainsKey(retryIssue.Id) ? 1 : 0);
            if (!HasGlobalSlot(workflowDefinition, otherReservationCount) ||
                !HasStateSlot(retryIssue.State, workflowDefinition, otherReservations))
            {
                await RecordCandidateRefusalAsync(retryIssue, "concurrency_limit", cancellationToken);
                await RescheduleRetryAsync(
                    retryEntry,
                    instanceId,
                    "no available orchestrator slots",
                    workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    cancellationToken,
                    // Waiting for a slot is not a failed attempt. Counting it as one
                    // would inflate the backoff and spend the retry budget of work that
                    // has not been tried yet.
                    advanceAttempt: false);
                continue;
            }

            var quotaFallbackRunner = await ResolveQuotaFallbackRunnerAsync(
                retryEntry,
                workflowDefinition,
                cancellationToken);

            if (await DispatchIssueAsync(
                    retryIssue,
                    workflowDefinition,
                    instanceId,
                    retryEntry.Attempt,
                    cancellationToken,
                    resetContinuousTurnBudget: retryEntry.DelayType == RetryDelayTypes.Backoff,
                    runnerOverride: quotaFallbackRunner))
            {
                runningIssueIds.Add(retryIssue.Id);
                reservedStateByIssueId[retryIssue.Id] = NormalizeStateKey(retryIssue.State);
                countsByState = CountReservationsByState(reservedStateByIssueId);
            }
        }

        var latestRunByIssueId = await LoadLatestRunByIssueAsync(candidatesById.Keys, cancellationToken);

        var orderedIssues = OrderIssuesForDispatch(issues).ToList();
        for (var issueIndex = 0; issueIndex < orderedIssues.Count; issueIndex++)
        {
            var issue = orderedIssues[issueIndex];
            if (!HasGlobalSlot(workflowDefinition, reservedStateByIssueId.Count))
            {
                var remainingIssues = orderedIssues
                    .Skip(issueIndex)
                    .ToList();
                await RecordRemainingCapacityRefusalsAsync(
                    remainingIssues,
                    workflowDefinition,
                    reservedStateByIssueId,
                    cancellationToken);
                break;
            }

            if (phaseOwnedIssueIds.Contains(issue.Id))
            {
                // Owned by the phase orchestrator until its ledger reaches a
                // terminal stage; dispatching here would clobber the phase run.
                await RecordCandidateRefusalAsync(issue, "owned_by_phase_orchestrator", cancellationToken);
                continue;
            }

            // Reserved, not merely running: an issue with a pending retry is already
            // being worked and must not be started a second time from here.
            var reservedIssueIds = new HashSet<string>(reservedStateByIssueId.Keys, StringComparer.OrdinalIgnoreCase);
            if (finalizedThisTick.Contains(issue.Id) ||
                !IsDispatchEligible(issue, workflowDefinition, reservedIssueIds, countsByState))
            {
                if (!finalizedThisTick.Contains(issue.Id))
                {
                    var reason = DetermineIneligibilityReason(issue, workflowDefinition, reservedIssueIds, countsByState);
                    if (!string.Equals(reason, "already_running", StringComparison.OrdinalIgnoreCase))
                    {
                        await RecordCandidateRefusalAsync(issue, reason, cancellationToken);
                    }
                }

                continue;
            }

            if (await ShouldBlockImplementationRedispatchAsync(issue, latestRunByIssueId, workflowDefinition, cancellationToken))
            {
                await RecordCandidateRefusalAsync(issue, "implementation_redispatch_blocked", cancellationToken);
                continue;
            }

            if (await DispatchIssueAsync(issue, workflowDefinition, instanceId, attempt: null, cancellationToken))
            {
                runningIssueIds.Add(issue.Id);
                reservedStateByIssueId[issue.Id] = NormalizeStateKey(issue.State);
                countsByState = CountReservationsByState(reservedStateByIssueId);
            }
        }
    }

    // ADCP#24. Retrying a quota-exhausted vendor cannot succeed however many
    // attempts are left, so when the account that just ran out is not the only one
    // available, the retry goes to the other one.
    //
    // Only on exhaustion. An ordinary implementation failure stays with the vendor
    // that produced it, because repairing your own work and having someone else
    // redo it are different things, and only the first is what a retry means.
    //
    // ADR-006 (independent review is dispatched on the OTHER vendor) survives this
    // by construction, twice over. The retry loop never touches phase-owned issues,
    // so a review or repair dispatch cannot be re-vendored here; and reviewer
    // selection is derived from the run's recorded Runner, which is written from
    // the runner that actually executed - so a fallen-back implementation is
    // reviewed by the vendor that did not implement it, not by the configured
    // default that never ran.
    private async Task<string?> ResolveQuotaFallbackRunnerAsync(
        RetryQueueEntity retryEntry,
        WorkflowDefinition workflowDefinition,
        CancellationToken cancellationToken)
    {
        if (!AgentQuotaSignals.IsQuotaExhaustion(retryEntry.Error))
        {
            return null;
        }

        var exhaustedRun = await FindLatestRunWithStatusAsync(
            retryEntry.IssueId,
            RunStatusNames.Retrying,
            cancellationToken);
        var fallbackRunner = AgentQuotaSignals.ResolveFallbackRunner(
            workflowDefinition.Runtime.Agent.FallbackRunner,
            exhaustedRun?.Runner);
        if (fallbackRunner is null)
        {
            return null;
        }

        AddIssueEvent(
            retryEntry.IssueId,
            retryEntry.IssueIdentifier,
            exhaustedRun?.Id,
            null,
            "quota_fallback_dispatched",
            LogLevel.Warning,
            $"Runner '{exhaustedRun?.Runner ?? "unknown"}' is out of quota for {retryEntry.IssueIdentifier}, so this " +
            $"attempt is dispatched to '{fallbackRunner}' instead of retrying into the same limit. Recorded cause: " +
            $"{retryEntry.Error}.");
        await dbContext.SaveChangesAsync(cancellationToken);

        return fallbackRunner;
    }

    private static Dictionary<string, int> CountReservationsByState(
        IReadOnlyDictionary<string, string> reservedStateByIssueId,
        string? excludeIssueId = null)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (issueId, stateKey) in reservedStateByIssueId)
        {
            if (excludeIssueId is not null && issueId.Equals(excludeIssueId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            counts[stateKey] = counts.GetValueOrDefault(stateKey) + 1;
        }

        return counts;
    }

    private async Task<HashSet<string>> ReconcileStaleReservationsBeforeCandidateSelectionAsync(
        IReadOnlyDictionary<string, NormalizedIssue> candidatesById,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var activeIssueIds = await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.Running || run.Status == RunStatusNames.Retrying)
            .Select(run => run.IssueId)
            .ToListAsync(cancellationToken);
        var staleIssueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var staleClaims = await dbContext.DispatchClaims
            .Where(claim => claim.Status == "active" && !activeIssueIds.Contains(claim.IssueId))
            .ToListAsync(cancellationToken);
        foreach (var claim in staleClaims)
        {
            staleIssueIds.Add(claim.IssueId);
            claim.Status = RunStatusNames.ReleasedIneligible;
            claim.ReleasedAtUtc = timeProvider.GetUtcNow();
            claim.UpdatedAtUtc = timeProvider.GetUtcNow();
            AddIssueEvent(
                claim.IssueId,
                claim.IssueIdentifier,
                null,
                null,
                "stale_reservation_reconciled",
                LogLevel.Warning,
                $"Released stale active claim for {claim.IssueIdentifier}: no running or retrying run exists.");
        }

        var staleRetries = await dbContext.RetryQueue
            .Where(retry => !activeIssueIds.Contains(retry.IssueId))
            .ToListAsync(cancellationToken);
        foreach (var retry in staleRetries)
        {
            staleIssueIds.Add(retry.IssueId);
            dbContext.RetryQueue.Remove(retry);
            await coordinationStore.ReleaseIssueClaimAsync(
                retry.IssueId,
                instanceId,
                RunStatusNames.ReleasedIneligible,
                cancellationToken);
            AddIssueEvent(
                retry.IssueId,
                retry.IssueIdentifier,
                retry.RunId,
                null,
                "stale_reservation_reconciled",
                LogLevel.Warning,
                $"Removed stale retry reservation for {retry.IssueIdentifier}: no running or retrying run exists.");
        }

        var staleActiveClaimsForCandidates = staleClaims
            .Where(claim => candidatesById.ContainsKey(claim.IssueId))
            .Select(claim => claim.IssueId)
            .ToList();
        if (staleActiveClaimsForCandidates.Count > 0)
        {
            logger.LogWarning(
                "Released stale active claims before candidate selection for {IssueCount} currently eligible issue(s).",
                staleActiveClaimsForCandidates.Count);
        }

        if (staleClaims.Count > 0 || staleRetries.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return staleIssueIds;
    }

    private async Task<Dictionary<string, RunEntity>> LoadLatestRunByIssueAsync(
        IEnumerable<string> issueIds,
        CancellationToken cancellationToken)
    {
        var ids = issueIds.ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, RunEntity>(StringComparer.OrdinalIgnoreCase);
        }

        var runs = await dbContext.Runs
            .Where(run => ids.Contains(run.IssueId))
            .ToListAsync(cancellationToken);

        // SQLite cannot ORDER BY DateTimeOffset reliably; order in memory.
        return runs
            .GroupBy(run => run.IssueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(run => run.StartedAtUtc).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<bool> ShouldBlockImplementationRedispatchAsync(
        NormalizedIssue issue,
        IReadOnlyDictionary<string, RunEntity> latestRunByIssueId,
        WorkflowDefinition workflowDefinition,
        CancellationToken cancellationToken)
    {
        if (!latestRunByIssueId.TryGetValue(issue.Id, out var latestRun))
        {
            return false;
        }

        // A prior escalation must be resolved by the Commander before automation resumes.
        if (string.Equals(latestRun.Status, RunStatusNames.NeedsCommandCenter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // An issue whose implementation already succeeded must not be silently
        // reimplemented. Automatic redispatch is allowed only when pull request evidence
        // affirmatively shows the implementation's PR has been resolved: PR data was
        // fetched, linkage exists, and none of the linked PRs are still open. Anything
        // less fails closed and requires the Commander to dispatch an explicit later
        // phase.
        if (!string.Equals(latestRun.Status, RunStatusNames.Succeeded, StringComparison.OrdinalIgnoreCase) ||
            !IsImplementationPhase(latestRun.Phase))
        {
            return false;
        }

        string blockMessage;
        if (!workflowDefinition.Runtime.Tracker.IncludePullRequests)
        {
            blockMessage =
                $"Issue {issue.Identifier} already has a completed implementation, but pull request evidence is " +
                "unavailable because tracker include_pull_requests is disabled. An existing pull request cannot be " +
                "ruled out, so automatic reimplementation is blocked; Commander/review handling is required.";
        }
        else if (HasOpenPullRequest(issue))
        {
            blockMessage =
                $"Issue {issue.Identifier} already has a completed implementation with an open pull request. " +
                "Automatic reimplementation is blocked; the Commander must dispatch an explicit repair/review phase or close the PR.";
        }
        else if (issue.PullRequests.Count == 0)
        {
            blockMessage =
                $"Issue {issue.Identifier} already has a completed implementation, but the tracker reports no pull " +
                "request linkage for it. \"No linked PR\" is not proof that reimplementation is safe, so automatic " +
                "reimplementation is blocked; Commander/review handling is required.";
        }
        else
        {
            // Linked PRs exist and none are open (merged or closed): the implementation's
            // output has been resolved, so a later-phase dispatch may proceed.
            return false;
        }

        var alreadyLogged = await dbContext.EventLog.AnyAsync(
            entry => entry.RunId == latestRun.Id && entry.EventName == "implementation_redispatch_blocked",
            cancellationToken);
        if (!alreadyLogged)
        {
            dbContext.EventLog.Add(new EventLogEntity
            {
                IssueId = issue.Id,
                IssueIdentifier = issue.Identifier,
                RunId = latestRun.Id,
                EventName = "implementation_redispatch_blocked",
                Level = LogLevel.Warning.ToString(),
                Message = blockMessage,
                OccurredAtUtc = timeProvider.GetUtcNow()
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogWarning(
            "Blocked implementation redispatch for issue {IssueIdentifier}: {Reason}",
            issue.Identifier,
            blockMessage);
        return true;
    }

    private async Task<bool> TryBlockImplementationRetryRedispatchAsync(
        RetryQueueEntity retryEntry,
        NormalizedIssue issue,
        WorkflowDefinition workflowDefinition,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var retryingRun = await FindLatestRunWithStatusAsync(retryEntry.IssueId, RunStatusNames.Retrying, cancellationToken);
        if (retryingRun is null || !IsImplementationPhase(retryingRun.Phase))
        {
            return false;
        }

        // The retry path must honor the same durable-work/open-PR protection as the
        // fresh-dispatch path: an implementation retry that would reimplement work whose
        // pull request already exists (or cannot be ruled out) fails closed and goes to
        // the Command Center. The PR and workspace are preserved for review.
        string blockReason;
        if (HasOpenPullRequest(issue))
        {
            blockReason =
                $"Issue {retryEntry.IssueIdentifier} has a pending implementation retry, but the issue already has an " +
                "open pull request. Re-running the implementation would reimplement existing work, so the retry is " +
                "blocked; the Commander must review the pull request or dispatch an explicit later phase.";
        }
        else if (!workflowDefinition.Runtime.Tracker.IncludePullRequests &&
                 await HasDurableUnfinishedWorkEvidenceAsync(retryingRun, cancellationToken))
        {
            blockReason =
                $"Issue {retryEntry.IssueIdentifier} has a pending implementation retry with durable implementation " +
                "evidence, but pull request evidence is unavailable because tracker include_pull_requests is disabled. " +
                "An existing pull request cannot be ruled out, so the retry is blocked; Commander/review handling is required.";
        }
        else if (workflowDefinition.Runtime.Tracker.IncludePullRequests &&
                 issue.PullRequests.Count == 0 &&
                 await HasCachedPullRequestEvidenceAsync(retryEntry.IssueId, cancellationToken))
        {
            blockReason =
                $"Issue {retryEntry.IssueIdentifier} has a pending implementation retry and previously recorded pull " +
                "request evidence, but the tracker no longer reports any pull request linkage. \"No linked PR\" is not " +
                "proof that reimplementation is safe, so the retry is blocked; Commander/review handling is required.";
        }
        else
        {
            return false;
        }

        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = retryEntry.IssueId,
            IssueIdentifier = retryEntry.IssueIdentifier,
            RunId = retryingRun.Id,
            EventName = "implementation_redispatch_blocked",
            Level = LogLevel.Warning.ToString(),
            Message = blockReason,
            OccurredAtUtc = timeProvider.GetUtcNow()
        });

        await EscalateRunToCommandCenterAsync(
            retryingRun,
            retryEntry.IssueId,
            retryEntry.IssueIdentifier,
            instanceId,
            blockReason,
            cancellationToken);
        return true;
    }

    private static bool IsImplementationPhase(string? phase)
    {
        // Legacy runs may predate the phase column; fail closed by treating an absent
        // phase as implementation.
        return string.IsNullOrWhiteSpace(phase) ||
               string.Equals(phase, RunPhaseNames.Implementation, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOpenPullRequest(NormalizedIssue issue)
    {
        return issue.PullRequests.Any(pullRequest =>
            string.IsNullOrWhiteSpace(pullRequest.State) ||
            pullRequest.State.Trim().Equals("open", StringComparison.OrdinalIgnoreCase));
    }

    // M3: dispatch a directive-authorized issue outside the ordinary candidate
    // loop (escalated issues are usually delabeled, so they never appear in the
    // label-filtered candidate query). Slot limits were checked by the directive
    // processor; the claim gate inside DispatchIssueAsync still applies.
    private async Task<bool> DispatchDirectiveIssueAsync(
        NormalizedIssue issue,
        WorkflowDefinition workflowDefinition,
        string instanceId,
        DirectiveDispatchContext directive,
        CancellationToken cancellationToken)
    {
        return await DispatchIssueAsync(
            issue,
            workflowDefinition,
            instanceId,
            attempt: null,
            cancellationToken,
            directive: directive);
    }

    // M4: dispatch a phase-orchestrated run (review/final review on the other
    // vendor, or the single bounded repair) outside the ordinary candidate loop.
    private async Task<bool> DispatchPhaseIssueAsync(
        NormalizedIssue issue,
        WorkflowDefinition workflowDefinition,
        string instanceId,
        PhaseDispatchRequest phaseRequest,
        CancellationToken cancellationToken)
    {
        return await DispatchIssueAsync(
            issue,
            workflowDefinition,
            instanceId,
            attempt: null,
            cancellationToken,
            phaseDispatch: phaseRequest);
    }

    private async Task<bool> DispatchIssueAsync(
        NormalizedIssue issue,
        WorkflowDefinition workflowDefinition,
        string instanceId,
        int? attempt,
        CancellationToken cancellationToken,
        bool resetContinuousTurnBudget = false,
        DirectiveDispatchContext? directive = null,
        PhaseDispatchRequest? phaseDispatch = null,
        string? runnerOverride = null)
    {
        AddIssueEvent(
            issue.Id,
            issue.Identifier,
            null,
            null,
            "claim_attempted",
            LogLevel.Information,
            $"Attempting to claim issue {issue.Identifier}.");
        await dbContext.SaveChangesAsync(cancellationToken);

        var claimResult = await coordinationStore.TryClaimIssueAsync(
            issue.Id,
            issue.Identifier,
            ResolveLeaseName(),
            instanceId,
            cancellationToken);

        if (!claimResult.Claimed)
        {
            var claimRefusalReason = claimResult.Reason ?? "claim_refused";
            if (await ShouldRecordCandidateRefusalAsync(issue.Id, claimRefusalReason, cancellationToken))
            {
                AddIssueEvent(
                    issue.Id,
                    issue.Identifier,
                    null,
                    null,
                    "claim_refused",
                    LogLevel.Warning,
                    $"Claim refused for issue {issue.Identifier}: {claimResult.Reason}.");
            }

            if (await ShouldWarnDelayedAcquisitionAsync(issue.Id, cancellationToken))
            {
                AddIssueEvent(
                    issue.Id,
                    issue.Identifier,
                    null,
                    null,
                    "candidate_acquisition_delayed",
                    LogLevel.Warning,
                    $"Issue {issue.Identifier} has remained unclaimed past the {AcquisitionDiagnosticThreshold.TotalMinutes:0}-minute acquisition diagnostic threshold. Latest refusal reason: {claimResult.Reason}.");
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        var nowUtc = timeProvider.GetUtcNow();
        var run = await dbContext.Runs
            .Where(runEntity =>
                runEntity.IssueId == issue.Id &&
                (runEntity.Status == RunStatusNames.Running || runEntity.Status == RunStatusNames.Retrying))
            .SingleOrDefaultAsync(cancellationToken);

        var dispatchPhase = phaseDispatch?.Phase ?? directive?.Phase ?? RunPhaseNames.Implementation;
        if (run is null)
        {
            run = new RunEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                IssueId = issue.Id,
                IssueIdentifier = issue.Identifier,
                OwnerInstanceId = instanceId,
                Status = RunStatusNames.Running,
                State = issue.State,
                Repository = issue.Repository,
                Phase = dispatchPhase,
                CurrentRetryAttempt = attempt,
                StartedAtUtc = nowUtc
            };
            dbContext.Runs.Add(run);
        }
        else
        {
            run.OwnerInstanceId = instanceId;
            run.Status = RunStatusNames.Running;
            run.State = issue.State;
            run.Repository = issue.Repository;
            run.Phase = dispatchPhase;
            run.CurrentRetryAttempt = attempt;
            run.CompletedAtUtc = null;
            run.RequestedStopReason = null;
            run.CleanupWorkspaceOnStop = false;
            run.SessionId = null;
            run.LastReportedInputTokens = 0;
            run.LastReportedOutputTokens = 0;
            run.LastReportedTotalTokens = 0;
            if (resetContinuousTurnBudget)
            {
                run.TurnCount = 0;
            }
        }

        var runAttempt = new RunAttemptEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RunId = run.Id,
            IssueId = issue.Id,
            AttemptNumber = attempt,
            Status = RunStatusNames.Running,
            StartedAtUtc = nowUtc
        };
        dbContext.RunAttempts.Add(runAttempt);

        var retryEntry = await dbContext.RetryQueue.SingleOrDefaultAsync(
            retry => retry.IssueId == issue.Id,
            cancellationToken);
        if (retryEntry is not null)
        {
            dbContext.RetryQueue.Remove(retryEntry);
        }

        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = issue.Id,
            IssueIdentifier = issue.Identifier,
            RunId = run.Id,
            RunAttemptId = runAttempt.Id,
            EventName = "issue_dispatched",
            Level = LogLevel.Information.ToString(),
            Message = $"Issue {issue.Identifier} dispatched with attempt {(attempt.HasValue ? attempt.Value.ToString() : "initial")}.",
            OccurredAtUtc = nowUtc
        });
        AddIssueEvent(
            issue.Id,
            issue.Identifier,
            run.Id,
            runAttempt.Id,
            "claim_succeeded",
            LogLevel.Information,
            $"Claim succeeded for issue {issue.Identifier}.");
        await ClearEligibleSeenAsync(issue.Id, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        var started = await issueExecutionCoordinator.TryStartAsync(
            new IssueExecutionRequest(
                run.Id,
                runAttempt.Id,
                instanceId,
                attempt,
                issue,
                workflowDefinition,
                DirectiveInstructions: directive?.Instructions,
                DirectiveAction: directive?.Action,
                DirectivePhase: directive?.Phase,
                PromptOverride: phaseDispatch?.Prompt,
                RunnerOverride: phaseDispatch?.RunnerName ?? runnerOverride),
            cancellationToken);

        if (!started)
        {
            run.Status = RunStatusNames.Retrying;
            run.CurrentRetryAttempt = attempt.HasValue ? attempt.Value + 1 : 1;
            run.LastEvent = "dispatch_failed";
            run.LastMessage = "Issue execution coordinator rejected the dispatch request.";
            run.LastEventAtUtc = nowUtc;
            runAttempt.Status = RunStatusNames.Failed;
            runAttempt.Error = "Issue execution coordinator rejected the dispatch request.";
            runAttempt.CompletedAtUtc = nowUtc;

            dbContext.RetryQueue.Add(new RetryQueueEntity
            {
                IssueId = issue.Id,
                IssueIdentifier = issue.Identifier,
                RunId = run.Id,
                OwnerInstanceId = instanceId,
                Attempt = run.CurrentRetryAttempt.Value,
                DueAtUtc = nowUtc.AddSeconds(10),
                DelayType = RetryDelayTypes.Backoff,
                Error = "failed to start issue execution coordinator",
                MaxBackoffMs = workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });

            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        return true;
    }

    private async Task RecordRemainingCapacityRefusalsAsync(
        IEnumerable<NormalizedIssue> issues,
        WorkflowDefinition workflowDefinition,
        IReadOnlyDictionary<string, string> reservedStateByIssueId,
        CancellationToken cancellationToken)
    {
        foreach (var issue in issues)
        {
            if (IsCandidateEligibleForAcquisitionSlo(issue, workflowDefinition) &&
                !reservedStateByIssueId.ContainsKey(issue.Id))
            {
                await RecordCandidateRefusalAsync(issue, "concurrency_limit", cancellationToken);
            }
        }
    }

    private async Task RecordCandidateRefusalAsync(
        NormalizedIssue issue,
        string reason,
        CancellationToken cancellationToken)
    {
        if (await ShouldRecordCandidateRefusalAsync(issue.Id, reason, cancellationToken))
        {
            AddIssueEvent(
                issue.Id,
                issue.Identifier,
                null,
                null,
                "claim_refused",
                LogLevel.Warning,
                $"Issue {issue.Identifier} was not claimable this tick: {reason}.");
        }

        if (await ShouldWarnDelayedAcquisitionAsync(issue.Id, cancellationToken))
        {
            AddIssueEvent(
                issue.Id,
                issue.Identifier,
                null,
                null,
                "candidate_acquisition_delayed",
                LogLevel.Warning,
                $"Issue {issue.Identifier} has remained unclaimed past the {AcquisitionDiagnosticThreshold.TotalMinutes:0}-minute acquisition diagnostic threshold. Latest refusal reason: {reason}.");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ShouldRecordCandidateRefusalAsync(
        string issueId,
        string reason,
        CancellationToken cancellationToken)
    {
        var cachedIssue = await dbContext.IssueCache.SingleOrDefaultAsync(
            issue => issue.IssueId == issueId,
            cancellationToken);
        if (cachedIssue?.EligibleSeenAtUtc is null)
        {
            return true;
        }

        var refusalEvents = await dbContext.EventLog
            .Where(entry =>
                entry.IssueId == issueId &&
                entry.EventName == "claim_refused")
            .Select(entry => new { entry.OccurredAtUtc, entry.Message })
            .ToListAsync(cancellationToken);

        return !refusalEvents.Any(entry =>
            entry.OccurredAtUtc >= cachedIssue.EligibleSeenAtUtc.Value &&
            entry.Message.Contains(reason, StringComparison.Ordinal));
    }

    private async Task<bool> ShouldWarnDelayedAcquisitionAsync(string issueId, CancellationToken cancellationToken)
    {
        var cachedIssue = await dbContext.IssueCache.SingleOrDefaultAsync(
            issue => issue.IssueId == issueId,
            cancellationToken);
        if (cachedIssue?.EligibleSeenAtUtc is null)
        {
            return false;
        }

        var eligibleSeenAtUtc = cachedIssue.EligibleSeenAtUtc.Value;
        var warningEvents = await dbContext.EventLog
            .Where(entry =>
                entry.IssueId == issueId &&
                entry.EventName == "candidate_acquisition_delayed")
            .Select(entry => entry.OccurredAtUtc)
            .ToListAsync(cancellationToken);
        var alreadyWarned = warningEvents.Any(occurredAtUtc => occurredAtUtc >= eligibleSeenAtUtc);

        return !alreadyWarned &&
               timeProvider.GetUtcNow() - cachedIssue.EligibleSeenAtUtc.Value >= AcquisitionDiagnosticThreshold;
    }

    private async Task ClearEligibleSeenAsync(string issueId, CancellationToken cancellationToken)
    {
        var cachedIssue = await dbContext.IssueCache.SingleOrDefaultAsync(
            issue => issue.IssueId == issueId,
            cancellationToken);
        if (cachedIssue is not null)
        {
            cachedIssue.EligibleSeenAtUtc = null;
        }
    }

    private bool IsDispatchEligible(
        NormalizedIssue issue,
        WorkflowDefinition workflowDefinition,
        HashSet<string> runningIssueIds,
        IReadOnlyDictionary<string, int> countsByState)
    {
        if (string.IsNullOrWhiteSpace(issue.Id) ||
            string.IsNullOrWhiteSpace(issue.Identifier) ||
            string.IsNullOrWhiteSpace(issue.Title) ||
            string.IsNullOrWhiteSpace(issue.State))
        {
            return false;
        }

        if (runningIssueIds.Contains(issue.Id))
        {
            return false;
        }

        if (MatchesTerminalState(issue.State, workflowDefinition.Runtime.Tracker.TerminalStates))
        {
            return false;
        }

        if (!IssueStateMatcher.MatchesConfiguredActiveState(issue.State, workflowDefinition.Runtime.Tracker.ActiveStates))
        {
            return false;
        }

        if (!HasStateSlot(issue.State, workflowDefinition, countsByState))
        {
            return false;
        }

        return PassesBlockerRule(issue, workflowDefinition);
    }

    private static bool IsCandidateEligibleForAcquisitionSlo(NormalizedIssue issue, WorkflowDefinition workflowDefinition)
    {
        return !string.IsNullOrWhiteSpace(issue.Id) &&
               !string.IsNullOrWhiteSpace(issue.Identifier) &&
               !string.IsNullOrWhiteSpace(issue.Title) &&
               !string.IsNullOrWhiteSpace(issue.State) &&
               !MatchesTerminalState(issue.State, workflowDefinition.Runtime.Tracker.TerminalStates) &&
               IssueStateMatcher.MatchesConfiguredActiveState(issue.State, workflowDefinition.Runtime.Tracker.ActiveStates) &&
               PassesBlockerRule(issue, workflowDefinition);
    }

    // The same checks as DetermineIneligibilityReason minus capacity. Kept separate
    // and explicit because the distinction is the whole of ADCP#25: everything this
    // reports is a durable "no" that should release the reservation, whereas a
    // capacity refusal is a "not yet" that must reschedule it.
    private static string? DetermineIneligibilityReasonIgnoringCapacity(
        NormalizedIssue issue,
        WorkflowDefinition workflowDefinition,
        HashSet<string> activeIssueIds)
    {
        if (string.IsNullOrWhiteSpace(issue.Id) ||
            string.IsNullOrWhiteSpace(issue.Identifier) ||
            string.IsNullOrWhiteSpace(issue.Title) ||
            string.IsNullOrWhiteSpace(issue.State))
        {
            return "invalid_candidate_payload";
        }

        if (activeIssueIds.Contains(issue.Id))
        {
            return "already_running";
        }

        if (MatchesTerminalState(issue.State, workflowDefinition.Runtime.Tracker.TerminalStates))
        {
            return "terminal_state";
        }

        if (!IssueStateMatcher.MatchesConfiguredActiveState(issue.State, workflowDefinition.Runtime.Tracker.ActiveStates))
        {
            return "non_eligible_label_state";
        }

        if (!PassesBlockerRule(issue, workflowDefinition))
        {
            return "active_blocker";
        }

        return null;
    }

    private static string DetermineIneligibilityReason(
        NormalizedIssue issue,
        WorkflowDefinition workflowDefinition,
        HashSet<string> runningIssueIds,
        IReadOnlyDictionary<string, int> countsByState)
    {
        if (string.IsNullOrWhiteSpace(issue.Id) ||
            string.IsNullOrWhiteSpace(issue.Identifier) ||
            string.IsNullOrWhiteSpace(issue.Title) ||
            string.IsNullOrWhiteSpace(issue.State))
        {
            return "invalid_candidate_payload";
        }

        if (runningIssueIds.Contains(issue.Id))
        {
            return "already_running";
        }

        if (MatchesTerminalState(issue.State, workflowDefinition.Runtime.Tracker.TerminalStates))
        {
            return "terminal_state";
        }

        if (!IssueStateMatcher.MatchesConfiguredActiveState(issue.State, workflowDefinition.Runtime.Tracker.ActiveStates))
        {
            return "non_eligible_label_state";
        }

        if (!HasStateSlot(issue.State, workflowDefinition, countsByState))
        {
            return "concurrency_limit";
        }

        if (!PassesBlockerRule(issue, workflowDefinition))
        {
            return "active_blocker";
        }

        return "not_dispatch_eligible";
    }

    private static bool PassesBlockerRule(NormalizedIssue issue, WorkflowDefinition workflowDefinition)
    {
        if (!NormalizeStateKey(issue.State).Equals("todo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return issue.BlockedBy.All(blocker =>
            string.IsNullOrWhiteSpace(blocker.State) ||
            MatchesTerminalState(blocker.State, workflowDefinition.Runtime.Tracker.TerminalStates));
    }

    private static bool HasGlobalSlot(WorkflowDefinition workflowDefinition, int runningCount)
    {
        return runningCount < workflowDefinition.Runtime.Agent.MaxConcurrentAgents;
    }

    private static bool HasStateSlot(
        string state,
        WorkflowDefinition workflowDefinition,
        IReadOnlyDictionary<string, int> countsByState)
    {
        var stateKey = NormalizeStateKey(state);
        var currentCount = countsByState.GetValueOrDefault(stateKey);
        if (!workflowDefinition.Runtime.Agent.MaxConcurrentAgentsByState.TryGetValue(stateKey, out var limit))
        {
            limit = workflowDefinition.Runtime.Agent.MaxConcurrentAgents;
        }

        return currentCount < limit;
    }

    private async Task CompleteSuccessfulDispatchAsync(
        RetryQueueEntity retryEntry,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var retryingRuns = await dbContext.Runs
            .Where(runEntity =>
                runEntity.IssueId == retryEntry.IssueId &&
                runEntity.Status == RunStatusNames.Retrying)
            .ToListAsync(cancellationToken);
        var run = retryingRuns
            .OrderByDescending(runEntity => runEntity.StartedAtUtc)
            .FirstOrDefault();

        if (run is not null)
        {
            run.Status = RunStatusNames.Succeeded;
            run.CurrentRetryAttempt = null;
            run.CompletedAtUtc = nowUtc;
            run.LastEvent = "run_completed";
            run.LastMessage = "Successful bounded execution completed; implicit continuation suppressed.";
            run.LastEventAtUtc = nowUtc;
        }

        dbContext.RetryQueue.Remove(retryEntry);
        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = retryEntry.IssueId,
            IssueIdentifier = retryEntry.IssueIdentifier,
            RunId = retryEntry.RunId,
            EventName = "implicit_continuation_suppressed",
            Level = LogLevel.Information.ToString(),
            Message = "A successful bounded execution was finalized without starting another implementation run.",
            OccurredAtUtc = nowUtc
        });

        await coordinationStore.ReleaseIssueClaimAsync(
            retryEntry.IssueId,
            instanceId,
            RunStatusNames.Succeeded,
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleMissingRetryCandidateAsync(
        RetryQueueEntity retryEntry,
        TrackerQuery query,
        WorkflowDefinition workflowDefinition,
        string instanceId,
        CancellationToken cancellationToken)
    {
        IssueStateSnapshot? snapshot;
        try
        {
            var snapshots = await trackerClient.FetchIssueStatesByIdsAsync(query, [retryEntry.IssueId], cancellationToken);
            snapshot = snapshots.FirstOrDefault(item =>
                string.Equals(item.Id, retryEntry.IssueId, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed: without a fresh read of the issue we cannot distinguish
            // "work finished" from "work vanished", so keep the reservation.
            logger.LogWarning(
                ex,
                "Could not reload issue {IssueIdentifier} to validate a missing retry candidate. Keeping the retry reservation.",
                retryEntry.IssueIdentifier);
            await RescheduleRetryAsync(
                retryEntry,
                instanceId,
                "retry candidate reload failed; failing closed",
                workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                cancellationToken);
            return;
        }

        if (snapshot is not null &&
            MatchesTerminalState(snapshot.State, workflowDefinition.Runtime.Tracker.TerminalStates))
        {
            await ReleaseRetryReservationAsync(
                retryEntry.IssueId,
                retryEntry.IssueIdentifier,
                instanceId,
                "issue reached a terminal state",
                cancellationToken);
            return;
        }

        var retryingRun = await FindLatestRunWithStatusAsync(retryEntry.IssueId, RunStatusNames.Retrying, cancellationToken);

        if (snapshot is null)
        {
            await EscalateRunToCommandCenterAsync(
                retryingRun,
                retryEntry.IssueId,
                retryEntry.IssueIdentifier,
                instanceId,
                $"Issue {retryEntry.IssueIdentifier} has a due retry reservation but could not be reloaded by id from the tracker.",
                cancellationToken);
            return;
        }

        if (retryingRun is not null && await HasDurableUnfinishedWorkEvidenceAsync(retryingRun, cancellationToken))
        {
            await EscalateRunToCommandCenterAsync(
                retryingRun,
                retryEntry.IssueId,
                retryEntry.IssueIdentifier,
                instanceId,
                $"Issue {retryEntry.IssueIdentifier} is still open with durable evidence of unfinished work, " +
                "but it is no longer dispatchable (missing from the candidate query). Refusing to silently release the run.",
                cancellationToken);
            return;
        }

        await ReleaseRetryReservationAsync(
            retryEntry.IssueId,
            retryEntry.IssueIdentifier,
            instanceId,
            "retry candidate missing",
            cancellationToken);
    }

    private async Task<RunEntity?> FindLatestRunWithStatusAsync(
        string issueId,
        string status,
        CancellationToken cancellationToken)
    {
        var runs = await dbContext.Runs
            .Where(runEntity => runEntity.IssueId == issueId && runEntity.Status == status)
            .ToListAsync(cancellationToken);

        return runs
            .OrderByDescending(runEntity => runEntity.StartedAtUtc)
            .FirstOrDefault();
    }

    private async Task<bool> HasDurableUnfinishedWorkEvidenceAsync(RunEntity run, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(run.SessionId) || run.TurnCount > 0)
        {
            return true;
        }

        return await HasCachedPullRequestEvidenceAsync(run.IssueId, cancellationToken);
    }

    private async Task<bool> HasCachedPullRequestEvidenceAsync(string issueId, CancellationToken cancellationToken)
    {
        var cachedIssue = await dbContext.IssueCache.SingleOrDefaultAsync(
            entry => entry.IssueId == issueId,
            cancellationToken);

        return cachedIssue is not null &&
               !string.IsNullOrWhiteSpace(cachedIssue.PullRequestsJson) &&
               !string.Equals(cachedIssue.PullRequestsJson.Trim(), "[]", StringComparison.Ordinal);
    }

    private async Task EscalateRunToCommandCenterAsync(
        RunEntity? run,
        string issueId,
        string issueIdentifier,
        string instanceId,
        string reason,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();

        if (run is not null)
        {
            run.Status = RunStatusNames.NeedsCommandCenter;
            run.CompletedAtUtc ??= nowUtc;
            run.LastEvent = "needs_command_center";
            run.LastMessage = reason;
            run.LastEventAtUtc = nowUtc;
        }

        var retryEntry = await dbContext.RetryQueue.SingleOrDefaultAsync(
            retry => retry.IssueId == issueId,
            cancellationToken);
        if (retryEntry is not null)
        {
            dbContext.RetryQueue.Remove(retryEntry);
        }

        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = issueId,
            IssueIdentifier = issueIdentifier,
            RunId = run?.Id,
            EventName = "needs_command_center",
            Level = LogLevel.Error.ToString(),
            Message = reason,
            OccurredAtUtc = nowUtc
        });

        await coordinationStore.ReleaseIssueClaimAsync(
            issueId,
            instanceId,
            RunStatusNames.NeedsCommandCenter,
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogError(
            "Issue {IssueIdentifier} needs Command Center attention: {Reason}",
            issueIdentifier,
            reason);
    }

    private async Task ReconcileAbandonedReleasedRunsAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        IReadOnlyDictionary<string, NormalizedIssue> candidatesById,
        CancellationToken cancellationToken)
    {
        // The #88 failure signature: execution started (a Codex session exists), the run
        // was finalized as released_ineligible, the issue is still open on GitHub, the
        // execution label is gone (absent from the candidate query), and no live run
        // exists. That is abandoned work and must surface to the Command Center instead
        // of idling silently.
        var releasedRuns = await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.ReleasedIneligible && run.SessionId != null)
            .ToListAsync(cancellationToken);
        if (releasedRuns.Count == 0)
        {
            return;
        }

        var issueIds = releasedRuns
            .Select(run => run.IssueId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allRunsForIssues = await dbContext.Runs
            .Where(run => issueIds.Contains(run.IssueId))
            .ToListAsync(cancellationToken);

        var suspects = new List<RunEntity>();
        foreach (var issueRuns in allRunsForIssues.GroupBy(run => run.IssueId, StringComparer.OrdinalIgnoreCase))
        {
            if (issueRuns.Any(run => string.Equals(run.Status, RunStatusNames.NeedsCommandCenter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var latestRun = issueRuns.OrderByDescending(run => run.StartedAtUtc).First();
            if (!string.Equals(latestRun.Status, RunStatusNames.ReleasedIneligible, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(latestRun.SessionId))
            {
                continue;
            }

            if (candidatesById.ContainsKey(latestRun.IssueId))
            {
                // Still dispatchable; the ordinary dispatch path (with the phase-aware
                // redispatch guard) governs this issue.
                continue;
            }

            suspects.Add(latestRun);
        }

        if (suspects.Count == 0)
        {
            return;
        }

        var suspectIds = suspects.Select(run => run.IssueId).ToList();
        var cachedIssues = await dbContext.IssueCache
            .Where(entry => suspectIds.Contains(entry.IssueId))
            .ToListAsync(cancellationToken);
        var cachedById = cachedIssues.ToDictionary(entry => entry.IssueId, StringComparer.OrdinalIgnoreCase);

        var toVerify = suspects
            .Where(run =>
                !cachedById.TryGetValue(run.IssueId, out var cached) ||
                !MatchesTerminalState(cached.State, workflowDefinition.Runtime.Tracker.TerminalStates))
            .ToList();
        if (toVerify.Count == 0)
        {
            return;
        }

        IReadOnlyList<IssueStateSnapshot> snapshots;
        try
        {
            snapshots = await trackerClient.FetchIssueStatesByIdsAsync(
                query,
                toVerify.Select(run => run.IssueId).ToList(),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Abandoned-work reconciliation could not reload issue states; will retry next tick.");
            return;
        }

        var snapshotById = snapshots.ToDictionary(snapshot => snapshot.Id, StringComparer.OrdinalIgnoreCase);
        var nowUtc = timeProvider.GetUtcNow();
        var escalated = false;
        foreach (var run in toVerify)
        {
            var snapshotFound = snapshotById.TryGetValue(run.IssueId, out var snapshot);
            if (snapshotFound && MatchesTerminalState(snapshot!.State, workflowDefinition.Runtime.Tracker.TerminalStates))
            {
                continue;
            }

            // A suspect the tracker reload cannot account for must not be skipped
            // silently forever: without a fresh read we cannot distinguish "work
            // finished" from "work vanished", so fail closed and surface it.
            var reason = snapshotFound
                ? $"Abandoned work detected for issue {run.IssueIdentifier}: the issue is still open, execution had " +
                  "already started, but the issue is no longer dispatchable (execution label removed) and no live run exists."
                : $"Abandoned work detected for issue {run.IssueIdentifier}: execution had already started and the run " +
                  "was released, but the issue could not be reloaded by id from the tracker. Refusing to silently skip it.";

            run.Status = RunStatusNames.NeedsCommandCenter;
            run.CompletedAtUtc ??= nowUtc;
            run.LastEvent = "needs_command_center";
            run.LastMessage = reason;
            run.LastEventAtUtc = nowUtc;

            dbContext.EventLog.Add(new EventLogEntity
            {
                IssueId = run.IssueId,
                IssueIdentifier = run.IssueIdentifier,
                RunId = run.Id,
                EventName = "needs_command_center",
                Level = LogLevel.Error.ToString(),
                Message = reason,
                OccurredAtUtc = nowUtc
            });

            logger.LogError(
                "Issue {IssueIdentifier} needs Command Center attention: {Reason}",
                run.IssueIdentifier,
                reason);
            escalated = true;
        }

        if (escalated)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ReleaseRetryReservationAsync(
        string issueId,
        string issueIdentifier,
        string instanceId,
        string reason,
        CancellationToken cancellationToken)
    {
        var run = await dbContext.Runs
            .Where(runEntity => runEntity.IssueId == issueId && runEntity.Status == RunStatusNames.Retrying)
            .ToListAsync(cancellationToken);

        var latestRun = run
            .OrderByDescending(runEntity => runEntity.StartedAtUtc)
            .FirstOrDefault();

        if (latestRun is not null)
        {
            latestRun.Status = RunStatusNames.ReleasedIneligible;
            latestRun.CompletedAtUtc = timeProvider.GetUtcNow();
            latestRun.LastEvent = "claim_released";
            latestRun.LastMessage = reason;
            latestRun.LastEventAtUtc = timeProvider.GetUtcNow();
        }

        var retryEntry = await dbContext.RetryQueue.SingleOrDefaultAsync(
            retry => retry.IssueId == issueId,
            cancellationToken);
        if (retryEntry is not null)
        {
            dbContext.RetryQueue.Remove(retryEntry);
        }

        await coordinationStore.ReleaseIssueClaimAsync(issueId, instanceId, RunStatusNames.ReleasedIneligible, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RescheduleRetryAsync(
        RetryQueueEntity retryEntry,
        string instanceId,
        string error,
        int maxRetryBackoffMs,
        CancellationToken cancellationToken,
        bool advanceAttempt = true)
    {
        var nextAttempt = advanceAttempt ? retryEntry.Attempt + 1 : retryEntry.Attempt;
        retryEntry.OwnerInstanceId = instanceId;
        retryEntry.Attempt = nextAttempt;
        retryEntry.Error = error;
        retryEntry.DelayType = RetryDelayTypes.Backoff;
        retryEntry.MaxBackoffMs = maxRetryBackoffMs;
        retryEntry.DueAtUtc = timeProvider.GetUtcNow().AddMilliseconds(ComputeBackoffMs(nextAttempt, maxRetryBackoffMs));
        retryEntry.UpdatedAtUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static int ComputeBackoffMs(int attempt, int maxRetryBackoffMs)
    {
        var exponent = Math.Max(attempt - 1, 0);
        var delayMs = 10_000 * (int)Math.Pow(2, exponent);
        return Math.Min(delayMs, maxRetryBackoffMs);
    }

    private static string NormalizeStateKey(string state) => state.Trim().ToLowerInvariant();

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

    private static IEnumerable<NormalizedIssue> OrderIssuesForDispatch(IEnumerable<NormalizedIssue> issues)
    {
        return issues
            .OrderBy(issue => issue.Priority.HasValue ? 0 : 1)
            .ThenBy(issue => issue.Priority ?? int.MaxValue)
            .ThenBy(issue => issue.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(issue => issue.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.Identifier, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.Id, StringComparer.OrdinalIgnoreCase);
    }
}
