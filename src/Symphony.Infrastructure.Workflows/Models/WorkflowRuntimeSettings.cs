namespace Symphony.Infrastructure.Workflows.Models;

public sealed record WorkflowRuntimeSettings(
    WorkflowTrackerSettings Tracker,
    WorkflowPollingSettings Polling,
    WorkflowAgentSettings Agent,
    WorkflowServerSettings Server,
    WorkflowWorkspaceSettings Workspace,
    WorkflowHooksSettings Hooks,
    WorkflowCodexSettings Codex,
    WorkflowClaudeSettings Claude);

public sealed record WorkflowTrackerSettings(
    string Kind,
    string Endpoint,
    string ApiKey,
    string Owner,
    string Repo,
    string? Milestone,
    bool IncludePullRequests,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> ActiveStates,
    IReadOnlyList<string> TerminalStates);

public sealed record WorkflowPollingSettings(int IntervalMs);

public sealed record WorkflowAgentSettings(
    int MaxConcurrentAgents,
    int MaxTurns,
    int MaxRetryBackoffMs,
    IReadOnlyDictionary<string, int> MaxConcurrentAgentsByState,
    // M4 rollout (blueprint decision 7): which agent runner implements an issue.
    // DefaultRunner applies unless one of the issue's labels appears in
    // RunnerByLabel (first matching label wins). Valid runners: codex, claude.
    string DefaultRunner,
    IReadOnlyDictionary<string, string> RunnerByLabel);

public sealed record WorkflowServerSettings(int? Port);

public sealed record WorkflowWorkspaceSettings(
    string Root,
    string SharedClonePath,
    string WorktreesRoot,
    string BaseBranch,
    string? RemoteUrl);

public sealed record WorkflowHooksSettings(
    string? AfterCreate,
    string? BeforeRun,
    string? AfterRun,
    string? BeforeRemove,
    int TimeoutMs);

public sealed record WorkflowCodexSettings(
    string Command,
    int TurnTimeoutMs,
    string ApprovalPolicy,
    string ThreadSandbox,
    string TurnSandboxPolicy,
    int ReadTimeoutMs,
    int StallTimeoutMs);

// M4: headless Claude Code as an agent runner. Timeouts are tuned separately
// from Codex — Claude emits stream events at a different cadence and a slower
// implementer must not trip false stall detection.
public sealed record WorkflowClaudeSettings(
    string Command,
    int TurnTimeoutMs,
    string PermissionMode,
    string? Model,
    int StallTimeoutMs);
