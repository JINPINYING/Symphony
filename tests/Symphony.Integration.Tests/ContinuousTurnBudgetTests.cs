using System.Reflection;
using Symphony.Core.Models;
using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Integration.Tests;

public sealed class ContinuousTurnBudgetTests
{
    [Theory]
    [InlineData(0, 20, false)]
    [InlineData(39, 20, false)]
    [InlineData(40, 20, true)]
    [InlineData(41, 20, true)]
    [InlineData(2, 1, true)]
    [InlineData(2147483647, 2147483647, false)]
    public void HasExceededContinuousTurnBudget_ShouldBoundLiveContinuationLoops(
        int turnCount,
        int maxTurns,
        bool expected)
    {
        var method = typeof(OrchestrationTickService).GetMethod(
            "HasExceededContinuousTurnBudget",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var run = new RunEntity { TurnCount = turnCount };
        var workflow = BuildWorkflowDefinition(maxTurns);

        var actual = (bool)method!.Invoke(null, [run, workflow])!;

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(6, 1)]
    [InlineData(20, 1)]
    public void AgentRunRequest_ShouldEnforceOneTurnPerDispatch(int configuredMaxTurns, int expectedMaxTurns)
    {
        var request = new AgentRunRequest(
            IssueId: "issue-id",
            IssueIdentifier: "#88",
            IssueTitle: "bounded execution",
            WorkspacePath: ".",
            Prompt: "work once",
            Command: "codex app-server",
            TimeoutMs: 900_000,
            MaxTurns: configuredMaxTurns,
            ApprovalPolicy: "never",
            ThreadSandbox: "danger-full-access",
            TurnSandboxPolicy: "danger-full-access",
            ReadTimeoutMs: 5_000);

        Assert.Equal(expectedMaxTurns, request.MaxTurns);
    }

    private static WorkflowDefinition BuildWorkflowDefinition(int maxTurns)
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
                MaxTurns: maxTurns,
                MaxRetryBackoffMs: 300_000,
                MaxConcurrentAgentsByState: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)),
            new WorkflowServerSettings(Port: null),
            new WorkflowWorkspaceSettings("./workspaces", "./workspaces/repo", "./workspaces/worktrees", "main", null),
            new WorkflowHooksSettings(null, null, null, null, 60_000),
            new WorkflowCodexSettings("codex app-server", 30_000, "never", "danger-full-access", "danger-full-access", 5_000, 300_000));

        return new WorkflowDefinition(
            new Dictionary<string, object?>(),
            "Prompt body",
            runtime,
            "WORKFLOW.md",
            DateTimeOffset.UtcNow);
    }
}
