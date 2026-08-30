using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symphony.Core.Abstractions;
using Symphony.Core.Configuration;
using Symphony.Core.Models;
using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Integration.Tests;

public sealed class OrchestrationTickServiceTests
{
    [Fact]
    public async Task RunTickAsync_ShouldFinalizeSuccessfulDispatchWithoutSchedulingContinuation()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.Succeeded, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Equal(RunPhaseNames.Implementation, run.Phase);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Equal(RunStatusNames.Succeeded, (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldDrainLegacyContinuationEntriesWithoutRedispatching()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", delayType: RetryDelayTypes.Continuation);

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Equal(RunStatusNames.Succeeded, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
    }

    [Fact]
    public async Task RunTickAsync_ShouldEscalateMissingRetryCandidateWithUnfinishedWork()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Open"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", sessionId: "session-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Equal(RunStatusNames.NeedsCommandCenter, (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "needs_command_center");
    }

    [Fact]
    public async Task RunTickAsync_ShouldReleaseMissingRetryCandidateWhenIssueIsTerminal()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", sessionId: "session-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal(RunStatusNames.ReleasedIneligible, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
    }

    [Fact]
    public async Task RunTickAsync_ShouldKeepRetryReservationWhenCandidateReloadFails()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], throwOnFetchStatesByIds: true),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", sessionId: "session-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal(RunStatusNames.Retrying, (await harness.DbContext.Runs.SingleAsync()).Status);
        var retryEntry = await harness.DbContext.RetryQueue.SingleAsync();
        Assert.True(retryEntry.DueAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RunTickAsync_ShouldEscalateAbandonedReleasedRunForOpenUnlabeledIssue()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-88"] = "Open"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync(
            "issue-88",
            "#88",
            "Open",
            "instance-1",
            status: RunStatusNames.ReleasedIneligible,
            sessionId: "session-88",
            completedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.NeedsCommandCenter, run.Status);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "needs_command_center");
    }

    [Fact]
    public async Task RunTickAsync_ShouldBlockImplementationRedispatchWhileSucceededRunHasOpenPullRequest()
    {
        var issueWithOpenPr = BuildIssue(
            "issue-1",
            "#1",
            "Open",
            null,
            pullRequests: [new PullRequestRef("pr-1", 89, "OPEN", null, null, null)]);

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([issueWithOpenPr]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            status: RunStatusNames.Succeeded,
            sessionId: "session-1",
            completedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Contains(
            await harness.DbContext.EventLog.ToListAsync(),
            entry => entry.EventName == "implementation_redispatch_blocked");
    }

    [Fact]
    public async Task RunTickAsync_ShouldAllowRedispatchOfSucceededIssueOncePullRequestIsNoLongerOpen()
    {
        var issueWithMergedPr = BuildIssue(
            "issue-1",
            "#1",
            "Open",
            null,
            pullRequests: [new PullRequestRef("pr-1", 89, "MERGED", null, null, null)]);

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([issueWithMergedPr]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            status: RunStatusNames.Succeeded,
            sessionId: "session-1",
            completedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.Coordinator.StartRequests);
    }

    [Fact]
    public async Task RunTickAsync_ShouldUseBackoffRetryAfterFailure()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Failure));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var retryEntry = await harness.DbContext.RetryQueue.SingleAsync();
        Assert.Equal(1, retryEntry.Attempt);
        Assert.Equal(RetryDelayTypes.Backoff, retryEntry.DelayType);
        Assert.True(retryEntry.DueAtUtc > DateTimeOffset.UtcNow.AddSeconds(9));
    }

    [Fact]
    public async Task RunTickAsync_ShouldHonorPerStateConcurrencyLimits()
    {
        var workflow = BuildWorkflowDefinition(
            maxConcurrentAgents: 5,
            maxConcurrentByState: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["open"] = 1
            });

        await using var harness = await TestHarness.CreateAsync(
            workflow,
            tracker: new FakeTrackerClient([BuildIssue("issue-2", "#2", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
    }

    [Fact]
    public async Task RunTickAsync_ShouldRejectTodoIssuesWithActiveBlockers()
    {
        var workflow = BuildWorkflowDefinition(
            maxConcurrentAgents: 1,
            activeStates: ["Todo"]);

        var todoIssue = BuildIssue(
            "issue-1",
            "#1",
            "Todo",
            [new BlockerRef("issue-0", "#0", "Open")]);

        await using var harness = await TestHarness.CreateAsync(
            workflow,
            tracker: new FakeTrackerClient([todoIssue]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
    }

    [Fact]
    public async Task RunTickAsync_ShouldStopTerminalRunsAndCleanupWorkspace()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldPersistTerminalStopBeforeCancelingLiveRun()
    {
        var coordinator = new FakeIssueExecutionCoordinator(
            FakeDispatchOutcome.LeaveRunning,
            observeStopStateWithFreshContext: true);

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator);

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.NotNull(coordinator.ObservedStopState);
        Assert.Equal(RunStopReasons.Terminal, coordinator.ObservedStopState.Value.RequestedStopReason);
        Assert.True(coordinator.ObservedStopState.Value.CleanupWorkspaceOnStop);
    }

    [Fact]
    public async Task RunTickAsync_ShouldRefreshTrackedIssueCacheStateForClosedIssues()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        var initialCachedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open", initialCachedAtUtc);

        await harness.Service.RunTickAsync(CancellationToken.None);

        var cachedIssue = await harness.DbContext.IssueCache.SingleAsync();
        Assert.Equal("Closed", cachedIssue.State);
        Assert.True(cachedIssue.CachedAtUtc > initialCachedAtUtc);
    }

    [Fact]
    public async Task RunTickAsync_ShouldCleanupRetryWorkspaceWhenTrackedIssueBecomesClosed()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open", DateTimeOffset.UtcNow.AddMinutes(-5));
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        var cleanupRequest = Assert.Single(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal("#1", cleanupRequest.IssueIdentifier);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
        Assert.NotNull((await harness.DbContext.WorkspaceRecords.SingleAsync()).LastCleanedAtUtc);
    }

    [Fact]
    public async Task RunTickAsync_ShouldResetLastReportedTokenTotalsWhenRetryStartsNewAttempt()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            inputTokens: 100,
            outputTokens: 50,
            totalTokens: 150,
            lastReportedInputTokens: 100,
            lastReportedOutputTokens: 50,
            lastReportedTotalTokens: 150);

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.Running, run.Status);
        Assert.Equal(100, run.InputTokens);
        Assert.Equal(50, run.OutputTokens);
        Assert.Equal(150, run.TotalTokens);
        Assert.Equal(0, run.LastReportedInputTokens);
        Assert.Equal(0, run.LastReportedOutputTokens);
        Assert.Equal(0, run.LastReportedTotalTokens);
    }

    [Fact]
    public async Task RunTickAsync_ShouldStopNonActiveRunsWithoutCleanup()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Blocked"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldDetectStalledRunsFromLastActivity()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            startedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            lastEventAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        var retry = await harness.DbContext.RetryQueue.SingleAsync();
        Assert.Equal(RunStatusNames.Retrying, run.Status);
        Assert.Equal(1, retry.Attempt);
    }

    [Fact]
    public async Task RunTickAsync_ShouldReconcileBeforeSkippingInvalidDispatch()
    {
        var workflow = BuildWorkflowDefinition(maxConcurrentAgents: 1, apiKey: "$MISSING_TOKEN");

        await using var harness = await TestHarness.CreateAsync(
            workflow,
            tracker: new FakeTrackerClient([]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            startedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            lastEventAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.False(harness.Tracker.FetchCandidateIssuesCalled);
        Assert.Equal(RunStatusNames.Retrying, (await harness.DbContext.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldNotRecoverRunsOwnedByInstancesWithLiveLease()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-2");
        await harness.InsertLeaseAsync("poll-dispatch", "instance-2", DateTimeOffset.UtcNow.AddMinutes(5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        var claim = await harness.DbContext.DispatchClaims.SingleAsync();

        Assert.Equal("instance-2", run.OwnerInstanceId);
        Assert.Equal(RunStatusNames.Running, run.Status);
        Assert.Equal("instance-2", claim.ClaimedByInstanceId);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
    }

    private static WorkflowDefinition BuildWorkflowDefinition(
        int maxConcurrentAgents,
        IReadOnlyList<string>? activeStates = null,
        IReadOnlyDictionary<string, int>? maxConcurrentByState = null,
        string apiKey = "test-token")
    {
        var runtime = new WorkflowRuntimeSettings(
            new WorkflowTrackerSettings(
                Kind: "github",
                Endpoint: "https://api.github.com/graphql",
                ApiKey: apiKey,
                Owner: "released",
                Repo: "symphony",
                Milestone: null,
                IncludePullRequests: true,
                Labels: [],
                ActiveStates: activeStates ?? ["Open"],
                TerminalStates: ["Closed"]),
            new WorkflowPollingSettings(600_000),
            new WorkflowAgentSettings(
                MaxConcurrentAgents: maxConcurrentAgents,
                MaxTurns: 20,
                MaxRetryBackoffMs: 300_000,
                MaxConcurrentAgentsByState: maxConcurrentByState ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)),
            new WorkflowServerSettings(Port: null),
            new WorkflowWorkspaceSettings("./workspaces", "./workspaces/repo", "./workspaces/worktrees", "main", null),
            new WorkflowHooksSettings(null, null, null, null, 60_000),
            new WorkflowCodexSettings("codex app-server", 30_000, "never", "danger-full-access", "danger-full-access", 5_000, 300_000));

        return new WorkflowDefinition(new Dictionary<string, object?>(), "Prompt body", runtime, "WORKFLOW.md", DateTimeOffset.UtcNow);
    }

    private static NormalizedIssue BuildIssue(
        string id,
        string identifier,
        string state,
        IReadOnlyList<BlockerRef>? blockedBy,
        IReadOnlyList<PullRequestRef>? pullRequests = null)
    {
        return new NormalizedIssue(
            id,
            identifier,
            $"Issue {identifier}",
            null,
            1,
            state,
            null,
            null,
            null,
            [],
            pullRequests ?? [],
            blockedBy ?? [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly string dbPath;

        private TestHarness(
            string dbPath,
            SymphonyDbContext dbContext,
            FakeTrackerClient tracker,
            FakeWorkspaceManager workspaceManager,
            FakeIssueExecutionCoordinator coordinator,
            OrchestrationTickService service)
        {
            this.dbPath = dbPath;
            DbContext = dbContext;
            Tracker = tracker;
            WorkspaceManager = workspaceManager;
            Coordinator = coordinator;
            Service = service;
        }

        public SymphonyDbContext DbContext { get; }
        public FakeTrackerClient Tracker { get; }
        public FakeWorkspaceManager WorkspaceManager { get; }
        public FakeIssueExecutionCoordinator Coordinator { get; }
        public OrchestrationTickService Service { get; }

        public static async Task<TestHarness> CreateAsync(
            WorkflowDefinition workflowDefinition,
            FakeTrackerClient tracker,
            FakeIssueExecutionCoordinator coordinator)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-orchestration.db");
            var options = new DbContextOptionsBuilder<SymphonyDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            var dbContext = new SymphonyDbContext(options);
            await dbContext.Database.EnsureDeletedAsync();
            await dbContext.Database.EnsureCreatedAsync();

            var workspaceManager = new FakeWorkspaceManager();
            coordinator.Attach(dbContext, dbPath);

            var service = new OrchestrationTickService(
                new FakeWorkflowDefinitionProvider(workflowDefinition),
                tracker,
                new OrchestrationCoordinationStore(dbContext, TimeProvider.System),
                dbContext,
                workspaceManager,
                coordinator,
                Options.Create(new OrchestrationOptions
                {
                    InstanceId = "instance-1",
                    LeaseName = "poll-dispatch",
                    LeaseTtlSeconds = 900
                }),
                TimeProvider.System,
                NullLogger<OrchestrationTickService>.Instance);

            return new TestHarness(dbPath, dbContext, tracker, workspaceManager, coordinator, service);
        }

        public async Task InsertRunningRunAsync(
            string issueId,
            string identifier,
            string state,
            string instanceId,
            DateTimeOffset? startedAtUtc = null,
            DateTimeOffset? lastEventAtUtc = null)
        {
            var run = new RunEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                IssueId = issueId,
                IssueIdentifier = identifier,
                OwnerInstanceId = instanceId,
                Status = RunStatusNames.Running,
                State = state,
                StartedAtUtc = startedAtUtc ?? DateTimeOffset.UtcNow
            };
            run.LastEventAtUtc = lastEventAtUtc;

            DbContext.Runs.Add(run);
            DbContext.RunAttempts.Add(new RunAttemptEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                RunId = run.Id,
                IssueId = issueId,
                Status = RunStatusNames.Running,
                StartedAtUtc = run.StartedAtUtc
            });
            DbContext.DispatchClaims.Add(new DispatchClaimEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                ClaimedByInstanceId = instanceId,
                ClaimedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Status = "active"
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertLeaseAsync(string leaseName, string ownerInstanceId, DateTimeOffset expiresAtUtc)
        {
            DbContext.InstanceLeases.Add(new InstanceLeaseEntity
            {
                LeaseName = leaseName,
                OwnerInstanceId = ownerInstanceId,
                AcquiredAtUtc = expiresAtUtc.AddMinutes(-5),
                ExpiresAtUtc = expiresAtUtc,
                UpdatedAtUtc = expiresAtUtc.AddMinutes(-1)
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertIssueCacheAsync(
            string issueId,
            string identifier,
            string state,
            DateTimeOffset? cachedAtUtc = null)
        {
            var nowUtc = cachedAtUtc ?? DateTimeOffset.UtcNow;
            DbContext.IssueCache.Add(new IssueCacheEntity
            {
                IssueId = issueId,
                Identifier = identifier,
                Title = $"Issue {identifier}",
                State = state,
                LabelsJson = "[]",
                PullRequestsJson = "[]",
                BlockedByJson = "[]",
                CachedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertRunAsync(
            string issueId,
            string identifier,
            string state,
            string instanceId,
            string status,
            string? sessionId = null,
            DateTimeOffset? completedAtUtc = null)
        {
            DbContext.Runs.Add(new RunEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                IssueId = issueId,
                IssueIdentifier = identifier,
                OwnerInstanceId = instanceId,
                Status = status,
                State = state,
                SessionId = sessionId,
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAtUtc = completedAtUtc
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertRetryingRunAsync(
            string issueId,
            string identifier,
            string state,
            string instanceId,
            int inputTokens = 0,
            int outputTokens = 0,
            int totalTokens = 0,
            int lastReportedInputTokens = 0,
            int lastReportedOutputTokens = 0,
            int lastReportedTotalTokens = 0,
            string delayType = RetryDelayTypes.Backoff,
            string? sessionId = null)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var run = new RunEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                IssueId = issueId,
                IssueIdentifier = identifier,
                OwnerInstanceId = instanceId,
                Status = RunStatusNames.Retrying,
                State = state,
                CurrentRetryAttempt = 1,
                SessionId = sessionId,
                StartedAtUtc = nowUtc.AddMinutes(-1),
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = totalTokens,
                LastReportedInputTokens = lastReportedInputTokens,
                LastReportedOutputTokens = lastReportedOutputTokens,
                LastReportedTotalTokens = lastReportedTotalTokens
            };

            DbContext.Runs.Add(run);
            DbContext.RetryQueue.Add(new RetryQueueEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                RunId = run.Id,
                OwnerInstanceId = instanceId,
                Attempt = 1,
                DueAtUtc = nowUtc.AddSeconds(-1),
                DelayType = delayType,
                MaxBackoffMs = 300_000,
                CreatedAtUtc = nowUtc.AddMinutes(-1),
                UpdatedAtUtc = nowUtc.AddMinutes(-1)
            });
            DbContext.DispatchClaims.Add(new DispatchClaimEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                ClaimedByInstanceId = instanceId,
                ClaimedAtUtc = nowUtc.AddMinutes(-1),
                UpdatedAtUtc = nowUtc.AddMinutes(-1),
                Status = "active"
            });
            DbContext.WorkspaceRecords.Add(new WorkspaceRecordEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                WorkspacePath = $"C:\\tmp\\{identifier}",
                BranchName = $"symphony/{identifier}",
                LastPreparedAtUtc = nowUtc.AddMinutes(-1)
            });

            await DbContext.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            TryDeleteFile(dbPath);
            TryDeleteFile($"{dbPath}-wal");
            TryDeleteFile($"{dbPath}-shm");
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
    }

    private sealed class FakeWorkflowDefinitionProvider(WorkflowDefinition workflowDefinition) : IWorkflowDefinitionProvider
    {
        public Task<WorkflowDefinition> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(workflowDefinition);
    }

    private sealed class FakeTrackerClient(
        IReadOnlyList<NormalizedIssue> issues,
        IReadOnlyDictionary<string, string>? issueStatesById = null,
        bool throwOnFetchStatesByIds = false) : IGitHubTrackerClient
    {
        private readonly Dictionary<string, string> statesById = issueStatesById is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(issueStatesById, StringComparer.OrdinalIgnoreCase);

        public bool FetchCandidateIssuesCalled { get; private set; }

        public Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(TrackerQuery query, CancellationToken cancellationToken = default)
        {
            FetchCandidateIssuesCalled = true;
            return Task.FromResult(issues);
        }

        public Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(TrackerQuery query, IReadOnlyList<string> states, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);

        public Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(TrackerQuery query, IReadOnlyList<string> issueIds, CancellationToken cancellationToken = default)
        {
            if (throwOnFetchStatesByIds)
            {
                throw new InvalidOperationException("simulated tracker outage");
            }

            var snapshots = issueIds
                .Where(id => statesById.ContainsKey(id))
                .Select(id => new IssueStateSnapshot(id, statesById[id]))
                .ToList();
            return Task.FromResult<IReadOnlyList<IssueStateSnapshot>>(snapshots);
        }

        public Task<GitHubGraphQlExecutionResult> ExecuteGitHubGraphQlAsync(
            TrackerQuery query,
            string graphQlDocument,
            string? variablesJson,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitHubGraphQlExecutionResult(true, "{\"data\":{}}"));
        }
    }

    private sealed class FakeWorkspaceManager : IWorkspaceManager
    {
        public List<WorkspaceCleanupRequest> CleanupRequests { get; } = [];

        public Task<WorkspacePreparationResult> PrepareIssueWorkspaceAsync(WorkspacePreparationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspacePreparationResult($"C:\\tmp\\{request.IssueIdentifier}", request.SuggestedBranchName ?? "branch", CreatedNow: true));

        public Task<WorkspaceCleanupResult> CleanupIssueWorkspaceAsync(WorkspaceCleanupRequest request, CancellationToken cancellationToken = default)
        {
            CleanupRequests.Add(request);
            return Task.FromResult(new WorkspaceCleanupResult($"C:\\tmp\\{request.IssueIdentifier}", Existed: true, RemovedNow: true));
        }
    }

    private enum FakeDispatchOutcome
    {
        LeaveRunning,
        Success,
        Failure
    }

    private sealed class FakeIssueExecutionCoordinator(
        FakeDispatchOutcome outcome,
        bool stopReturnsFalse = false,
        bool observeStopStateWithFreshContext = false) : IIssueExecutionCoordinator
    {
        private SymphonyDbContext? dbContext;
        private string? dbPath;

        public List<IssueExecutionRequest> StartRequests { get; } = [];
        public List<string> StopRequests { get; } = [];
        public (string? RequestedStopReason, bool CleanupWorkspaceOnStop)? ObservedStopState { get; private set; }

        public void Attach(SymphonyDbContext dbContext, string dbPath)
        {
            this.dbContext = dbContext;
            this.dbPath = dbPath;
        }

        public async Task<bool> TryStartAsync(IssueExecutionRequest request, CancellationToken cancellationToken = default)
        {
            StartRequests.Add(request);
            if (dbContext is null || outcome == FakeDispatchOutcome.LeaveRunning)
            {
                return true;
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var run = await dbContext.Runs.SingleAsync(runEntity => runEntity.Id == request.RunId, cancellationToken);
            var attempt = await dbContext.RunAttempts.SingleAsync(attemptEntity => attemptEntity.Id == request.AttemptId, cancellationToken);

            if (outcome == FakeDispatchOutcome.Success)
            {
                // Mirrors IssueExecutionCoordinator: a successful bounded execution is
                // terminal for the dispatch — no continuation retry, claim released.
                run.Status = RunStatusNames.Succeeded;
                run.CurrentRetryAttempt = null;
                run.CompletedAtUtc = nowUtc;
                attempt.Status = RunStatusNames.Succeeded;
                attempt.CompletedAtUtc = nowUtc;

                var claim = await dbContext.DispatchClaims.SingleOrDefaultAsync(
                    entity => entity.IssueId == request.Issue.Id && entity.Status == "active",
                    cancellationToken);
                if (claim is not null)
                {
                    claim.Status = RunStatusNames.Succeeded;
                    claim.ReleasedAtUtc = nowUtc;
                    claim.UpdatedAtUtc = nowUtc;
                }
            }
            else
            {
                var retryAttempt = request.Attempt.HasValue ? request.Attempt.Value + 1 : 1;
                run.Status = RunStatusNames.Retrying;
                run.CurrentRetryAttempt = retryAttempt;
                attempt.Status = RunStatusNames.Failed;
                attempt.Error = "simulated failure";
                attempt.CompletedAtUtc = nowUtc;
                dbContext.RetryQueue.Add(new RetryQueueEntity
                {
                    IssueId = request.Issue.Id,
                    IssueIdentifier = request.Issue.Identifier,
                    RunId = request.RunId,
                    OwnerInstanceId = request.InstanceId,
                    Attempt = retryAttempt,
                    DueAtUtc = nowUtc.AddSeconds(10),
                    DelayType = RetryDelayTypes.Backoff,
                    Error = "simulated failure",
                    MaxBackoffMs = request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task<bool> TryStopAsync(string issueId, CancellationToken cancellationToken = default)
        {
            StopRequests.Add(issueId);

            if (observeStopStateWithFreshContext && dbPath is not null)
            {
                var options = new DbContextOptionsBuilder<SymphonyDbContext>()
                    .UseSqlite($"Data Source={dbPath}")
                    .Options;
                await using var freshDbContext = new SymphonyDbContext(options);
                var run = await freshDbContext.Runs.SingleAsync(
                    runEntity => runEntity.IssueId == issueId,
                    cancellationToken);
                ObservedStopState = (run.RequestedStopReason, run.CleanupWorkspaceOnStop);
            }

            return !stopReturnsFalse;
        }
    }
}
