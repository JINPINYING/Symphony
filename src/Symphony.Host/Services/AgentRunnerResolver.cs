using Microsoft.Extensions.Logging;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;
using Symphony.Infrastructure.Agent.Claude;
using Symphony.Infrastructure.Agent.Codex;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

// Everything a dispatch needs from the chosen runner: the runner itself plus the
// per-runner request parameters the coordinator feeds into AgentRunRequest.
public sealed record AgentRunnerSelection(
    string RunnerName,
    IAgentRunner Runner,
    string Command,
    int TurnTimeoutMs,
    string ApprovalPolicy,
    string ThreadSandbox,
    string TurnSandboxPolicy,
    int ReadTimeoutMs);

public interface IAgentRunnerResolver
{
    AgentRunnerSelection Resolve(WorkflowDefinition workflowDefinition, NormalizedIssue issue);

    AgentRunnerSelection ResolveByName(WorkflowDefinition workflowDefinition, string runnerName);
}

// M4 rollout (blueprint decision 7): an issue's labels pick its implementer.
// The first label with an entry in agent.runner_by_label wins; otherwise
// agent.default_runner applies. Claude runners are constructed per dispatch so
// live workflow-config edits (timeouts, model, permission mode) take effect on
// the next dispatch without a service restart.
public sealed class AgentRunnerResolver(
    CodexAgentRunner codexRunner,
    ILoggerFactory loggerFactory) : IAgentRunnerResolver
{
    public AgentRunnerSelection Resolve(WorkflowDefinition workflowDefinition, NormalizedIssue issue)
    {
        return ResolveByName(workflowDefinition, ResolveRunnerName(workflowDefinition.Runtime.Agent, issue.Labels));
    }

    public AgentRunnerSelection ResolveByName(WorkflowDefinition workflowDefinition, string runnerName)
    {
        if (string.Equals(runnerName, AgentRunnerNames.Claude, StringComparison.OrdinalIgnoreCase))
        {
            var settings = workflowDefinition.Runtime.Claude;
            var runner = new ClaudeAgentRunner(loggerFactory.CreateLogger<ClaudeAgentRunner>())
            {
                PermissionMode = settings.PermissionMode,
                Model = settings.Model,
                StallTimeoutMs = settings.StallTimeoutMs
            };

            // ApprovalPolicy/sandbox fields are Codex app-server concepts; the
            // Claude runner ignores them, but the request contract requires values.
            return new AgentRunnerSelection(
                AgentRunnerNames.Claude,
                runner,
                settings.Command,
                settings.TurnTimeoutMs,
                ApprovalPolicy: "never",
                ThreadSandbox: "danger-full-access",
                TurnSandboxPolicy: "danger-full-access",
                ReadTimeoutMs: 5_000);
        }

        var codex = workflowDefinition.Runtime.Codex;
        return new AgentRunnerSelection(
            AgentRunnerNames.Codex,
            codexRunner,
            codex.Command,
            codex.TurnTimeoutMs,
            codex.ApprovalPolicy,
            codex.ThreadSandbox,
            codex.TurnSandboxPolicy,
            codex.ReadTimeoutMs);
    }

    public static string ResolveRunnerName(WorkflowAgentSettings agent, IReadOnlyList<string> labels)
    {
        foreach (var label in labels)
        {
            if (agent.RunnerByLabel.TryGetValue(label, out var runner))
            {
                return runner;
            }
        }

        return agent.DefaultRunner;
    }
}
