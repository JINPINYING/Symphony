using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed class IssueExecutionCoordinator(
    IServiceScopeFactory serviceScopeFactory,
    IHostApplicationLifetime applicationLifetime,
    TimeProvider timeProvider,
    ILogger<IssueExecutionCoordinator> logger) : IIssueExecutionCoordinator
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRuns = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> TryStartAsync(IssueExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            applicationLifetime.ApplicationStopping,
            cancellationToken);

        if (!_activeRuns.TryAdd(request.Issue.Id, linkedSource))
        {
            linkedSource.Dispose();
            return Task.FromResult(false);
        }

        _ = Task.Run(() => ExecuteRunAsync(request, linkedSource), CancellationToken.None);
        return Task.FromResult(true);
    }

    public Task<bool> TryStopAsync(string issueId, CancellationToken cancellationToken = default)
    {
        if (!_activeRuns.TryGetValue(issueId, out var cancellationSource))
        {
            return Task.FromResult(false);
        }

        cancellationSource.Cancel();
        return Task.FromResult(true);
    }

    private async Task ExecuteRunAsync(IssueExecutionRequest request, CancellationTokenSource cancellationSource)
    {
        var cancellationToken = cancellationSource.Token;
        WorkspacePreparationResult? workspace = null;
        string? finalStatus = null;
        string? finalError = null;
        RetryPlan? retryPlan = null;
        bool releaseClaim = false;
        var releaseStatus = RunStatusNames.Failed;
        var cleanupWorkspace = false;

        // Hoisted so the cleanup in the finally block uses the same repository's
        // paths the work was prepared in, not the primary repository's.
        var repository = ResolveRepository(request.WorkflowDefinition, request.Issue.Repository);

        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var workspaceManager = scope.ServiceProvider.GetRequiredService<IWorkspaceManager>();
            var workspaceHookRunner = scope.ServiceProvider.GetRequiredService<IWorkspaceHookRunner>();
            var workflowPromptRenderer = scope.ServiceProvider.GetRequiredService<IWorkflowPromptRenderer>();
            var agentRunnerResolver = scope.ServiceProvider.GetRequiredService<IAgentRunnerResolver>();
            var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();

            // M4: pick the implementer for this issue (label-routed rollout, or a
            // phase dispatch's forced runner) and record it on the run so stall
            // detection uses the right window.
            var runnerSelection = request.RunnerOverride is null
                ? agentRunnerResolver.Resolve(request.WorkflowDefinition, request.Issue)
                : agentRunnerResolver.ResolveByName(request.WorkflowDefinition, request.RunnerOverride);
            var runEntity = await dbContext.Runs.SingleOrDefaultAsync(
                run => run.Id == request.RunId,
                cancellationToken);
            if (runEntity is not null)
            {
                runEntity.Runner = runnerSelection.RunnerName;
            }

            await AppendEventAsync(
                dbContext,
                request,
                "dispatch_started",
                LogLevel.Information,
                $"Dispatch started for {request.Issue.Identifier} on runner '{runnerSelection.RunnerName}'.",
                cancellationToken);

            // The repository this issue belongs to, and therefore the clone and
            // worktrees root the work happens in. They are per repository because
            // they have to be: two repositories can each have an issue #115, and a
            // shared worktrees root would put both in the same directory.
            var remoteUrl = ResolveRemoteUrl(
                repository.RemoteUrl,
                repository.Owner,
                repository.Repo);

            workspace = await workspaceManager.PrepareIssueWorkspaceAsync(
                new WorkspacePreparationRequest(
                    IssueId: request.Issue.Id,
                    IssueIdentifier: request.Issue.Identifier,
                    SuggestedBranchName: request.Issue.BranchName,
                    WorkspaceRoot: request.WorkflowDefinition.Runtime.Workspace.Root,
                    SharedClonePath: repository.SharedClonePath,
                    WorktreesRoot: repository.WorktreesRoot,
                    BaseBranch: request.WorkflowDefinition.Runtime.Workspace.BaseBranch,
                    RemoteRepositoryUrl: remoteUrl),
                cancellationToken);

            await UpsertWorkspaceRecordAsync(
                dbContext,
                request,
                workspace,
                timeProvider.GetUtcNow(),
                cancellationToken);

            WorkspacePathSafety.EnsurePathIsWithinRoot(
                request.WorkflowDefinition.Runtime.Workspace.Root,
                workspace.WorkspacePath);

            if (!Directory.Exists(workspace.WorkspacePath))
            {
                throw new InvalidOperationException($"Workspace path does not exist: {workspace.WorkspacePath}");
            }

            if (workspace.CreatedNow)
            {
                await RunRequiredHookAsync(
                    workspaceHookRunner,
                    "after_create",
                    request.WorkflowDefinition.Runtime.Hooks.AfterCreate,
                    request.Issue.Identifier,
                    workspace.WorkspacePath,
                    request.WorkflowDefinition.Runtime.Hooks.TimeoutMs,
                    cancellationToken);
            }

            await RunRequiredHookAsync(
                workspaceHookRunner,
                "before_run",
                request.WorkflowDefinition.Runtime.Hooks.BeforeRun,
                request.Issue.Identifier,
                workspace.WorkspacePath,
                request.WorkflowDefinition.Runtime.Hooks.TimeoutMs,
                cancellationToken);

            var prompt = request.PromptOverride ?? workflowPromptRenderer.RenderForIssue(
                request.WorkflowDefinition,
                request.Issue,
                request.Attempt);

            if (request.PromptOverride is null &&
                (!string.IsNullOrWhiteSpace(request.DirectiveInstructions) ||
                 !string.IsNullOrWhiteSpace(request.DirectiveAction)))
            {
                prompt +=
                    "\n\n## COMMAND CENTER DIRECTIVE (authoritative for this dispatch)\n" +
                    $"action: {request.DirectiveAction ?? "resume"}\n" +
                    $"phase: {request.DirectivePhase ?? "recorded phase"}\n" +
                    (string.IsNullOrWhiteSpace(request.DirectiveInstructions)
                        ? string.Empty
                        : $"instructions:\n{request.DirectiveInstructions}\n") +
                    "Follow this directive within the bounded scope of the source issue. " +
                    "It resolves the previous escalation on this issue; do not restart from scratch unless the directive says to.";
            }

            var trackerQuery = BuildTrackerQueries(
                    request.WorkflowDefinition,
                    WorkflowDispatchPreflightValidator.ValidateAndResolveApiKey(request.WorkflowDefinition))
                .For(request.Issue.Repository);

            var result = await runnerSelection.Runner.RunIssueAsync(
                new AgentRunRequest(
                    request.Issue.Id,
                    request.Issue.Identifier,
                    request.Issue.Title,
                    workspace.WorkspacePath,
                    prompt,
                    runnerSelection.Command,
                    runnerSelection.TurnTimeoutMs,
                    request.WorkflowDefinition.Runtime.Agent.MaxTurns,
                    runnerSelection.ApprovalPolicy,
                    runnerSelection.ThreadSandbox,
                    runnerSelection.TurnSandboxPolicy,
                    runnerSelection.ReadTimeoutMs,
                    trackerQuery),
                (update, token) => PersistAgentUpdateAsync(request, update, token),
                cancellationToken);

            if (result.Success)
            {
                // A successful bounded execution is terminal for this dispatch. Further work
                // (verify/review/repair) must arrive as an explicit new phase dispatch, never
                // as an implicit continuation retry of the implementation run.
                finalStatus = RunStatusNames.Succeeded;
                retryPlan = null;
                releaseClaim = true;
                releaseStatus = RunStatusNames.Succeeded;
            }
            else
            {
                finalStatus = ClassifyFailureStatus(result);
                finalError = SecretRedactor.Redact(Truncate(result.Stderr, 2_000));
                retryPlan = CreateFailureRetryPlan(request.Attempt, finalError, request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs);
            }
        }
        catch (WorkflowLoadException ex)
        {
            finalStatus = RunStatusNames.Failed;
            finalError = SecretRedactor.Redact($"{ex.Code}: {ex.Message}");
            retryPlan = CreateFailureRetryPlan(request.Attempt, finalError, request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs);
        }
        catch (WorkspaceHookExecutionException ex)
        {
            finalStatus = RunStatusNames.Failed;
            finalError = SecretRedactor.Redact($"{ex.HookName}: {ex.Message}");
            retryPlan = CreateFailureRetryPlan(request.Attempt, finalError, request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var stopState = await ReadStopStateAsync(request, CancellationToken.None);
            var outcome = ResolveStopOutcome(stopState.RequestedStopReason, stopState.CleanupWorkspaceOnStop);
            finalStatus = outcome.FinalStatus;
            finalError = outcome.Error;
            releaseClaim = outcome.ReleaseClaim;
            releaseStatus = outcome.ReleaseStatus;
            cleanupWorkspace = outcome.CleanupWorkspace;
            retryPlan = outcome.Retry
                ? CreateFailureRetryPlan(request.Attempt, outcome.Error, request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs)
                : null;
        }
        catch (Exception ex)
        {
            finalStatus = RunStatusNames.Failed;
            finalError = SecretRedactor.Redact(Truncate(ex.Message, 2_000));
            retryPlan = CreateFailureRetryPlan(request.Attempt, finalError, request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs);
        }
        finally
        {
            try
            {
                if (workspace is not null)
                {
                    await using var scope = serviceScopeFactory.CreateAsyncScope();
                    var workspaceHookRunner = scope.ServiceProvider.GetRequiredService<IWorkspaceHookRunner>();
                    var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
                    var workspaceManager = scope.ServiceProvider.GetRequiredService<IWorkspaceManager>();

                    await RunBestEffortHookAsync(
                        workspaceHookRunner,
                        "after_run",
                        request.WorkflowDefinition.Runtime.Hooks.AfterRun,
                        request.Issue.Identifier,
                        workspace.WorkspacePath,
                        request.WorkflowDefinition.Runtime.Hooks.TimeoutMs,
                        CancellationToken.None);

                    if (cleanupWorkspace)
                    {
                        await workspaceManager.CleanupIssueWorkspaceAsync(
                            new WorkspaceCleanupRequest(
                                request.Issue.Identifier,
                                request.WorkflowDefinition.Runtime.Workspace.Root,
                                repository.SharedClonePath,
                                repository.WorktreesRoot,
                                request.WorkflowDefinition.Runtime.Hooks.BeforeRemove,
                                request.WorkflowDefinition.Runtime.Hooks.TimeoutMs),
                            CancellationToken.None);

                        await UpdateWorkspaceCleanupAsync(
                            dbContext,
                            request.Issue,
                            RunStopReasons.Terminal,
                            timeProvider.GetUtcNow(),
                            CancellationToken.None);
                    }

                    if (finalStatus is not null)
                    {
                        await PersistFinalStateAsync(
                            scope.ServiceProvider,
                            dbContext,
                            request,
                            finalStatus,
                            finalError,
                            retryPlan,
                            releaseClaim,
                            releaseStatus,
                            CancellationToken.None);
                    }
                }
                else if (finalStatus is not null)
                {
                    await using var scope = serviceScopeFactory.CreateAsyncScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
                    await PersistFinalStateAsync(
                        scope.ServiceProvider,
                        dbContext,
                        request,
                        finalStatus,
                        finalError,
                        retryPlan,
                        releaseClaim,
                        releaseStatus,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed finalizing run state for issue {IssueIdentifier}.", request.Issue.Identifier);
            }

            _activeRuns.TryRemove(request.Issue.Id, out _);
            cancellationSource.Dispose();
        }
    }

    private async Task PersistAgentUpdateAsync(
        IssueExecutionRequest request,
        AgentRunUpdate update,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();

        var run = await dbContext.Runs.SingleOrDefaultAsync(runEntity => runEntity.Id == request.RunId, cancellationToken);
        if (run is null)
        {
            return;
        }

        run.LastEvent = update.EventType;
        run.LastMessage = SecretRedactor.Redact(Truncate(update.Message, 500));
        run.LastEventAtUtc = update.Timestamp;
        run.SessionId = update.SessionId ?? run.SessionId;

        ApplyTokenTotals(run, update);

        if (string.Equals(update.EventType, "session_started", StringComparison.OrdinalIgnoreCase))
        {
            run.TurnCount += 1;
        }

        if (!string.IsNullOrWhiteSpace(update.SessionId))
        {
            var session = await dbContext.Sessions.SingleOrDefaultAsync(
                entity => entity.Id == update.SessionId,
                cancellationToken);

            if (session is null)
            {
                session = new SessionEntity
                {
                    Id = update.SessionId,
                    RunId = request.RunId,
                    RunAttemptId = request.AttemptId,
                    ThreadId = update.ThreadId,
                    TurnId = update.TurnId,
                    CodexAppServerPid = update.CodexAppServerPid?.ToString(),
                    LastCodexEvent = update.EventType,
                    LastCodexTimestamp = update.Timestamp,
                    LastCodexMessage = SecretRedactor.Redact(Truncate(update.Message, 500)),
                    CreatedAtUtc = update.Timestamp,
                    UpdatedAtUtc = update.Timestamp,
                    TurnCount = string.Equals(update.EventType, "session_started", StringComparison.OrdinalIgnoreCase) ? 1 : 0
                };

                dbContext.Sessions.Add(session);
            }
            else
            {
                session.ThreadId = update.ThreadId ?? session.ThreadId;
                session.TurnId = update.TurnId ?? session.TurnId;
                session.CodexAppServerPid = update.CodexAppServerPid?.ToString() ?? session.CodexAppServerPid;
                session.LastCodexEvent = update.EventType;
                session.LastCodexTimestamp = update.Timestamp;
                session.LastCodexMessage = SecretRedactor.Redact(Truncate(update.Message, 500));
                session.UpdatedAtUtc = update.Timestamp;

                if (string.Equals(update.EventType, "session_started", StringComparison.OrdinalIgnoreCase))
                {
                    session.TurnCount += 1;
                }
            }

            ApplyTokenTotals(session, update);
        }

        dbContext.EventLog.Add(CreateAgentEventLogEntry(request, update));
        if (!string.IsNullOrWhiteSpace(update.RateLimitsJson))
        {
            dbContext.EventLog.Add(new EventLogEntity
            {
                IssueId = request.Issue.Id,
                IssueIdentifier = request.Issue.Identifier,
                RunId = request.RunId,
                RunAttemptId = request.AttemptId,
                SessionId = update.SessionId,
                EventName = "rate_limits_updated",
                Level = LogLevel.Information.ToString(),
                Message = "Rate limits updated.",
                DataJson = update.RateLimitsJson,
                OccurredAtUtc = update.Timestamp
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // How a stop request becomes a final run state. Extracted from the cancellation
    // handler so the one property that matters operationally is directly testable:
    // exactly which stop reasons schedule another retry, and which end the run.
    internal readonly record struct StopOutcome(
        string FinalStatus,
        string Error,
        bool ReleaseClaim,
        string ReleaseStatus,
        bool CleanupWorkspace,
        bool Retry);

    internal static StopOutcome ResolveStopOutcome(string? requestedStopReason, bool cleanupWorkspaceOnStop)
    {
        return requestedStopReason switch
        {
            RunStopReasons.Terminal => new StopOutcome(
                RunStatusNames.CanceledByReconciliation,
                "terminal state reached",
                ReleaseClaim: true,
                RunStatusNames.CanceledByReconciliation,
                CleanupWorkspace: cleanupWorkspaceOnStop,
                Retry: false),
            RunStopReasons.Inactive => new StopOutcome(
                RunStatusNames.CanceledByReconciliation,
                "issue is no longer active",
                ReleaseClaim: true,
                RunStatusNames.CanceledByReconciliation,
                CleanupWorkspace: false,
                Retry: false),
            RunStopReasons.Stalled => new StopOutcome(
                RunStatusNames.Stalled,
                "stall timeout exceeded",
                ReleaseClaim: false,
                RunStatusNames.Failed,
                CleanupWorkspace: false,
                Retry: true),

            // Terminal, and deliberately WITHOUT a retry (ADCP#23). The startup guard
            // has already spent the pre-session attempt budget, so a retry here only
            // creates a reservation that TryClaimIssueAsync fences forever - the run
            // then sits in 'retrying' holding an agent slot with no route out. It ends
            // here instead, and goes to the Command Center where a person can see it.
            RunStopReasons.StartupExhausted => new StopOutcome(
                RunStatusNames.NeedsCommandCenter,
                "startup retry budget exhausted without an agent session",
                ReleaseClaim: true,
                RunStatusNames.NeedsCommandCenter,
                CleanupWorkspace: false,
                Retry: false),
            _ => new StopOutcome(
                RunStatusNames.Failed,
                "run canceled",
                ReleaseClaim: false,
                RunStatusNames.Failed,
                CleanupWorkspace: false,
                Retry: true)
        };
    }

    private async Task<(string? RequestedStopReason, bool CleanupWorkspaceOnStop)> ReadStopStateAsync(
        IssueExecutionRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
        var run = await dbContext.Runs.SingleOrDefaultAsync(runEntity => runEntity.Id == request.RunId, cancellationToken);
        return run is null
            ? (null, false)
            : (run.RequestedStopReason, run.CleanupWorkspaceOnStop);
    }

    private async Task PersistFinalStateAsync(
        IServiceProvider serviceProvider,
        SymphonyDbContext dbContext,
        IssueExecutionRequest request,
        string finalStatus,
        string? finalError,
        RetryPlan? retryPlan,
        bool releaseClaim,
        string releaseStatus,
        CancellationToken cancellationToken)
    {
        var completedAtUtc = timeProvider.GetUtcNow();

        var run = await dbContext.Runs.SingleAsync(runEntity => runEntity.Id == request.RunId, cancellationToken);
        var attempt = await dbContext.RunAttempts.SingleAsync(attemptEntity => attemptEntity.Id == request.AttemptId, cancellationToken);

        attempt.Status = finalStatus;
        attempt.Error = finalError;
        attempt.CompletedAtUtc = completedAtUtc;

        run.LastEvent = finalStatus;
        run.LastMessage = finalError;
        run.LastEventAtUtc = completedAtUtc;
        run.RequestedStopReason = null;
        run.CleanupWorkspaceOnStop = false;

        if (retryPlan is not null)
        {
            run.Status = RunStatusNames.Retrying;
            run.CurrentRetryAttempt = retryPlan.Attempt;
            run.OwnerInstanceId = request.InstanceId;

            var existingRetry = await dbContext.RetryQueue.SingleOrDefaultAsync(
                retryEntity => retryEntity.IssueId == request.Issue.Id,
                cancellationToken);

            if (existingRetry is null)
            {
                dbContext.RetryQueue.Add(new RetryQueueEntity
                {
                    IssueId = request.Issue.Id,
                    IssueIdentifier = request.Issue.Identifier,
                    RunId = request.RunId,
                    OwnerInstanceId = request.InstanceId,
                    Attempt = retryPlan.Attempt,
                    DueAtUtc = retryPlan.DueAtUtc,
                    DelayType = retryPlan.DelayType,
                    Error = retryPlan.Error,
                    MaxBackoffMs = request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    CreatedAtUtc = completedAtUtc,
                    UpdatedAtUtc = completedAtUtc
                });
            }
            else
            {
                existingRetry.OwnerInstanceId = request.InstanceId;
                existingRetry.Attempt = retryPlan.Attempt;
                existingRetry.DueAtUtc = retryPlan.DueAtUtc;
                existingRetry.DelayType = retryPlan.DelayType;
                existingRetry.Error = retryPlan.Error;
                existingRetry.MaxBackoffMs = request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs;
                existingRetry.UpdatedAtUtc = completedAtUtc;
            }
        }
        else
        {
            run.Status = finalStatus;
            run.CompletedAtUtc = completedAtUtc;
            var existingRetry = await dbContext.RetryQueue.SingleOrDefaultAsync(
                retryEntity => retryEntity.IssueId == request.Issue.Id,
                cancellationToken);
            if (existingRetry is not null)
            {
                dbContext.RetryQueue.Remove(existingRetry);
            }
        }

        await AppendEventAsync(
            dbContext,
            request,
            retryPlan is null ? "run_completed" : "retry_scheduled",
            retryPlan is null ? LogLevel.Information : LogLevel.Warning,
            retryPlan is null
                ? $"Run completed with status {finalStatus}."
                : $"Retry scheduled with attempt {retryPlan.Attempt} due at {retryPlan.DueAtUtc:O}.",
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (releaseClaim)
        {
            var coordinationStore = serviceProvider.GetRequiredService<IOrchestrationCoordinationStore>();
            await coordinationStore.ReleaseIssueClaimAsync(
                request.Issue.Id,
                request.InstanceId,
                releaseStatus,
                cancellationToken);
        }
    }

    private async Task AppendEventAsync(
        SymphonyDbContext dbContext,
        IssueExecutionRequest request,
        string eventName,
        LogLevel level,
        string message,
        CancellationToken cancellationToken)
    {
        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = request.Issue.Id,
            IssueIdentifier = request.Issue.Identifier,
            RunId = request.RunId,
            RunAttemptId = request.AttemptId,
            EventName = eventName,
            Level = level.ToString(),
            Message = message,
            OccurredAtUtc = timeProvider.GetUtcNow()
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpsertWorkspaceRecordAsync(
        SymphonyDbContext dbContext,
        IssueExecutionRequest request,
        WorkspacePreparationResult workspace,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        var workspaceRecord = await dbContext.WorkspaceRecords.SingleOrDefaultAsync(
            record => record.IssueId == request.Issue.Id,
            cancellationToken);

        if (workspaceRecord is null)
        {
            dbContext.WorkspaceRecords.Add(new WorkspaceRecordEntity
            {
                IssueId = request.Issue.Id,
                IssueIdentifier = request.Issue.Identifier,
                WorkspacePath = workspace.WorkspacePath,
                BranchName = workspace.BranchName,
                LastPreparedAtUtc = recordedAtUtc
            });
        }
        else
        {
            workspaceRecord.IssueIdentifier = request.Issue.Identifier;
            workspaceRecord.WorkspacePath = workspace.WorkspacePath;
            workspaceRecord.BranchName = workspace.BranchName;
            workspaceRecord.LastPreparedAtUtc = recordedAtUtc;
        }

        var run = await dbContext.Runs.SingleAsync(runEntity => runEntity.Id == request.RunId, cancellationToken);
        run.WorkspacePath = workspace.WorkspacePath;

        var attempt = await dbContext.RunAttempts.SingleAsync(attemptEntity => attemptEntity.Id == request.AttemptId, cancellationToken);
        attempt.WorkspacePath = workspace.WorkspacePath;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpdateWorkspaceCleanupAsync(
        SymphonyDbContext dbContext,
        NormalizedIssue issue,
        string reason,
        DateTimeOffset cleanedAtUtc,
        CancellationToken cancellationToken)
    {
        var workspaceRecord = await dbContext.WorkspaceRecords.SingleOrDefaultAsync(
            record => record.IssueId == issue.Id,
            cancellationToken);

        if (workspaceRecord is null)
        {
            return;
        }

        workspaceRecord.LastCleanedAtUtc = cleanedAtUtc;
        workspaceRecord.LastCleanupReason = reason;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string ClassifyFailureStatus(AgentRunResult result)
    {
        return result.ExitCode == -1 && result.Stderr.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            ? RunStatusNames.TimedOut
            : RunStatusNames.Failed;
    }

    private RetryPlan CreateFailureRetryPlan(int? attempt, string? error, int maxRetryBackoffMs)
    {
        var nextAttempt = attempt.HasValue ? attempt.Value + 1 : 1;
        var exponent = Math.Max(nextAttempt - 1, 0);
        var delayMs = Math.Min(10_000 * (int)Math.Pow(2, exponent), maxRetryBackoffMs);
        return new RetryPlan(
            Attempt: nextAttempt,
            DueAtUtc: timeProvider.GetUtcNow().AddMilliseconds(delayMs),
            DelayType: RetryDelayTypes.Backoff,
            Error: error);
    }

    private static string ResolveRemoteUrl(string? configuredRemoteUrl, string owner, string repo)
    {
        if (!string.IsNullOrWhiteSpace(configuredRemoteUrl))
        {
            return configuredRemoteUrl;
        }

        return $"https://github.com/{owner}/{repo}.git";
    }

    // Falls back to the primary repository, which is what an empty repository key
    // has always meant: every run recorded before multi-repository tracking, and
    // every run in a single-repository install.
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

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.Length <= maxLength
            ? value
            : $"{value[..maxLength]}...";
    }

    private static void ApplyTokenTotals(RunEntity run, AgentRunUpdate update)
    {
        (run.InputTokens, run.LastReportedInputTokens) = AccumulateTokenTotal(run.InputTokens, run.LastReportedInputTokens, update.InputTokens, update.TokenUsageIsDelta);
        (run.OutputTokens, run.LastReportedOutputTokens) = AccumulateTokenTotal(run.OutputTokens, run.LastReportedOutputTokens, update.OutputTokens, update.TokenUsageIsDelta);
        (run.TotalTokens, run.LastReportedTotalTokens) = AccumulateTokenTotal(run.TotalTokens, run.LastReportedTotalTokens, update.TotalTokens, update.TokenUsageIsDelta);
    }

    private static void ApplyTokenTotals(SessionEntity session, AgentRunUpdate update)
    {
        (session.CodexInputTokens, session.LastReportedInputTokens) = AccumulateTokenTotal(session.CodexInputTokens, session.LastReportedInputTokens, update.InputTokens, update.TokenUsageIsDelta);
        (session.CodexOutputTokens, session.LastReportedOutputTokens) = AccumulateTokenTotal(session.CodexOutputTokens, session.LastReportedOutputTokens, update.OutputTokens, update.TokenUsageIsDelta);
        (session.CodexTotalTokens, session.LastReportedTotalTokens) = AccumulateTokenTotal(session.CodexTotalTokens, session.LastReportedTotalTokens, update.TotalTokens, update.TokenUsageIsDelta);
    }

    private static (int AccumulatedTotal, int LastReportedTotal) AccumulateTokenTotal(
        int accumulatedTotal,
        int lastReportedTotal,
        int? nextObservedTotal,
        bool tokenUsageIsDelta)
    {
        return tokenUsageIsDelta
            ? AccumulateDeltaTotal(accumulatedTotal, lastReportedTotal, nextObservedTotal)
            : AccumulateAbsoluteTotal(accumulatedTotal, lastReportedTotal, nextObservedTotal);
    }

    private static (int AccumulatedTotal, int LastReportedTotal) AccumulateDeltaTotal(
        int accumulatedTotal,
        int lastReportedTotal,
        int? nextObservedDelta)
    {
        if (!nextObservedDelta.HasValue)
        {
            return (accumulatedTotal, lastReportedTotal);
        }

        var delta = Math.Max(nextObservedDelta.Value, 0);
        return (accumulatedTotal + delta, lastReportedTotal + delta);
    }

    private static (int AccumulatedTotal, int LastReportedTotal) AccumulateAbsoluteTotal(
        int accumulatedTotal,
        int lastReportedTotal,
        int? nextObservedTotal)
    {
        if (!nextObservedTotal.HasValue)
        {
            return (accumulatedTotal, lastReportedTotal);
        }

        var normalizedObservedTotal = Math.Max(nextObservedTotal.Value, lastReportedTotal);
        accumulatedTotal += Math.Max(normalizedObservedTotal - lastReportedTotal, 0);
        return (accumulatedTotal, normalizedObservedTotal);
    }

    private static EventLogEntity CreateAgentEventLogEntry(IssueExecutionRequest request, AgentRunUpdate update)
    {
        var level = update.EventType switch
        {
            "turn_failed" or "turn_cancelled" or "turn_input_required" => LogLevel.Warning,
            "malformed" or "unsupported_tool_call" or "tool_call_failed" => LogLevel.Warning,
            _ => LogLevel.Information
        };

        var message = SecretRedactor.Redact(Truncate(update.Message, 500)) ?? update.EventType;
        var dataJson = update.DataJson;
        if (!string.IsNullOrWhiteSpace(update.RateLimitsJson))
        {
            dataJson = update.DataJson is null
                ? JsonSerializer.Serialize(new { rate_limits = JsonDocument.Parse(update.RateLimitsJson).RootElement.Clone() })
                : update.DataJson;
        }

        return new EventLogEntity
        {
            IssueId = request.Issue.Id,
            IssueIdentifier = request.Issue.Identifier,
            RunId = request.RunId,
            RunAttemptId = request.AttemptId,
            SessionId = update.SessionId,
            EventName = update.EventType,
            Level = level.ToString(),
            Message = message,
            DataJson = dataJson,
            OccurredAtUtc = update.Timestamp
        };
    }

    private static async Task RunRequiredHookAsync(
        IWorkspaceHookRunner workspaceHookRunner,
        string hookName,
        string? hookScript,
        string issueIdentifier,
        string workspacePath,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hookScript))
        {
            return;
        }

        await workspaceHookRunner.RunHookAsync(
            new WorkspaceHookRequest(
                HookName: hookName,
                Script: hookScript,
                WorkspacePath: workspacePath,
                TimeoutMs: timeoutMs,
                IssueIdentifier: issueIdentifier),
            cancellationToken);
    }

    private async Task RunBestEffortHookAsync(
        IWorkspaceHookRunner workspaceHookRunner,
        string hookName,
        string? hookScript,
        string issueIdentifier,
        string workspacePath,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hookScript))
        {
            return;
        }

        try
        {
            await workspaceHookRunner.RunHookAsync(
                new WorkspaceHookRequest(
                    HookName: hookName,
                    Script: hookScript,
                    WorkspacePath: workspacePath,
                    TimeoutMs: timeoutMs,
                    IssueIdentifier: issueIdentifier),
                cancellationToken);
        }
        catch (WorkspaceHookExecutionException ex)
        {
            logger.LogWarning(ex, "{HookName} hook failed for issue {IssueIdentifier}.", hookName, issueIdentifier);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{HookName} hook failed unexpectedly for issue {IssueIdentifier}.", hookName, issueIdentifier);
        }
    }

    private sealed record RetryPlan(
        int Attempt,
        DateTimeOffset DueAtUtc,
        string DelayType,
        string? Error);
}
