namespace Symphony.Infrastructure.Workflows.Models;

public sealed record WorkflowRuntimeSettings(
    WorkflowTrackerSettings Tracker,
    WorkflowPollingSettings Polling,
    WorkflowAgentSettings Agent,
    WorkflowServerSettings Server,
    WorkflowWorkspaceSettings Workspace,
    WorkflowHooksSettings Hooks,
    WorkflowCodexSettings Codex,
    WorkflowClaudeSettings Claude,
    WorkflowMergePolicySettings MergePolicy,
    WorkflowEventLogRetentionSettings EventLogRetention,
    // Trailing with a default so the six existing construction sites keep
    // compiling; an absent list simply means nothing is being watched.
    IReadOnlyList<WorkflowWatchedTaskSettings>? WatchedTasks = null);

// The plane is woken by schedulers it does not own, and until now it could not
// see them. When the artifact publisher stopped being started, the engine had no
// way to know: from inside, a scheduler that never fires and a quiet week look
// identical. Naming the tasks here makes their silence legible.
//
// ExpectEveryMinutes is what the schedule promises, not a deadline - lateness is
// judged at a multiple of it (see WatchedTaskEvaluator), so ordinary host jitter
// does not raise an alarm. LateAfterMinutes overrides that when a task deserves
// a tighter or looser leash than the default.
public sealed record WorkflowWatchedTaskSettings(
    string Name,
    string Path,
    int ExpectEveryMinutes,
    int? LateAfterMinutes);

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

// M6: the policy gate for autonomous merges (blueprint decision 8, tier 1).
// Enabled must be opted into explicitly. A pull request touching any protected
// path is never merged autonomously — it escalates to the command center, which
// is the conservative stand-in until dual-vendor review (tier 2) exists.
// ADCP #4 follow-up: the event log grows without bound. ~96% of rows are agent
// streaming deltas that stop being useful within hours, while the operational
// events - dispatches, phase changes, verdicts, merges - are the durable record
// and are kept far longer. Retention is therefore split by what the row IS, not
// just by age.
public sealed record WorkflowEventLogRetentionSettings(
    bool Enabled,
    int ProtocolRetentionDays,
    int OperationalRetentionDays,
    int MaxRows);

public sealed record WorkflowMergePolicySettings(
    bool Enabled,
    string Method,
    IReadOnlyList<string> ProtectedPaths,
    int MaxChangedFiles);
