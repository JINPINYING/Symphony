using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Host.Services;

public sealed class RuntimeStateService(
    SymphonyDbContext dbContext,
    IWorkflowDefinitionProvider workflowDefinitionProvider,
    IWatchedTaskReader watchedTaskReader,
    TrackerReachability trackerReachability,
    TimeProvider timeProvider)
{
    // A malformed or older snapshot must never take the page down. An empty list
    // reads as "no pull requests are waiting", which is the safe wrong answer:
    // the page understates rather than inventing something to act on.
    private static AgentActivityReport? ReadAgentActivity(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentActivityReport>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }


    /// <summary>
    /// Whether a cached issue carries one of the contract's execution labels.
    /// Reads the cached label JSON rather than re-querying: the queue must reflect
    /// what the dispatcher will see on its next pass, which is this same cache.
    /// </summary>
    private static bool HasExecutionLabel(string? labelsJson, IReadOnlyList<string> executionLabels)
    {
        if (executionLabels.Count == 0 || string.IsNullOrWhiteSpace(labelsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(labelsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var label in document.RootElement.EnumerateArray())
            {
                var value = label.ValueKind == JsonValueKind.String
                    ? label.GetString()
                    : label.TryGetProperty("name", out var name) ? name.GetString() : null;

                if (value is not null &&
                    executionLabels.Any(l => string.Equals(l, value, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // A malformed cache row must not take the page down; it simply does not
            // appear in the queue.
        }

        return false;
    }

    private static IReadOnlyList<OpenPullRequest> ReadOpenPullRequests(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<OpenPullRequest>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public Task<object> GetStateAsync(CancellationToken cancellationToken) =>
        GetStateAsync(includeRawEvents: false, cancellationToken);

    public async Task<object> GetStateAsync(bool includeRawEvents, CancellationToken cancellationToken)
    {
        var generatedAt = timeProvider.GetUtcNow();
        var runningRuns = (await dbContext.Runs
            .AsNoTracking()
            .Where(run => run.Status == RunStatusNames.Running)
            .ToListAsync(cancellationToken))
            .OrderBy(run => run.StartedAtUtc)
            .ToList();
        var retryEntries = (await dbContext.RetryQueue
            .AsNoTracking()
            .ToListAsync(cancellationToken))
            .OrderBy(entry => entry.DueAtUtc)
            .ToList();
        var attempts = await dbContext.RunAttempts
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        // Per vendor, not one global slot (ADCP#24). A single latest row cannot say
        // "Claude is exhausted and Codex is fine", which is exactly the question an
        // operator has when the plane looks idle but nothing is moving. The row does
        // not carry the runner, but it carries the run that produced it, and the run
        // does - so the join answers it without a schema change.
        var rateLimitEvents = (await dbContext.EventLog
            .AsNoTracking()
            .Where(entry => entry.EventName == "rate_limits_updated" && entry.DataJson != null)
            .ToListAsync(cancellationToken))
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .ToList();
        var runnerByRunId = (await dbContext.Runs
            .AsNoTracking()
            .Select(run => new { run.Id, run.Runner })
            .ToListAsync(cancellationToken))
            .ToDictionary(run => run.Id, run => run.Runner, StringComparer.OrdinalIgnoreCase);

        var rateLimitsByRunner = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in rateLimitEvents)
        {
            if (entry.RunId is null ||
                !runnerByRunId.TryGetValue(entry.RunId, out var runner) ||
                string.IsNullOrWhiteSpace(runner) ||
                rateLimitsByRunner.ContainsKey(runner))
            {
                continue;
            }

            rateLimitsByRunner[runner] = new
            {
                runner,
                observed_at = entry.OccurredAtUtc,
                limits = ParseJsonValue(entry.DataJson)
            };
        }

        // rate_limits keeps its existing shape and meaning - the Codex payload the
        // status page already renders - so adding Claude does not silently change
        // what an existing reader is looking at.
        var latestRateLimitsJson = rateLimitEvents
            .FirstOrDefault(entry =>
                entry.RunId is not null &&
                runnerByRunId.TryGetValue(entry.RunId, out var runner) &&
                string.Equals(runner, AgentRunnerNames.Codex, StringComparison.OrdinalIgnoreCase))
            ?.DataJson
            ?? rateLimitEvents.FirstOrDefault()?.DataJson;
        var issueCacheEntries = (await dbContext.IssueCache
            .AsNoTracking()
            .ToListAsync(cancellationToken))
            .OrderByDescending(entry => entry.UpdatedAtUtc ?? entry.CachedAtUtc)
            .ToList();
        var recentActivity = await GetRecentActivityAsync(
            dbContext.EventLog.AsNoTracking(),
            limit: 24,
            includeRawEvents,
            cancellationToken);
        var leases = (await dbContext.InstanceLeases
            .AsNoTracking()
            .ToListAsync(cancellationToken))
            .OrderBy(entry => entry.LeaseName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(entry => entry.UpdatedAtUtc)
            .ToList();
        var issueCacheById = issueCacheEntries.ToDictionary(entry => entry.IssueId);
        var runningIssueIds = runningRuns.Select(run => run.IssueId).ToHashSet(StringComparer.Ordinal);
        var retryIssueIds = retryEntries.Select(entry => entry.IssueId).ToHashSet(StringComparer.Ordinal);

        var secondsRunning = attempts
            .Where(attempt => attempt.CompletedAtUtc.HasValue)
            .Sum(attempt => Math.Max((attempt.CompletedAtUtc!.Value - attempt.StartedAtUtc).TotalSeconds, 0d));
        secondsRunning += attempts
            .Where(attempt => attempt.Status == RunStatusNames.Running && attempt.CompletedAtUtc is null)
            .Sum(attempt => Math.Max((generatedAt - attempt.StartedAtUtc).TotalSeconds, 0d));

        // The owner-facing answer to "does this need me?", computed here so the
        // live page and the published copy say the same thing.
        var escalatedRuns = await dbContext.Runs
            .AsNoTracking()
            .Where(run => run.Status == RunStatusNames.NeedsCommandCenter)
            .ToListAsync(cancellationToken);
        var phaseRows = await dbContext.PhaseLedger.AsNoTracking().ToListAsync(cancellationToken);

        // Written by the orchestration tick rather than fetched here: the page is
        // rendered on every poll, and a GitHub call in this path would make it
        // slow or blank precisely when the owner is checking on things.
        var openPullRequests = ReadOpenPullRequests((await dbContext.EventLog
            .AsNoTracking()
            .Where(entry => entry.EventName == OrchestrationTickService.OpenPullRequestsEventName && entry.DataJson != null)
            .ToListAsync(cancellationToken))
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Select(entry => entry.DataJson)
            .FirstOrDefault());

        // Agents that are not runs, reporting what they are doing. Without this the
        // page reports the queue and calls it the project.
        var agentActivity = (await dbContext.EventLog
            .AsNoTracking()
            .Where(entry => entry.EventName == AgentActivity.EventName && entry.DataJson != null)
            .ToListAsync(cancellationToken))
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Take(8)
            .Select(entry => ReadAgentActivity(entry.DataJson))
            .Where(report => report is not null)
            .Select(report => report!)
            .ToList();

        // The schedulers that wake this plane. The engine cannot see its own
        // liveness from the inside - a scheduler that stopped firing and a genuinely
        // quiet week produce identical internal state - so it asks the host.
        // A failure here must never blank the page: an unreadable scheduler is
        // reported as unmonitored by the reader itself, and an outright throw
        // leaves the rest of the page intact.
        var watchedTasks = new List<WatchedTaskReport>();
        string? primaryRepository = null;
        var trackedRepositories = Array.Empty<string>();
        try
        {
            var workflowForTasks = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
            watchedTasks.AddRange(await watchedTaskReader.ReadAsync(
                workflowForTasks.Runtime.WatchedTasks ?? [], cancellationToken));

            // Only qualify identifiers when there is genuinely something to
            // disambiguate; a single-repository plane keeps reading "#115".
            primaryRepository = workflowForTasks.Runtime.Tracker.IsMultiRepository
                ? workflowForTasks.Runtime.Tracker.PrimaryRepository.Key
                : null;
            trackedRepositories = workflowForTasks.Runtime.Tracker.TrackedRepositories
                .Select(repository => repository.Key)
                .ToArray();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Leave the list empty; the page then simply shows no watched tasks
            // rather than claiming they are healthy.
        }

        // The labels that make an issue executable, from the contract rather than
        // hard-coded, so the queue cannot disagree with what the dispatcher uses.
        var executionLabels = new List<string>();
        try
        {
            var labelWorkflow = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
            executionLabels.AddRange(labelWorkflow.Runtime.Tracker.Labels);
        }
        catch (Exception)
        {
            // Reported elsewhere; an empty list simply yields an empty queue rather
            // than a queue built on a guess.
        }

        var attention = OwnerAttentionSummary.Build(
            engineHealthy: true, // this code only runs when the engine is serving
            escalatedRuns: escalatedRuns,
            runningCount: runningRuns.Count,
            retryQueueCount: retryEntries.Count,
            phases: phaseRows,
            openPullRequests: openPullRequests,
            agentActivity: agentActivity,
            watchedTasks: watchedTasks,
            tracker: trackerReachability.Current,
            lastEventAtUtc: recentActivity.Count > 0 ? recentActivity[0].At : null,
            now: generatedAt,
            primaryRepository: primaryRepository);

        // The workforce view. Runners come from the workflow so an unconfigured
        // vendor is not silently reported as an idle worker.
        var configuredRunners = new List<string>();
        try
        {
            var workflow = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
            configuredRunners.Add(workflow.Runtime.Agent.DefaultRunner);
            configuredRunners.AddRange(workflow.Runtime.Agent.RunnerByLabel.Values);

            // The fallback vendor is a member of the team, not a spare part. It
            // reviews everything the implementer produces - review is always a
            // cross-vendor dispatch - and it takes the work outright when the
            // implementer runs out of quota.
            //
            // It was invisible here because the list was built from the implementer
            // settings only. Once runner_by_label emptied and both lanes went to
            // one vendor, "what the team is doing" showed a team of one, which is
            // not what is configured and not what happens.
            if (!string.IsNullOrWhiteSpace(workflow.Runtime.Agent.FallbackRunner))
            {
                configuredRunners.Add(workflow.Runtime.Agent.FallbackRunner!);
            }
        }
        catch (Exception)
        {
            // A workflow that will not load is already reported elsewhere on the
            // page; it must not also blank the staff view.
        }
        if (configuredRunners.Count == 0)
        {
            configuredRunners.AddRange(["codex", "claude"]);
        }

        var recentRuns = (await dbContext.Runs
            .AsNoTracking()
            .Where(run => run.Status != RunStatusNames.Running)
            .ToListAsync(cancellationToken))
            .OrderByDescending(run => run.LastEventAtUtc ?? run.StartedAtUtc)
            .Take(40)
            .ToList();

        var staff = StaffSummary.Build(
            configuredRunners, runningRuns, recentRuns, generatedAt,
            schedulers: watchedTasks,
            sessions: agentActivity,
            decisionsWaitingOnOwner: attention.Items.Count,
            implementerRunner: configuredRunners.FirstOrDefault());

        return new
        {
            generated_at = generatedAt,
            staff = staff.Select(member => new
            {
                runner = member.Runner,
                role = member.Role,
                state = member.State,
                issue_identifier = member.IssueIdentifier,
                phase = member.Phase,
                activity = member.Activity,
                elapsed_seconds = member.ElapsedSeconds,
                turn_count = member.TurnCount,
                total_tokens = member.TotalTokens,
                last_message = member.LastMessage
            }),
            attention = new
            {
                level = attention.Level,
                headline = attention.Headline,
                detail = attention.Detail,
                items = attention.Items.Select(item => new
                {
                    label = item.Label,
                    detail = item.Detail,
                    severity = item.Severity,
                    url = item.Url
                })
            },
            tracker_reachability = new
            {
                consecutive_failures = trackerReachability.Current.ConsecutiveFailures,
                last_success = trackerReachability.Current.LastSuccessUtc?.ToString("o"),
                unreachable_since = trackerReachability.Current.UnreachableSinceUtc?.ToString("o"),
                last_failure_reason = trackerReachability.Current.LastFailureReason,
                last_failure_transient = trackerReachability.Current.LastFailureTransient
            },
            watched_tasks = watchedTasks.Select(task => new
            {
                name = task.Name,
                path = task.Path,
                state = task.State,
                status = task.Status,
                last_run = task.LastRunUtc?.ToString("o"),
                last_result = task.LastResult,
                next_run = task.NextRunUtc?.ToString("o"),
                expect_every_minutes = task.ExpectEveryMinutes,
                health = task.Health,
                explanation = task.Explanation
            }),
            agent_activity = agentActivity.Select(report => new
            {
                actor = report.Actor,
                summary = report.Summary,
                detail = report.Detail,
                url = report.Url,
                at = report.AtUtc.ToString("o"),
                live = generatedAt - report.AtUtc <= AgentActivity.LiveWindow
            }),
            open_pull_requests = openPullRequests.Select(pr => new
            {
                number = pr.Number,
                title = pr.Title,
                url = pr.Url,
                author = pr.Author,
                is_draft = pr.IsDraft,
                checks_state = pr.ChecksState,
                mergeable = pr.Mergeable,
                updated_at = pr.UpdatedAtUtc == DateTimeOffset.MinValue ? null : pr.UpdatedAtUtc.ToString("o")
            }),
            roadmap = RoadmapReader.Read(Directory.GetCurrentDirectory()).Select(entry => new
            {
                status = entry.Status,
                milestone = entry.Milestone,
                title = entry.Title,
                group = entry.Group
            }),
            counts = new
            {
                running = runningRuns.Count,
                retrying = retryEntries.Count,
                tracked = issueCacheEntries.Count
            },
            running = runningRuns.Select(run =>
            {
                issueCacheById.TryGetValue(run.IssueId, out var cachedIssue);
                return new
                {
                    issue_id = run.IssueId,
                    issue_identifier = run.IssueIdentifier,
                    title = cachedIssue?.Title,
                    url = cachedIssue?.Url,
                    milestone = cachedIssue?.Milestone,
                    labels = ParseJsonValue(cachedIssue?.LabelsJson),
                    state = run.State,
                    session_id = run.SessionId,
                    turn_count = run.TurnCount,
                    last_event = run.LastEvent,
                    last_message = DashboardEventPresentation.GetVisibleMessage(run.LastEvent, run.LastMessage),
                    started_at = run.StartedAtUtc,
                    last_event_at = run.LastEventAtUtc,
                    tokens = new
                    {
                        input_tokens = run.InputTokens,
                        output_tokens = run.OutputTokens,
                        total_tokens = run.TotalTokens
                    }
                };
            }),
            retrying = retryEntries.Select(entry =>
            {
                issueCacheById.TryGetValue(entry.IssueId, out var cachedIssue);
                return new
                {
                    issue_id = entry.IssueId,
                    issue_identifier = entry.IssueIdentifier,
                    title = cachedIssue?.Title,
                    url = cachedIssue?.Url,
                    milestone = cachedIssue?.Milestone,
                    labels = ParseJsonValue(cachedIssue?.LabelsJson),
                    attempt = entry.Attempt,
                    due_at = entry.DueAtUtc,
                    error = entry.Error
                };
            }),
            tracked = new
            {
                total = issueCacheEntries.Count,
                by_state = issueCacheEntries
                    .GroupBy(entry => string.IsNullOrWhiteSpace(entry.State) ? "Unknown" : entry.State)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new
                    {
                        state = group.Key,
                        count = group.Count()
                    }),
                recently_updated = issueCacheEntries
                    .Take(18)
                    .Select(entry => new
                    {
                        issue_id = entry.IssueId,
                        issue_identifier = entry.Identifier,
                        title = entry.Title,
                        state = entry.State,
                        status = retryIssueIds.Contains(entry.IssueId)
                            ? RunStatusNames.Retrying
                            : runningIssueIds.Contains(entry.IssueId)
                                ? RunStatusNames.Running
                                : "tracked",
                        milestone = entry.Milestone,
                        updated_at = entry.UpdatedAtUtc ?? entry.CachedAtUtc,
                        url = entry.Url,
                        labels = ParseJsonValue(entry.LabelsJson)
                    })
            },
            // What the plane will pick up next, and why it has not yet.
            //
            // The page could say what was running and what was tracked, and nothing
            // about the space between - so an issue labelled and waiting looked
            // identical to one nobody had queued. Worse, an issue the plane could
            // not claim (#115 sat on implementation_redispatch_blocked) was
            // indistinguishable from one merely waiting its turn, which is the
            // difference between patience and a fault.
            queue = issueCacheEntries
                .Where(entry => !IssueStateMatcher.IsClosedState(entry.State))
                .Where(entry => !runningIssueIds.Contains(entry.IssueId)
                             && !retryIssueIds.Contains(entry.IssueId))
                .Where(entry => HasExecutionLabel(entry.LabelsJson, executionLabels))
                .OrderBy(entry => entry.UpdatedAtUtc ?? entry.CachedAtUtc)
                .Select(entry => new
                {
                    issue_identifier = entry.Identifier,
                    title = entry.Title,
                    url = entry.Url,
                    repository = entry.Repository,
                    labels = ParseJsonValue(entry.LabelsJson),
                    // A phase ledger means the pipeline already owns this issue: it
                    // is not waiting for a dispatch slot, it is mid-flight in
                    // verify, review or merge, and saying "queued" would be wrong.
                    waiting_on = phaseRows.FirstOrDefault(p =>
                        string.Equals(p.IssueId, entry.IssueId, StringComparison.OrdinalIgnoreCase) &&
                        p.Stage != PhaseStages.Merged && p.Stage != PhaseStages.Closed) is { } phase
                        ? $"in the pipeline at {phase.Stage.Replace('_', ' ')}"
                        : runningRuns.Count > 0
                            ? "waiting for a free slot"
                            : "next to be picked up"
                }),
            activity_mode = includeRawEvents ? "raw" : "operational",
            activity = recentActivity.Select(entry => new
            {
                at = entry.At,
                issue_id = entry.IssueId,
                issue_identifier = entry.IssueIdentifier,
                session_id = entry.SessionId,
                level = entry.Level,
                @event = entry.EventName,
                label = entry.Label,
                repeat_count = entry.RepeatCount,
                is_protocol = entry.IsProtocol,
                message = entry.Message
            }),
            tracked_repositories = trackedRepositories,
            coordination = new
            {
                leases = leases.Select(entry => new
                {
                    lease_name = entry.LeaseName,
                    owner_instance_id = entry.OwnerInstanceId,
                    acquired_at = entry.AcquiredAtUtc,
                    updated_at = entry.UpdatedAtUtc,
                    expires_at = entry.ExpiresAtUtc,
                    is_expired = entry.ExpiresAtUtc <= generatedAt
                })
            },
            codex_totals = new
            {
                input_tokens = runningRuns.Sum(run => run.InputTokens) + await dbContext.Runs
                    .AsNoTracking()
                    .Where(run => run.Status != RunStatusNames.Running)
                    .SumAsync(run => run.InputTokens, cancellationToken),
                output_tokens = runningRuns.Sum(run => run.OutputTokens) + await dbContext.Runs
                    .AsNoTracking()
                    .Where(run => run.Status != RunStatusNames.Running)
                    .SumAsync(run => run.OutputTokens, cancellationToken),
                total_tokens = runningRuns.Sum(run => run.TotalTokens) + await dbContext.Runs
                    .AsNoTracking()
                    .Where(run => run.Status != RunStatusNames.Running)
                    .SumAsync(run => run.TotalTokens, cancellationToken),
                seconds_running = Math.Round(secondsRunning, 3)
            },
            rate_limits = ParseJsonValue(latestRateLimitsJson),
            rate_limits_by_runner = rateLimitsByRunner
        };
    }

    public async Task<(bool Found, object? Payload)> GetIssueStateAsync(
        string issueIdentifier,
        CancellationToken cancellationToken)
    {
        var latestRun = (await dbContext.Runs
            .AsNoTracking()
            .Where(run => run.IssueIdentifier == issueIdentifier)
            .ToListAsync(cancellationToken))
            .OrderByDescending(run => run.StartedAtUtc)
            .FirstOrDefault();
        var workspaceRecord = await dbContext.WorkspaceRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.IssueIdentifier == issueIdentifier, cancellationToken);
        var retryEntry = await dbContext.RetryQueue
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.IssueIdentifier == issueIdentifier, cancellationToken);
        var issueCache = await dbContext.IssueCache
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Identifier == issueIdentifier, cancellationToken);
        var recentEvents = await GetRecentActivityAsync(
            dbContext.EventLog
                .AsNoTracking()
                .Where(entry => entry.IssueIdentifier == issueIdentifier),
            limit: 20,
            includeRawEvents: false,
            cancellationToken);

        if (latestRun is null && workspaceRecord is null && retryEntry is null && issueCache is null && recentEvents.Count == 0)
        {
            return (false, null);
        }

        var issueId = latestRun?.IssueId ?? workspaceRecord?.IssueId ?? retryEntry?.IssueId ?? issueCache?.IssueId;
        var attemptCount = issueId is null
            ? 0
            : await dbContext.RunAttempts.CountAsync(attempt => attempt.IssueId == issueId, cancellationToken);
        var lastError = issueId is null
            ? null
            : (await dbContext.RunAttempts
                .AsNoTracking()
                .Where(attempt => attempt.IssueId == issueId && attempt.Error != null)
                .ToListAsync(cancellationToken))
                .OrderByDescending(attempt => attempt.CompletedAtUtc ?? attempt.StartedAtUtc)
                .Select(attempt => attempt.Error)
                .FirstOrDefault();

        var payload = new
        {
            issue_identifier = issueIdentifier,
            issue_id = issueId,
            status = latestRun?.Status ?? (retryEntry is null ? "tracked" : RunStatusNames.Retrying),
            workspace = new
            {
                path = workspaceRecord?.WorkspacePath
            },
            attempts = new
            {
                restart_count = Math.Max(attemptCount - 1, 0),
                current_retry_attempt = latestRun?.CurrentRetryAttempt ?? retryEntry?.Attempt
            },
            running = latestRun is null
                ? null
                : new
                {
                    session_id = latestRun.SessionId,
                    turn_count = latestRun.TurnCount,
                    state = latestRun.State,
                    started_at = latestRun.StartedAtUtc,
                    last_event = latestRun.LastEvent,
                    last_message = DashboardEventPresentation.GetVisibleMessage(latestRun.LastEvent, latestRun.LastMessage),
                    last_event_at = latestRun.LastEventAtUtc,
                    tokens = new
                    {
                        input_tokens = latestRun.InputTokens,
                        output_tokens = latestRun.OutputTokens,
                        total_tokens = latestRun.TotalTokens
                    }
                },
            retry = retryEntry is null
                ? null
                : new
                {
                    attempt = retryEntry.Attempt,
                    due_at = retryEntry.DueAtUtc,
                    error = retryEntry.Error
                },
            logs = new
            {
                codex_session_logs = Array.Empty<object>()
            },
            recent_events = recentEvents
                .OrderBy(entry => entry.At)
                .Select(entry => new
                {
                    at = entry.At,
                    @event = entry.EventName,
                    label = entry.Label,
                    repeat_count = entry.RepeatCount,
                    message = entry.Message
                }),
            last_error = lastError,
            tracked = new
            {
                title = issueCache?.Title,
                url = issueCache?.Url,
                priority = issueCache?.Priority,
                cache_state = issueCache?.State,
                milestone = issueCache?.Milestone,
                updated_at = issueCache?.UpdatedAtUtc ?? issueCache?.CachedAtUtc,
                labels = ParseJsonValue(issueCache?.LabelsJson),
                blocked_by = ParseJsonValue(issueCache?.BlockedByJson),
                pull_requests = ParseJsonValue(issueCache?.PullRequestsJson)
            }
        };

        return (true, payload);
    }

    private static object? ParseJsonValue(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<IReadOnlyList<DashboardActivityEntry>> GetRecentActivityAsync(
        IQueryable<EventLogEntity> query,
        int limit,
        bool includeRawEvents,
        CancellationToken cancellationToken)
    {
        // Protocol chatter is ~96% of the log, so excluding it in SQL rather than
        // paging through it in memory is the difference between one query and
        // dozens. The in-memory classifier stays the authority - this only avoids
        // dragging rows across that it would certainly discard.
        if (!includeRawEvents)
        {
            var protocolNames = DashboardEventPresentation.ProtocolEventNames.ToArray();
            query = query.Where(entry =>
                !protocolNames.Contains(entry.EventName) &&
                entry.Message != entry.EventName &&
                entry.Message != "");
        }

        const int minimumBatchSize = 32;
        const int maxPages = 8;
        var batchSize = Math.Max(limit * 2, minimumBatchSize);
        var offset = 0;
        var candidates = new List<EventLogEntity>(batchSize);
        var activity = (IReadOnlyList<DashboardActivityEntry>)Array.Empty<DashboardActivityEntry>();

        for (var page = 0; page < maxPages; page++)
        {
            var batch = await query
                // SQLite cannot order DateTimeOffset columns directly, so page by the
                // append-only identity and restore timestamp ordering inside the bounded batch.
                .OrderByDescending(entry => entry.Id)
                .Skip(offset)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            candidates.AddRange(batch);
            offset += batch.Count;

            // Collapsing can only reduce the count, so re-aggregate the whole
            // accumulated set each page rather than guessing how many raw rows a
            // full page of activity needs.
            activity = DashboardActivityAggregator.Build(
                candidates
                    .OrderByDescending(entry => entry.OccurredAtUtc)
                    .ThenByDescending(entry => entry.Id),
                includeRawEvents,
                limit);

            if (activity.Count >= limit || batch.Count < batchSize)
            {
                break;
            }
        }

        return activity;
    }
}
