using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed partial class OrchestrationTickService
{
    private const int StartupAttemptBudget = 2;
    private const int DefaultStartupTimeoutMs = 300_000;
    private const int MinimumStartupTimeoutMs = 60_000;

    private async Task ReconcileStartupAttemptsAsync(
        WorkflowDefinition workflowDefinition,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var startupTimeout = ResolveStartupAttemptTimeout(workflowDefinition);

        var startupRuns = await dbContext.Runs
            .Where(run =>
                run.Status == RunStatusNames.Running &&
                run.OwnerInstanceId == instanceId &&
                run.SessionId == null)
            .ToListAsync(cancellationToken);

        foreach (var run in startupRuns)
        {
            var activeAttempts = await dbContext.RunAttempts
                .Where(attempt => attempt.RunId == run.Id && attempt.CompletedAtUtc == null)
                .ToListAsync(cancellationToken);

            var activeAttempt = activeAttempts
                .OrderByDescending(attempt => attempt.StartedAtUtc)
                .FirstOrDefault();
            if (activeAttempt is null || !IsStartupAttemptStale(activeAttempt.StartedAtUtc, nowUtc, startupTimeout))
            {
                continue;
            }

            var attemptCount = await dbContext.RunAttempts
                .CountAsync(attempt => attempt.RunId == run.Id, cancellationToken);
            var exhausted = HasExhaustedStartupAttemptBudget(attemptCount);
            var age = nowUtc - activeAttempt.StartedAtUtc;
            var message = exhausted
                ? $"Startup retry budget exhausted after {attemptCount} attempts without a Codex session. Latest attempt {activeAttempt.Id} remained pre-session for {(int)age.TotalSeconds}s. The active claim remains reserved so ordinary candidate polling cannot start a third attempt."
                : $"Startup attempt {activeAttempt.Id} remained pre-session for {(int)age.TotalSeconds}s and exceeded the {startupTimeout.TotalSeconds:0}s startup timeout.";

            dbContext.EventLog.Add(new EventLogEntity
            {
                IssueId = run.IssueId,
                IssueIdentifier = run.IssueIdentifier,
                RunId = run.Id,
                RunAttemptId = activeAttempt.Id,
                EventName = exhausted ? "startup_retry_budget_exhausted" : "startup_attempt_stalled",
                Level = LogLevel.Warning.ToString(),
                Message = message,
                OccurredAtUtc = nowUtc
            });

            run.LastEvent = exhausted ? "startup_retry_budget_exhausted" : "startup_attempt_stalled";
            run.LastMessage = message;
            run.LastEventAtUtc = nowUtc;
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogWarning(
                "Startup guard stopping issue {IssueIdentifier}. AttemptId={AttemptId} AttemptCount={AttemptCount} Exhausted={Exhausted} AgeSeconds={AgeSeconds}",
                run.IssueIdentifier,
                activeAttempt.Id,
                attemptCount,
                exhausted,
                (int)age.TotalSeconds);

            // A stalled attempt keeps its claim and retries. An exhausted budget must
            // NOT: stopping it as "stalled" schedules a retry that TryClaimIssueAsync
            // then refuses forever with startup_attempt_fence, so the run sits in
            // 'retrying' with an elapsed due_at, holding the only agent slot, until
            // somebody clears it by hand (ADCP#23). Exhaustion is terminal and goes to
            // the Command Center instead.
            await RequestRunStopAsync(
                run,
                exhausted ? RunStopReasons.StartupExhausted : RunStopReasons.Stalled,
                cleanupWorkspace: false,
                workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                instanceId,
                cancellationToken);
        }
    }

    private static TimeSpan ResolveStartupAttemptTimeout(WorkflowDefinition workflowDefinition)
    {
        var configuredMs = workflowDefinition.Runtime.Codex.StallTimeoutMs;
        var boundedMs = configuredMs > 0
            ? Math.Min(configuredMs, DefaultStartupTimeoutMs)
            : DefaultStartupTimeoutMs;
        return TimeSpan.FromMilliseconds(Math.Max(boundedMs, MinimumStartupTimeoutMs));
    }

    private static bool IsStartupAttemptStale(
        DateTimeOffset startedAtUtc,
        DateTimeOffset nowUtc,
        TimeSpan timeout)
    {
        return nowUtc - startedAtUtc > timeout;
    }

    private static bool HasExhaustedStartupAttemptBudget(int attemptCount)
    {
        return attemptCount >= StartupAttemptBudget;
    }
}
