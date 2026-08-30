using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed partial class OrchestrationTickService
{
    private async Task DispatchCandidatesAsync(
        WorkflowDefinition workflowDefinition,
        string apiKey,
        string instanceId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NormalizedIssue> issues;
        var query = BuildTrackerQuery(workflowDefinition, apiKey);
        try
        {
            issues = await trackerClient.FetchCandidateIssuesAsync(query, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Candidate fetch failed for {Owner}/{Repo}. Dispatch will be skipped this tick.",
                query.Owner,
                query.Repo);
            return;
        }

        await UpsertIssueCacheAsync(issues, cancellationToken);

        var runningIssues = await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.Running)
            .ToListAsync(cancellationToken);

        // Issues finalized during this tick (legacy continuation drain) must not be
        // re-dispatched in the same tick; the next tick's phase-aware checks govern them.
        var finalizedThisTick = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var countsByState = runningIssues
            .GroupBy(run => NormalizeStateKey(run.State), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var runningIssueIds = new HashSet<string>(runningIssues.Select(run => run.IssueId), StringComparer.OrdinalIgnoreCase);

        var dueRetries = await dbContext.RetryQueue
            .FromSqlInterpolated($"""
                SELECT *
                FROM retry_queue
                WHERE DueAtUtc <= {timeProvider.GetUtcNow()}
                ORDER BY DueAtUtc
                """)
            .ToListAsync(cancellationToken);

        var candidatesById = issues.ToDictionary(issue => issue.Id, StringComparer.OrdinalIgnoreCase);

        await ReconcileAbandonedReleasedRunsAsync(workflowDefinition, query, candidatesById, cancellationToken);

        foreach (var retryEntry in dueRetries)
        {
            if (string.Equals(retryEntry.DelayType, RetryDelayTypes.Continuation, StringComparison.OrdinalIgnoreCase))
            {
                await CompleteSuccessfulDispatchAsync(retryEntry, instanceId, cancellationToken);
                finalizedThisTick.Add(retryEntry.IssueId);
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

            if (!IsDispatchEligible(retryIssue, workflowDefinition, runningIssueIds, countsByState))
            {
                await ReleaseRetryReservationAsync(
                    retryEntry.IssueId,
                    retryEntry.IssueIdentifier,
                    instanceId,
                    "issue no longer eligible for dispatch",
                    cancellationToken);
                continue;
            }

            if (!HasGlobalSlot(workflowDefinition, runningIssueIds.Count) ||
                !HasStateSlot(retryIssue.State, workflowDefinition, countsByState))
            {
                await RescheduleRetryAsync(
                    retryEntry,
                    instanceId,
                    "no available orchestrator slots",
                    workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    cancellationToken);
                continue;
            }

            if (await DispatchIssueAsync(
                    retryIssue,
                    workflowDefinition,
                    instanceId,
                    retryEntry.Attempt,
                    countsByState,
                    cancellationToken,
                    resetContinuousTurnBudget: retryEntry.DelayType == RetryDelayTypes.Backoff))
            {
                runningIssueIds.Add(retryIssue.Id);
            }
        }

        var latestRunByIssueId = await LoadLatestRunByIssueAsync(candidatesById.Keys, cancellationToken);

        foreach (var issue in OrderIssuesForDispatch(issues))
        {
            if (!HasGlobalSlot(workflowDefinition, runningIssueIds.Count))
            {
                break;
            }

            if (finalizedThisTick.Contains(issue.Id) ||
                !IsDispatchEligible(issue, workflowDefinition, runningIssueIds, countsByState))
            {
                continue;
            }

            if (await ShouldBlockImplementationRedispatchAsync(issue, latestRunByIssueId, cancellationToken))
            {
                continue;
            }

            if (await DispatchIssueAsync(issue, workflowDefinition, instanceId, attempt: null, countsByState, cancellationToken))
            {
                runningIssueIds.Add(issue.Id);
            }
        }
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

        // An issue whose implementation already succeeded and whose PR is still open must
        // not be silently reimplemented. It is NOT suppressed forever: once the PR is
        // merged or closed (or the issue is explicitly re-dispatched for a later phase),
        // dispatch becomes possible again.
        if (!string.Equals(latestRun.Status, RunStatusNames.Succeeded, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(latestRun.Phase, RunPhaseNames.Implementation, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!HasOpenPullRequest(issue))
        {
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
                Message = $"Issue {issue.Identifier} already has a completed implementation with an open pull request. " +
                          "Automatic reimplementation is blocked; the Commander must dispatch an explicit repair/review phase or close the PR.",
                OccurredAtUtc = timeProvider.GetUtcNow()
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogWarning(
            "Blocked implementation redispatch for issue {IssueIdentifier}: implementation already succeeded and a pull request is still open.",
            issue.Identifier);
        return true;
    }

    private static bool HasOpenPullRequest(NormalizedIssue issue)
    {
        return issue.PullRequests.Any(pullRequest =>
            string.IsNullOrWhiteSpace(pullRequest.State) ||
            pullRequest.State.Trim().Equals("open", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> DispatchIssueAsync(
        NormalizedIssue issue,
        WorkflowDefinition workflowDefinition,
        string instanceId,
        int? attempt,
        Dictionary<string, int> countsByState,
        CancellationToken cancellationToken,
        bool resetContinuousTurnBudget = false)
    {
        var claimed = await coordinationStore.TryClaimIssueAsync(
            issue.Id,
            issue.Identifier,
            ResolveLeaseName(),
            instanceId,
            cancellationToken);

        if (!claimed)
        {
            return false;
        }

        var nowUtc = timeProvider.GetUtcNow();
        var run = await dbContext.Runs
            .Where(runEntity =>
                runEntity.IssueId == issue.Id &&
                (runEntity.Status == RunStatusNames.Running || runEntity.Status == RunStatusNames.Retrying))
            .SingleOrDefaultAsync(cancellationToken);

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
                Phase = RunPhaseNames.Implementation,
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
            run.Phase = RunPhaseNames.Implementation;
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

        await dbContext.SaveChangesAsync(cancellationToken);

        var started = await issueExecutionCoordinator.TryStartAsync(
            new IssueExecutionRequest(
                run.Id,
                runAttempt.Id,
                instanceId,
                attempt,
                issue,
                workflowDefinition),
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

        var stateKey = NormalizeStateKey(issue.State);
        countsByState[stateKey] = countsByState.GetValueOrDefault(stateKey) + 1;
        return true;
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
            Message = "Successful Codex execution was finalized without starting another implementation run.",
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

        var cachedIssue = await dbContext.IssueCache.SingleOrDefaultAsync(
            entry => entry.IssueId == run.IssueId,
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
            if (!snapshotById.TryGetValue(run.IssueId, out var snapshot) ||
                MatchesTerminalState(snapshot.State, workflowDefinition.Runtime.Tracker.TerminalStates))
            {
                continue;
            }

            var reason =
                $"Abandoned work detected for issue {run.IssueIdentifier}: the issue is still open, execution had " +
                "already started, but the issue is no longer dispatchable (execution label removed) and no live run exists.";

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
        CancellationToken cancellationToken)
    {
        var nextAttempt = retryEntry.Attempt + 1;
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
            .ThenBy(issue => issue.Identifier, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.Id, StringComparer.OrdinalIgnoreCase);
    }
}
