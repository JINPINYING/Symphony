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
        var method = typeof(OrchestrationTickService).GetMethod(
            "ResolveStartupAttemptTimeout",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var workflow = BuildWorkflowDefinition(stallTimeoutMs);
        var timeout = (TimeSpan)method!.Invoke(null, [workflow])!;
        Assert.Equal(expectedTimeoutMs, (int)timeout.TotalMilliseconds);
    }

    private static WorkflowDefinition BuildWorkflowDefinition(int stallTimeoutMs)
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
                Labels: [],
                ActiveStates: ["Open"],
                TerminalStates: ["Closed"]),
            new WorkflowPollingSettings(600_000),
            new WorkflowAgentSettings(
                MaxConcurrentAgents: 1,
                MaxTurns: 20,
                MaxRetryBackoffMs: 300_000,
                MaxConcurrentAgentsByState: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)),
            new WorkflowServerSettings(Port: null),
            new WorkflowWorkspaceSettings("./workspaces", "./workspaces/repo", "./workspaces/worktrees", "main", null),
            new WorkflowHooksSettings(null, null, null, null, 60_000),
            new WorkflowCodexSettings("codex app-server", 30_000, "never", "danger-full-access", "danger-full-access", 5_000, stallTimeoutMs));

        return new WorkflowDefinition(
            new Dictionary<string, object?>(),
            "Prompt body",
            runtime,
            "WORKFLOW.md",
            DateTimeOffset.UtcNow);
    }
}
