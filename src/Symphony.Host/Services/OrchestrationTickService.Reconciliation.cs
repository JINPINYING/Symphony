using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed partial class OrchestrationTickService
{
    private async Task RecoverOrphanedStateAsync(
        string instanceId,
        WorkflowDefinition workflowDefinition,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var leaseName = ResolveLeaseName();
        var liveLeaseOwners = (await dbContext.InstanceLeases
            .Where(lease => lease.LeaseName == leaseName)
            .ToListAsync(cancellationToken))
            .Where(lease => lease.ExpiresAtUtc > nowUtc)
            .Select(lease => lease.OwnerInstanceId)
            .ToList();
        var liveOwnerSet = new HashSet<string>(liveLeaseOwners, StringComparer.OrdinalIgnoreCase);

        var orphanedRuns = await dbContext.Runs
            .Where(run =>
                (run.Status == RunStatusNames.Running || run.Status == RunStatusNames.Retrying) &&
                run.OwnerInstanceId != instanceId)
            .ToListAsync(cancellationToken);

        foreach (var run in orphanedRuns)
        {
            if (liveOwnerSet.Contains(run.OwnerInstanceId))
            {
                continue;
            }

            var claim = await dbContext.DispatchClaims.SingleOrDefaultAsync(
                entity => entity.IssueId == run.IssueId && entity.Status == "active",
                cancellationToken);

            if (claim is not null &&
                !claim.ClaimedByInstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase) &&
                liveOwnerSet.Contains(claim.ClaimedByInstanceId))
            {
                continue;
            }

            if (claim is not null)
            {
                claim.ClaimedByInstanceId = instanceId;
                claim.UpdatedAtUtc = nowUtc;
            }

            if (run.Status == RunStatusNames.Retrying)
            {
                run.OwnerInstanceId = instanceId;
                var retry = await dbContext.RetryQueue.SingleOrDefaultAsync(
                    retryEntity => retryEntity.IssueId == run.IssueId,
                    cancellationToken);
                if (retry is not null)
                {
                    retry.OwnerInstanceId = instanceId;
                    retry.UpdatedAtUtc = nowUtc;
                }

                continue;
            }

            var activeAttempt = await dbContext.RunAttempts
                .Where(attempt => attempt.RunId == run.Id && attempt.CompletedAtUtc == null)
                .ToListAsync(cancellationToken);

            var latestAttempt = activeAttempt
                .OrderByDescending(attempt => attempt.StartedAtUtc)
                .FirstOrDefault();

            if (latestAttempt is not null)
            {
                latestAttempt.Status = RunStatusNames.Failed;
                latestAttempt.Error = "recovered after instance takeover";
                latestAttempt.CompletedAtUtc = nowUtc;
            }

            run.OwnerInstanceId = instanceId;
            run.Status = RunStatusNames.Retrying;
            run.CurrentRetryAttempt = (run.CurrentRetryAttempt ?? 0) + 1;
            run.LastEvent = "orphaned_run_recovered";
            run.LastMessage = "Recovered after instance takeover.";
            run.LastEventAtUtc = nowUtc;

            await UpsertRetryEntryAsync(
                run,
                instanceId,
                run.CurrentRetryAttempt.Value,
                RetryDelayTypes.Backoff,
                "recovered after instance takeover",
                nowUtc.AddSeconds(1),
                workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // A run that can never reach a terminal status is a leak, whichever code path
    // forgot it. ADCP#23 found one such path - the startup fence refusing every
    // claim for a reservation it had itself scheduled - but the shape is general,
    // and two more routes into it already exist: a run in 'retrying' whose
    // reservation row is gone, and a phase-owned run in 'retrying', which the
    // retry loop skips (the phase orchestrator owns it) while the phase
    // orchestrator only reacts to running/succeeded/failed/timed_out/stalled.
    //
    // This is the net under all of them. A run parked in 'retrying' is never
    // executing - that status is written only once the agent process is gone - so
    // if its reservation stays due and undispatched, nothing is coming for it,
    // and it holds an agent slot the whole time it waits.
    private static readonly TimeSpan WedgedRetryFloor = TimeSpan.FromMinutes(15);

    // Overdue in the database is not enough on its own: after the service has been
    // stopped for an hour, every pending reservation is overdue through no fault of
    // its own, and the very next tick would dispatch it. What identifies a wedge is
    // that THIS instance has watched the reservation stay due across a full grace
    // period of its own ticks and never managed to act on it. In-memory is the
    // right lifetime for that: a restart genuinely should start the clock again.
    private readonly Dictionary<string, DateTimeOffset> firstObservedOverdueRetries = new(StringComparer.OrdinalIgnoreCase);

    private async Task ReconcileWedgedRetriesAsync(
        WorkflowDefinition workflowDefinition,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();

        var retryingRuns = await dbContext.Runs
            .Where(run =>
                run.Status == RunStatusNames.Retrying &&
                run.CompletedAtUtc == null &&
                run.OwnerInstanceId == instanceId)
            .ToListAsync(cancellationToken);

        var stillRetrying = new HashSet<string>(retryingRuns.Select(run => run.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var runId in firstObservedOverdueRetries.Keys.Where(id => !stillRetrying.Contains(id)).ToList())
        {
            firstObservedOverdueRetries.Remove(runId);
        }

        if (retryingRuns.Count == 0)
        {
            return;
        }

        // Generous on purpose. A retry that is merely waiting its turn has its due
        // time pushed forward each tick, so it never accumulates overdue time at all;
        // only a reservation nobody can act on does.
        var backoffCeiling = TimeSpan.FromMilliseconds(
            Math.Max(workflowDefinition.Runtime.Agent.MaxRetryBackoffMs, 0)) * 4;
        var grace = backoffCeiling > WedgedRetryFloor ? backoffCeiling : WedgedRetryFloor;

        var issueIds = retryingRuns.Select(run => run.IssueId).ToList();
        var reservations = (await dbContext.RetryQueue
                .Where(retry => issueIds.Contains(retry.IssueId))
                .ToListAsync(cancellationToken))
            .ToDictionary(retry => retry.IssueId, StringComparer.OrdinalIgnoreCase);

        foreach (var run in retryingRuns)
        {
            var hasReservation = reservations.TryGetValue(run.IssueId, out var reservation);

            // A reservation that is not due yet is simply waiting, and a run with no
            // reservation at all has nothing that could ever fire for it.
            if (hasReservation && reservation!.DueAtUtc > nowUtc)
            {
                firstObservedOverdueRetries.Remove(run.Id);
                continue;
            }

            if (!firstObservedOverdueRetries.TryGetValue(run.Id, out var firstObservedUtc))
            {
                firstObservedOverdueRetries[run.Id] = nowUtc;
                continue;
            }

            var stuckFor = nowUtc - firstObservedUtc;
            if (stuckFor <= grace)
            {
                continue;
            }

            var reason = hasReservation
                ? $"Run for issue {run.IssueIdentifier} has been parked in '{RunStatusNames.Retrying}' with a retry " +
                  $"reservation that has been due since {reservation!.DueAtUtc:O} and has stayed undispatched for " +
                  $"{(int)stuckFor.TotalMinutes} minutes, past the {(int)grace.TotalMinutes}-minute wedge threshold. " +
                  "No dispatch path is able to act on it, so it is ended here rather than left holding an agent slot " +
                  $"indefinitely. Latest recorded cause: {reservation.Error ?? run.LastMessage ?? "unknown"}."
                : $"Run for issue {run.IssueIdentifier} has been parked in '{RunStatusNames.Retrying}' with no retry " +
                  $"reservation at all for {(int)stuckFor.TotalMinutes} minutes, so nothing will ever re-dispatch it. " +
                  $"Latest recorded cause: {run.LastMessage ?? "unknown"}.";

            dbContext.EventLog.Add(new EventLogEntity
            {
                IssueId = run.IssueId,
                IssueIdentifier = run.IssueIdentifier,
                RunId = run.Id,
                EventName = "wedged_retry_reconciled",
                Level = LogLevel.Warning.ToString(),
                Message = reason,
                OccurredAtUtc = nowUtc
            });

            firstObservedOverdueRetries.Remove(run.Id);
            await EscalateRunToCommandCenterAsync(
                run,
                run.IssueId,
                run.IssueIdentifier,
                instanceId,
                reason,
                cancellationToken);
        }
    }

    private async Task ReconcileRunningIssuesAsync(
        WorkflowDefinition workflowDefinition,
        string? apiKey,
        WorkflowLoadException? preflightError,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var runningIssues = await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.Running)
            .ToListAsync(cancellationToken);

        if (runningIssues.Count == 0)
        {
            return;
        }

        await ReconcileStalledRunsAsync(runningIssues, workflowDefinition, instanceId, cancellationToken);

        if (preflightError is not null || string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        var refreshedStates = await TryFetchIssueStatesByIdsAsync(
            workflowDefinition,
            apiKey,
            runningIssues.Select(run => run.IssueId).ToList(),
            "Running issue reconciliation failed; active runs will continue.",
            cancellationToken,
            runningIssues
                .GroupBy(run => run.IssueId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Repository, StringComparer.Ordinal),
            IssueIdentifierMap.From(runningIssues, run => run.IssueId, run => run.IssueIdentifier));
        if (refreshedStates is null)
        {
            return;
        }

        var refreshedById = refreshedStates.ToDictionary(state => state.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var run in runningIssues)
        {
            if (!refreshedById.TryGetValue(run.IssueId, out var refreshedState))
            {
                continue;
            }

            run.State = refreshedState.State;

            if (MatchesTerminalState(refreshedState.State, workflowDefinition.Runtime.Tracker.TerminalStates))
            {
                await RequestRunStopAsync(
                    run,
                    RunStopReasons.Terminal,
                    cleanupWorkspace: true,
                    workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    instanceId,
                    cancellationToken);
                continue;
            }

            if (!IssueStateMatcher.MatchesConfiguredActiveState(refreshedState.State, workflowDefinition.Runtime.Tracker.ActiveStates))
            {
                await RequestRunStopAsync(
                    run,
                    RunStopReasons.Inactive,
                    cleanupWorkspace: false,
                    workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    instanceId,
                    cancellationToken);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcileStalledRunsAsync(
        IReadOnlyList<RunEntity> runningIssues,
        WorkflowDefinition workflowDefinition,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();

        foreach (var run in runningIssues.Where(run => run.OwnerInstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)))
        {
            // M4: stall windows are per runner — a slower implementer (claude)
            // must not trip the codex-tuned inactivity rule.
            var stallTimeoutMs = string.Equals(run.Runner, AgentRunnerNames.Claude, StringComparison.OrdinalIgnoreCase)
                ? workflowDefinition.Runtime.Claude.StallTimeoutMs
                : workflowDefinition.Runtime.Codex.StallTimeoutMs;
            var lastActivity = run.LastEventAtUtc ?? run.StartedAtUtc;
            var inactivityStalled = stallTimeoutMs > 0 &&
                                    (nowUtc - lastActivity).TotalMilliseconds > stallTimeoutMs;
            var continuousTurnBudgetExceeded = HasExceededContinuousTurnBudget(run, workflowDefinition);

            if (!inactivityStalled && !continuousTurnBudgetExceeded)
            {
                continue;
            }

            if (continuousTurnBudgetExceeded && !inactivityStalled)
            {
                logger.LogWarning(
                    "Run for issue {IssueIdentifier} on runner {Runner} exceeded the continuous turn safety budget with {TurnCount} turns while agent activity remained live. Requesting a bounded stalled retry.",
                    run.IssueIdentifier,
                    run.Runner,
                    run.TurnCount);
            }

            await RequestRunStopAsync(
                run,
                RunStopReasons.Stalled,
                cleanupWorkspace: false,
                workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                instanceId,
                cancellationToken);
        }
    }

    private static bool HasExceededContinuousTurnBudget(RunEntity run, WorkflowDefinition workflowDefinition)
    {
        var maxTurnsPerWorker = Math.Max(workflowDefinition.Runtime.Agent.MaxTurns, 1);
        var maxContinuousTurns = (long)maxTurnsPerWorker * 2;
        return run.TurnCount >= maxContinuousTurns;
    }

    private async Task RequestRunStopAsync(
        RunEntity run,
        string stopReason,
        bool cleanupWorkspace,
        int maxRetryBackoffMs,
        string instanceId,
        CancellationToken cancellationToken)
    {
        run.RequestedStopReason = stopReason;
        run.CleanupWorkspaceOnStop = cleanupWorkspace;
        run.LastEvent = "stop_requested";
        run.LastMessage = stopReason;
        run.LastEventAtUtc = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        var stopRequested = await issueExecutionCoordinator.TryStopAsync(run.IssueId, cancellationToken);
        if (stopRequested)
        {
            return;
        }

        var nowUtc = timeProvider.GetUtcNow();
        var activeAttempt = await dbContext.RunAttempts
            .Where(attempt => attempt.RunId == run.Id && attempt.CompletedAtUtc == null)
            .ToListAsync(cancellationToken);

        var latestAttempt = activeAttempt
            .OrderByDescending(attempt => attempt.StartedAtUtc)
            .FirstOrDefault();

        if (latestAttempt is not null)
        {
            latestAttempt.Status = stopReason switch
            {
                RunStopReasons.Stalled => RunStatusNames.Stalled,
                RunStopReasons.StartupExhausted => RunStatusNames.NeedsCommandCenter,
                _ => RunStatusNames.CanceledByReconciliation
            };
            latestAttempt.Error = stopReason;
            latestAttempt.CompletedAtUtc = nowUtc;
        }

        if (stopReason == RunStopReasons.StartupExhausted)
        {
            // Terminal, and deliberately without a retry reservation: the guard has
            // spent the budget, so a reservation here would only be fenced forever
            // (ADCP#23). EscalateRunToCommandCenterAsync drops the reservation,
            // releases the claim and saves.
            await EscalateRunToCommandCenterAsync(
                run,
                run.IssueId,
                run.IssueIdentifier,
                instanceId,
                run.LastMessage ?? "Startup retry budget exhausted without an agent session.",
                cancellationToken);
        }
        else if (stopReason == RunStopReasons.Stalled)
        {
            run.Status = RunStatusNames.Retrying;
            run.CurrentRetryAttempt = (run.CurrentRetryAttempt ?? 0) + 1;
            await UpsertRetryEntryAsync(
                run,
                instanceId,
                run.CurrentRetryAttempt.Value,
                RetryDelayTypes.Backoff,
                "stall timeout exceeded",
                nowUtc.AddMilliseconds(ComputeBackoffMs(run.CurrentRetryAttempt.Value, maxRetryBackoffMs)),
                maxRetryBackoffMs,
                cancellationToken);
        }
        else
        {
            run.Status = RunStatusNames.CanceledByReconciliation;
            run.CompletedAtUtc = nowUtc;

            var retryEntry = await dbContext.RetryQueue.SingleOrDefaultAsync(
                retry => retry.IssueId == run.IssueId,
                cancellationToken);
            if (retryEntry is not null)
            {
                dbContext.RetryQueue.Remove(retryEntry);
            }

            await coordinationStore.ReleaseIssueClaimAsync(
                run.IssueId,
                instanceId,
                RunStatusNames.CanceledByReconciliation,
                cancellationToken);

            if (cleanupWorkspace)
            {
                await CleanupWorkspaceWithoutLiveRunAsync(run, cancellationToken);
            }
        }
    }

    private async Task UpsertRetryEntryAsync(
        RunEntity run,
        string instanceId,
        int attempt,
        string delayType,
        string error,
        DateTimeOffset dueAtUtc,
        int maxRetryBackoffMs,
        CancellationToken cancellationToken)
    {
        var retryEntry = await dbContext.RetryQueue.SingleOrDefaultAsync(
            entry => entry.IssueId == run.IssueId,
            cancellationToken);
        if (retryEntry is null)
        {
            dbContext.RetryQueue.Add(new RetryQueueEntity
            {
                IssueId = run.IssueId,
                IssueIdentifier = run.IssueIdentifier,
                RunId = run.Id,
                OwnerInstanceId = instanceId,
                Attempt = attempt,
                DueAtUtc = dueAtUtc,
                DelayType = delayType,
                Error = error,
                MaxBackoffMs = maxRetryBackoffMs,
                CreatedAtUtc = timeProvider.GetUtcNow(),
                UpdatedAtUtc = timeProvider.GetUtcNow()
            });
        }
        else
        {
            retryEntry.OwnerInstanceId = instanceId;
            retryEntry.Attempt = attempt;
            retryEntry.DueAtUtc = dueAtUtc;
            retryEntry.DelayType = delayType;
            retryEntry.Error = error;
            retryEntry.MaxBackoffMs = maxRetryBackoffMs;
            retryEntry.UpdatedAtUtc = timeProvider.GetUtcNow();
        }
    }

    private async Task CleanupWorkspaceWithoutLiveRunAsync(RunEntity run, CancellationToken cancellationToken)
    {
        var workflowDefinition = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
        try
        {
            var repository = ResolveRepository(workflowDefinition, run.Repository);
            await workspaceManager.CleanupIssueWorkspaceAsync(
                new WorkspaceCleanupRequest(
                    run.IssueIdentifier,
                    workflowDefinition.Runtime.Workspace.Root,
                    repository.SharedClonePath,
                    repository.WorktreesRoot,
                    workflowDefinition.Runtime.Hooks.BeforeRemove,
                    workflowDefinition.Runtime.Hooks.TimeoutMs),
                cancellationToken);

            await UpdateWorkspaceCleanupRecordAsync(
                new NormalizedIssue(
                    run.IssueId,
                    run.IssueIdentifier,
                    run.IssueIdentifier,
                    null,
                    null,
                    run.State,
                    null,
                    null,
                    null,
                    [],
                    [],
                    [],
                    null,
                    null,
                    run.Repository),
                RunStopReasons.Terminal,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Terminal cleanup failed for issue {IssueIdentifier}.", run.IssueIdentifier);
        }
    }
}
