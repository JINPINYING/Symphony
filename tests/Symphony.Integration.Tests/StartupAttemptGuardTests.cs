using System.Reflection;
using Symphony.Host.Services;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Integration.Tests;

public sealed class StartupAttemptGuardTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void HasExhaustedStartupAttemptBudget_ShouldStopRepeatedPreSessionRetries(
        int attemptCount,
        bool expected)
    {
        var method = typeof(OrchestrationTickService).GetMethod(
            "HasExhaustedStartupAttemptBudget",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var actual = (bool)method!.Invoke(null, [attemptCount])!;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsStartupAttemptStale_ShouldUseStrictTimeoutBoundary()
    {
        var method = typeof(OrchestrationTickService).GetMethod(
            "IsStartupAttemptStale",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var started = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
        var timeout = TimeSpan.FromMinutes(5);

        Assert.False((bool)method!.Invoke(null, [started, started.Add(timeout), timeout])!);
        Assert.True((bool)method.Invoke(null, [started, started.Add(timeout).AddMilliseconds(1), timeout])!);
    }

    [Theory]
    [InlineData(0, 300_000)]
    [InlineData(30_000, 60_000)]
    [InlineData(120_000, 120_000)]
    [InlineData(600_000, 300_000)]
    public void ResolveStartupAttemptTimeout_ShouldRemainOperationallyBounded(
        int stallTimeoutMs,
        int expectedTimeoutMs)
    {
        var workflow = BuildWorkflowDefinition(stallTimeoutMs);
        Assert.Equal(expectedTimeoutMs, (int)ResolveStartupAttemptTimeout(workflow, "codex").TotalMilliseconds);
    }

    // ADCP#26. The guard used to read the Codex stall timeout whichever runner had
    // actually been dispatched. WORKFLOW.md gives Claude 600s and Codex 180s, so
    // every Claude run was measured against 180 seconds and killed mid-work: two
    // attempts plus the gap between them landed within 22 seconds of the observed
    // 6m07s-6m29s run lifetimes, three times running.
    [Theory]
    [InlineData("codex", 180_000)]
    [InlineData("claude", 300_000)]
    [InlineData("", 180_000)]
    public void ResolveStartupAttemptTimeout_ShouldUseTheDispatchedRunnersWindow(
        string runner,
        int expectedTimeoutMs)
    {
        var workflow = BuildWorkflowDefinition(codexStallTimeoutMs: 180_000, claudeStallTimeoutMs: 600_000);
        Assert.Equal(expectedTimeoutMs, (int)ResolveStartupAttemptTimeout(workflow, runner).TotalMilliseconds);
    }

    private static TimeSpan ResolveStartupAttemptTimeout(WorkflowDefinition workflow, string? runner)
    {
        var method = typeof(OrchestrationTickService).GetMethod(
            "ResolveStartupAttemptTimeout",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (TimeSpan)method!.Invoke(null, [workflow, runner])!;
    }

    private static WorkflowDefinition BuildWorkflowDefinition(
        int stallTimeoutMs = 300_000,
        int? codexStallTimeoutMs = null,
        int claudeStallTimeoutMs = 600_000)
    {
        var codexStall = codexStallTimeoutMs ?? stallTimeoutMs;
        var runtime = new WorkflowRuntimeSettings(
            new WorkflowTrackerSettings(
                Kind: "github",
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "test-token",
                Owner: "released",
                Repo: "symphony",
                Milestone: null,
                IncludePullRequests: true,
                Labels: [],
                ActiveStates: ["Open"],
                TerminalStates: ["Closed"]),
            new WorkflowPollingSettings(600_000),
            new WorkflowAgentSettings(
                MaxConcurrentAgents: 1,
                MaxTurns: 20,
                MaxRetryBackoffMs: 300_000,
                MaxConcurrentAgentsByState: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                DefaultRunner: "codex",
                RunnerByLabel: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            new WorkflowServerSettings(Port: null),
            new WorkflowWorkspaceSettings("./workspaces", "./workspaces/repo", "./workspaces/worktrees", "main", null),
            new WorkflowHooksSettings(null, null, null, null, 60_000),
            new WorkflowCodexSettings("codex app-server", 30_000, "never", "danger-full-access", "danger-full-access", 5_000, codexStall),
            new WorkflowClaudeSettings("claude", 30_000, "bypassPermissions", null, claudeStallTimeoutMs),
            new WorkflowMergePolicySettings(false, "squash", [], 50),
            new WorkflowEventLogRetentionSettings(
                Enabled: false,
                ProtocolRetentionDays: 3,
                OperationalRetentionDays: 180,
                MaxRows: 250_000));

        return new WorkflowDefinition(
            new Dictionary<string, object?>(),
            "Prompt body",
            runtime,
            "WORKFLOW.md",
            DateTimeOffset.UtcNow);
    }
}
