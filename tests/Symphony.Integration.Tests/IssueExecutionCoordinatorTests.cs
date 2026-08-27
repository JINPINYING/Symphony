using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Symphony.Core.Models;
using Symphony.Core.Abstractions;
using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Integration.Tests;

public sealed class IssueExecutionCoordinatorTests
{
    [Fact]
    public void ApplyTokenTotals_ShouldAccumulateTurnUsageDeltasAndReconcileAbsoluteSnapshots()
    {
        var run = new RunEntity();
        var applyTokenTotals = typeof(IssueExecutionCoordinator)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method =>
            {
                if (method.Name != "ApplyTokenTotals")
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 2 &&
                       parameters[0].ParameterType == typeof(RunEntity) &&
                       parameters[1].ParameterType == typeof(AgentRunUpdate);
            });

        applyTokenTotals.Invoke(null, [run, CreateTokenUpdate(10, 4, 14, tokenUsageIsDelta: true)]);
        applyTokenTotals.Invoke(null, [run, CreateTokenUpdate(10, 4, 14, tokenUsageIsDelta: true)]);
        applyTokenTotals.Invoke(null, [run, CreateTokenUpdate(30, 12, 42, tokenUsageIsDelta: false)]);

        Assert.Equal(30, run.InputTokens);
        Assert.Equal(12, run.OutputTokens);
        Assert.Equal(42, run.TotalTokens);
        Assert.Equal(30, run.LastReportedInputTokens);
        Assert.Equal(12, run.LastReportedOutputTokens);
        Assert.Equal(42, run.LastReportedTotalTokens);
    }

    [Fact]
    public async Task TryStartAsync_ShouldReleaseClaimWithoutContinuationRetryWhenRequiredLabelIsRemovedAfterSuccess()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-coordinator.db");
        var workspaceRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-coordinator-workspaces")).FullName;
        await using var provider = BuildServiceProvider(dbPath, workspaceRoot);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        var issue = new NormalizedIssue(
            "issue-1",
            "#1",
            "Issue #1",
            null,
            1,
            "Open",
            null,
            null,
            null,
            ["symphony-test"],
            [],
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var workflow = BuildWorkflowDefinition(workspaceRoot, labels: ["symphony-test"]);
        var run = new RunEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            IssueId = issue.Id,
            IssueIdentifier = issue.Identifier,
            OwnerInstanceId = "instance-1",
            Status = RunStatusNames.Running,
            State = issue.State,
            StartedAtUtc = DateTimeOffset.UtcNow
        };
        var attempt = new RunAttemptEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RunId = run.Id,
            IssueId = issue.Id,
            Status = RunStatusNames.Running,
            StartedAtUtc = run.StartedAtUtc
        };

        dbContext.Runs.Add(run);
        dbContext.RunAttempts.Add(attempt);
        dbContext.DispatchClaims.Add(new DispatchClaimEntity
        {
            IssueId = issue.Id,
            IssueIdentifier = issue.Identifier,
            ClaimedByInstanceId = "instance-1",
            ClaimedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Status = "active"
        });
        await dbContext.SaveChangesAsync();

        var coordinator = provider.GetRequiredService<IIssueExecutionCoordinator>();
        var started = await coordinator.TryStartAsync(
            new IssueExecutionRequest(
                run.Id,
                attempt.Id,
                "instance-1",
                Attempt: null,
                issue,
                workflow),
            CancellationToken.None);

        Assert.True(started);
        await WaitForAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();
            return (await dbContext.Runs.SingleAsync()).Status == RunStatusNames.ReleasedIneligible;
        });

        Assert.Empty(await dbContext.RetryQueue.ToListAsync());
        Assert.Equal(RunStatusNames.ReleasedIneligible, (await dbContext.DispatchClaims.SingleAsync()).Status);
        Assert.Equal(RunStatusNames.ReleasedIneligible, (await dbContext.RunAttempts.SingleAsync()).Status);

        Directory.Delete(workspaceRoot, recursive: true);
        TryDeleteFile(dbPath);
        TryDeleteFile($"{dbPath}-wal");
        TryDeleteFile($"{dbPath}-shm");
    }

    private static AgentRunUpdate CreateTokenUpdate(
        int inputTokens,
        int outputTokens,
        int totalTokens,
        bool tokenUsageIsDelta)
    {
        return new AgentRunUpdate(
            EventType: "turn/completed",
            Timestamp: DateTimeOffset.UtcNow,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: totalTokens,
            TokenUsageIsDelta: tokenUsageIsDelta);
    }

    private static ServiceProvider BuildServiceProvider(string dbPath, string workspaceRoot)
    {
        var services = new ServiceCollection();
        services.AddDbContext<SymphonyDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddScoped<IWorkspaceManager>(_ => new SucceedingWorkspaceManager(workspaceRoot));
        services.AddScoped<IWorkspaceHookRunner, NoOpWorkspaceHookRunner>();
        services.AddScoped<IWorkflowPromptRenderer, StaticWorkflowPromptRenderer>();
        services.AddScoped<IAgentRunner, SuccessfulAgentRunner>();
        services.AddScoped<ITrackerClient, LabelRemovedTrackerClient>();
        services.AddScoped<IOrchestrationCoordinationStore, OrchestrationCoordinationStore>();
        services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddSingleton<IIssueExecutionCoordinator, IssueExecutionCoordinator>();
        return services.BuildServiceProvider();
    }

    private static WorkflowDefinition BuildWorkflowDefinition(string workspaceRoot, IReadOnlyList<string> labels)
    {
        var runtime = new WorkflowRuntimeSettings(
            new WorkflowTrackerSettings(
                Kind: "github",
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "test-token",
                Owner: "released",
                Repo: "symphony",
                Milestone: null,
                IncludePullRequests: true,
                Labels: labels,
                ActiveStates: ["Open"],
                TerminalStates: ["Closed"]),
            new WorkflowPollingSettings(600_000),
            new WorkflowAgentSettings(1, 20, 300_000, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)),
            new WorkflowServerSettings(Port: null),
            new WorkflowWorkspaceSettings(workspaceRoot, Path.Combine(workspaceRoot, "repo"), Path.Combine(workspaceRoot, "worktrees"), "main", null),
            new WorkflowHooksSettings(null, null, null, null, 60_000),
            new WorkflowCodexSettings("codex app-server", 30_000, "never", "danger-full-access", "danger-full-access", 5_000, 300_000));

        return new WorkflowDefinition(new Dictionary<string, object?>(), "Prompt body", runtime, "WORKFLOW.md", DateTimeOffset.UtcNow);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(await condition(), "Timed out waiting for asynchronous coordinator finalization.");
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

    private sealed class SucceedingWorkspaceManager(string workspaceRoot) : IWorkspaceManager
    {
        public Task<WorkspacePreparationResult> PrepareIssueWorkspaceAsync(
            WorkspacePreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            var workspacePath = Directory.CreateDirectory(Path.Combine(workspaceRoot, "issue-1")).FullName;
            return Task.FromResult(new WorkspacePreparationResult(workspacePath, "symphony/1", CreatedNow: false));
        }

        public Task<WorkspaceCleanupResult> CleanupIssueWorkspaceAsync(
            WorkspaceCleanupRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WorkspaceCleanupResult(Path.Combine(workspaceRoot, "issue-1"), Existed: true, RemovedNow: false));
        }
    }

    private sealed class NoOpWorkspaceHookRunner : IWorkspaceHookRunner
    {
        public Task RunHookAsync(WorkspaceHookRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StaticWorkflowPromptRenderer : IWorkflowPromptRenderer
    {
        public string RenderForIssue(WorkflowDefinition workflowDefinition, NormalizedIssue issue, int? attempt = null) => "Prompt body";
    }

    private sealed class SuccessfulAgentRunner : IAgentRunner
    {
        public Task<AgentRunResult> RunIssueAsync(
            AgentRunRequest request,
            Func<AgentRunUpdate, CancellationToken, Task>? onUpdate = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentRunResult(true, 0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class LabelRemovedTrackerClient : ITrackerClient
    {
        public Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(
            TrackerQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);

        public Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(
            TrackerQuery query,
            IReadOnlyList<string> states,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);

        public Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(
            TrackerQuery query,
            IReadOnlyList<string> issueIds,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IssueStateSnapshot>>([new IssueStateSnapshot(issueIds[0], "Open", [])]);

        public Task<GitHubGraphQlExecutionResult> ExecuteGitHubGraphQlAsync(
            TrackerQuery query,
            string graphQlDocument,
            string? variablesJson,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GitHubGraphQlExecutionResult(true, "{\"data\":{}}"));
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => stopping.Cancel();
    }
}
