using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed partial class OrchestrationTickService
{
    private async Task RunStartupCleanupCoreAsync(
        WorkflowDefinition workflowDefinition,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var terminalStates = workflowDefinition.Runtime.Tracker.TerminalStates;
        if (terminalStates.Count == 0)
        {
            logger.LogDebug("Skipping startup terminal cleanup because tracker.terminal_states is empty.");
            return;
        }

        // Per repository, and each one's failure is its own: a repository the plane
        // cannot reach at startup must not stop the others being tidied.
        var terminalIssues = new List<NormalizedIssue>();
        foreach (var repositoryQuery in BuildTrackerQueries(workflowDefinition, apiKey).All)
        {
            try
            {
                terminalIssues.AddRange(
                    await trackerClient.FetchIssuesByStatesAsync(repositoryQuery, terminalStates, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Startup terminal cleanup could not fetch terminal issues for {Owner}/{Repo}. Continuing startup.",
                    repositoryQuery.Owner,
                    repositoryQuery.Repo);
            }
        }

        foreach (var issue in terminalIssues)
        {
            try
            {
                var repository = ResolveRepository(workflowDefinition, issue.Repository);
                var cleanupResult = await workspaceManager.CleanupIssueWorkspaceAsync(
                    new WorkspaceCleanupRequest(
                        issue.Identifier,
                        workflowDefinition.Runtime.Workspace.Root,
                        repository.SharedClonePath,
                        repository.WorktreesRoot,
                        workflowDefinition.Runtime.Hooks.BeforeRemove,
                        workflowDefinition.Runtime.Hooks.TimeoutMs),
                    cancellationToken);

                if (cleanupResult.RemovedNow)
                {
                    await UpdateWorkspaceCleanupRecordAsync(issue, RunStopReasons.Terminal, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Startup terminal cleanup failed for issue {IssueIdentifier}.", issue.Identifier);
            }
        }
    }

    private async Task PersistWorkflowSnapshotAsync(WorkflowDefinition workflowDefinition, CancellationToken cancellationToken)
    {
        var runtimeJson = JsonSerializer.Serialize(workflowDefinition.Runtime);
        var configHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runtimeJson)));

        var latestSnapshot = await dbContext.WorkflowSnapshots
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestSnapshot is not null &&
            latestSnapshot.ConfigHash == configHash &&
            latestSnapshot.SourcePath.Equals(workflowDefinition.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        dbContext.WorkflowSnapshots.Add(new WorkflowSnapshotEntity
        {
            SourcePath = workflowDefinition.SourcePath,
            ConfigHash = configHash,
            RuntimeJson = runtimeJson,
            LoadedAtUtc = workflowDefinition.LoadedAtUtc
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertIssueCacheAsync(
        IReadOnlyList<NormalizedIssue> issues,
        WorkflowDefinition workflowDefinition,
        CancellationToken cancellationToken)
    {
        var cachedAtUtc = timeProvider.GetUtcNow();
        var activeIssueIds = await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.Running || run.Status == RunStatusNames.Retrying)
            .Select(run => run.IssueId)
            .ToListAsync(cancellationToken);
        var activeIssueIdSet = new HashSet<string>(activeIssueIds, StringComparer.OrdinalIgnoreCase);

        foreach (var issue in issues)
        {
            var isEligible = IsCandidateEligibleForAcquisitionSlo(issue, workflowDefinition);
            var isUnclaimedEligibilityEpisode = isEligible && !activeIssueIdSet.Contains(issue.Id);
            var existing = await dbContext.IssueCache.SingleOrDefaultAsync(
                entity => entity.IssueId == issue.Id,
                cancellationToken);

            if (existing is null)
            {
                var entity = CreateIssueCacheEntity(issue, cachedAtUtc);
                if (isUnclaimedEligibilityEpisode)
                {
                    entity.EligibleSeenAtUtc = cachedAtUtc;
                    AddIssueEvent(
                        issue.Id,
                        issue.Identifier,
                        null,
                        null,
                        "candidate_discovered",
                        LogLevel.Information,
                        $"Issue {issue.Identifier} first observed as acquisition-eligible.");
                }

                dbContext.IssueCache.Add(entity);
                continue;
            }

            existing.Identifier = issue.Identifier;
            existing.Repository = issue.Repository;
            existing.Title = issue.Title;
            existing.Description = issue.Description;
            existing.Priority = issue.Priority;
            existing.State = issue.State;
            existing.BranchName = issue.BranchName;
            existing.Url = issue.Url;
            existing.Milestone = issue.Milestone;
            existing.LabelsJson = JsonSerializer.Serialize(issue.Labels);
            // An empty incoming PR list may mean "not fetched" (include_pull_requests
            // disabled) or "linkage lost", not "no PR ever existed". Cached pull request
            // evidence is durable implementation evidence for the redispatch guards, so
            // never replace non-empty evidence with an empty list.
            if (issue.PullRequests.Count > 0)
            {
                existing.PullRequestsJson = JsonSerializer.Serialize(issue.PullRequests);
            }
            existing.BlockedByJson = JsonSerializer.Serialize(issue.BlockedBy);
            existing.CreatedAtUtc = issue.CreatedAt;
            existing.UpdatedAtUtc = issue.UpdatedAt;
            if (isUnclaimedEligibilityEpisode && existing.EligibleSeenAtUtc is null)
            {
                existing.EligibleSeenAtUtc = cachedAtUtc;
                AddIssueEvent(
                    issue.Id,
                    issue.Identifier,
                    null,
                    null,
                    "candidate_discovered",
                    LogLevel.Information,
                    $"Issue {issue.Identifier} first observed as acquisition-eligible.");
            }

            if (!isEligible)
            {
                existing.EligibleSeenAtUtc = null;
            }

            existing.CachedAtUtc = cachedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RefreshTrackedIssueCacheStatesAsync(
        WorkflowDefinition workflowDefinition,
        string apiKey,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var cachedIssues = await dbContext.IssueCache.ToListAsync(cancellationToken);
        if (cachedIssues.Count == 0)
        {
            return;
        }

        var refreshedStates = await TryFetchIssueStatesByIdsAsync(
            workflowDefinition,
            apiKey,
            cachedIssues.Select(issue => issue.IssueId).ToList(),
            "Tracked issue cache state refresh failed; dashboard issue-state summaries may be stale.",
            cancellationToken);
        if (refreshedStates is null)
        {
            return;
        }

        var refreshedById = refreshedStates.ToDictionary(state => state.Id, StringComparer.OrdinalIgnoreCase);
        var hasChanges = false;
        var refreshedAtUtc = timeProvider.GetUtcNow();

        foreach (var cachedIssue in cachedIssues)
        {
            if (!refreshedById.TryGetValue(cachedIssue.IssueId, out var refreshedState))
            {
                continue;
            }

            var isTerminal = MatchesTerminalState(
                refreshedState.State,
                workflowDefinition.Runtime.Tracker.TerminalStates);

            if (!string.Equals(cachedIssue.State, refreshedState.State, StringComparison.OrdinalIgnoreCase))
            {
                cachedIssue.State = refreshedState.State;
                cachedIssue.CachedAtUtc = refreshedAtUtc;
                hasChanges = true;
            }

            if (isTerminal)
            {
                await CleanupTerminalTrackedIssueWorkspaceAsync(
                    cachedIssue,
                    workflowDefinition,
                    instanceId,
                    cancellationToken);
            }
        }

        if (hasChanges)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task CleanupTerminalTrackedIssueWorkspaceAsync(
        IssueCacheEntity cachedIssue,
        WorkflowDefinition workflowDefinition,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var hasRunningRun = await dbContext.Runs.AnyAsync(
            run => run.IssueId == cachedIssue.IssueId && run.Status == RunStatusNames.Running,
            cancellationToken);
        if (hasRunningRun)
        {
            return;
        }

        var workspaceRecord = await dbContext.WorkspaceRecords.SingleOrDefaultAsync(
            record => record.IssueId == cachedIssue.IssueId,
            cancellationToken);

        var releasedRetryState = await ReleaseTerminalRetryStateAsync(cachedIssue, instanceId, cancellationToken);
        if (workspaceRecord is null && !releasedRetryState)
        {
            return;
        }

        if (workspaceRecord is not null &&
            workspaceRecord.LastCleanedAtUtc.HasValue &&
            string.Equals(workspaceRecord.LastCleanupReason, RunStopReasons.Terminal, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var repository = ResolveRepository(workflowDefinition, cachedIssue.Repository);
            var cleanupResult = await workspaceManager.CleanupIssueWorkspaceAsync(
                new WorkspaceCleanupRequest(
                    cachedIssue.Identifier,
                    workflowDefinition.Runtime.Workspace.Root,
                    repository.SharedClonePath,
                    repository.WorktreesRoot,
                    workflowDefinition.Runtime.Hooks.BeforeRemove,
                    workflowDefinition.Runtime.Hooks.TimeoutMs),
                cancellationToken);

            if (workspaceRecord is not null && (!cleanupResult.Existed || cleanupResult.RemovedNow))
            {
                workspaceRecord.LastCleanedAtUtc = timeProvider.GetUtcNow();
                workspaceRecord.LastCleanupReason = RunStopReasons.Terminal;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tracked terminal cleanup failed for issue {IssueIdentifier}.", cachedIssue.Identifier);
        }
    }

    private async Task<bool> ReleaseTerminalRetryStateAsync(
        IssueCacheEntity cachedIssue,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var retryingRuns = await dbContext.Runs
            .Where(run => run.IssueId == cachedIssue.IssueId && run.Status == RunStatusNames.Retrying)
            .ToListAsync(cancellationToken);

        foreach (var run in retryingRuns)
        {
            run.Status = RunStatusNames.CanceledByReconciliation;
            run.CompletedAtUtc = nowUtc;
            run.RequestedStopReason = null;
            run.CleanupWorkspaceOnStop = false;
            run.LastEvent = "terminal_state_observed";
            run.LastMessage = RunStopReasons.Terminal;
            run.LastEventAtUtc = nowUtc;
        }

        var retryEntries = await dbContext.RetryQueue
            .Where(entry => entry.IssueId == cachedIssue.IssueId)
            .ToListAsync(cancellationToken);
        if (retryEntries.Count > 0)
        {
            dbContext.RetryQueue.RemoveRange(retryEntries);
        }

        if (retryingRuns.Count > 0 || retryEntries.Count > 0)
        {
            await coordinationStore.ReleaseIssueClaimAsync(
                cachedIssue.IssueId,
                instanceId,
                RunStatusNames.CanceledByReconciliation,
                cancellationToken);
        }

        if (retryingRuns.Count > 0 || retryEntries.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }

    private async Task<IReadOnlyList<IssueStateSnapshot>?> TryFetchIssueStatesByIdsAsync(
        WorkflowDefinition workflowDefinition,
        string apiKey,
        IReadOnlyList<string> issueIds,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        if (issueIds.Count == 0)
        {
            return [];
        }

        try
        {
            return await trackerClient.FetchIssueStatesByIdsAsync(
                BuildTrackerQuery(workflowDefinition, apiKey),
                issueIds,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{FailureMessage}", failureMessage);
            return null;
        }
    }

    private async Task UpdateWorkspaceCleanupRecordAsync(
        NormalizedIssue issue,
        string reason,
        CancellationToken cancellationToken)
    {
        var record = await dbContext.WorkspaceRecords.SingleOrDefaultAsync(
            entity => entity.IssueId == issue.Id,
            cancellationToken);

        if (record is null)
        {
            return;
        }

        record.LastCleanedAtUtc = timeProvider.GetUtcNow();
        record.LastCleanupReason = reason;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static IssueCacheEntity CreateIssueCacheEntity(NormalizedIssue issue, DateTimeOffset cachedAtUtc)
    {
        return new IssueCacheEntity
        {
            IssueId = issue.Id,
            Identifier = issue.Identifier,
            Repository = issue.Repository,
            Title = issue.Title,
            Description = issue.Description,
            Priority = issue.Priority,
            State = issue.State,
            BranchName = issue.BranchName,
            Url = issue.Url,
            Milestone = issue.Milestone,
            LabelsJson = JsonSerializer.Serialize(issue.Labels),
            PullRequestsJson = JsonSerializer.Serialize(issue.PullRequests),
            BlockedByJson = JsonSerializer.Serialize(issue.BlockedBy),
            CreatedAtUtc = issue.CreatedAt,
            UpdatedAtUtc = issue.UpdatedAt,
            CachedAtUtc = cachedAtUtc
        };
    }

    private void AddIssueEvent(
        string? issueId,
        string? issueIdentifier,
        string? runId,
        string? runAttemptId,
        string eventName,
        LogLevel level,
        string message)
    {
        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = issueId,
            IssueIdentifier = issueIdentifier,
            RunId = runId,
            RunAttemptId = runAttemptId,
            EventName = eventName,
            Level = level.ToString(),
            Message = message,
            OccurredAtUtc = timeProvider.GetUtcNow()
        });
    }

    // Falls back to the primary repository, which is what an empty repository key
    // has always meant: every row written before multi-repository tracking, and
    // every row in a single-repository install.
    private static WorkflowRepositorySettings ResolveRepository(
        WorkflowDefinition workflowDefinition,
        string? repositoryKey)
    {
        var tracker = workflowDefinition.Runtime.Tracker;
        return tracker.FindRepository(repositoryKey) ?? tracker.PrimaryRepository;
    }

    private static TrackerQuerySet BuildTrackerQueries(WorkflowDefinition workflowDefinition, string apiKey)
    {
        var tracker = workflowDefinition.Runtime.Tracker;
        return new TrackerQuerySet(tracker.TrackedRepositories
            .Select(repository => new TrackerQuery(
                tracker.Endpoint,
                apiKey,
                repository.Owner,
                repository.Repo,
                tracker.ActiveStates,
                tracker.Labels,
                tracker.Milestone,
                tracker.IncludePullRequests))
            .ToList());
    }

    private static TrackerQuery BuildTrackerQuery(WorkflowDefinition workflowDefinition, string apiKey) =>
        BuildTrackerQueries(workflowDefinition, apiKey).Primary;
}
