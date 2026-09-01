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

// One repository the plane watches, and the workspace it works it in.
//
// The workspace fields are per repository rather than global because they have
// to be: two repositories can each have an issue #115, and a single clone path
// or worktrees root would put both issues in the same directory. Separate roots
// also mean branch names cannot collide, since symphony/115 in one repository
// and symphony/115 in another are simply different branches in different repos.
public sealed record WorkflowRepositorySettings(
    string Owner,
    string Repo,
    string SharedClonePath,
    string WorktreesRoot,
    string RemoteUrl)
{
    // "owner/repo" - the durable key recorded on runs, ledgers and cache rows so
    // a later phase can rebuild the query for the repository the work came from.
    public string Key => $"{Owner}/{Repo}";
}

public sealed record WorkflowTrackerSettings(
    string Kind,
    string Endpoint,
    string ApiKey,
    // Owner/Repo remain the PRIMARY repository rather than the only one. Keeping
    // them means a single-repository config, the preflight validator and every
    // existing consumer behave exactly as before; Repositories is what makes more
    // than one possible.
    string Owner,
    string Repo,
    string? Milestone,
    bool IncludePullRequests,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> ActiveStates,
    IReadOnlyList<string> TerminalStates,
    // Trailing with a default so existing construction sites keep compiling. An
    // empty list means "just the primary repository", which the loader fills in.
    IReadOnlyList<WorkflowRepositorySettings>? Repositories = null)
{
    public IReadOnlyList<WorkflowRepositorySettings> TrackedRepositories =>
        Repositories is { Count: > 0 } configured
            ? configured
            : [new WorkflowRepositorySettings(Owner, Repo, string.Empty, string.Empty, string.Empty)];

    public WorkflowRepositorySettings PrimaryRepository => TrackedRepositories[0];

    public bool IsMultiRepository => TrackedRepositories.Count > 1;

    public WorkflowRepositorySettings? FindRepository(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (var repository in TrackedRepositories)
        {
            if (repository.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return repository;
            }
        }

        return null;
    }
}

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
    IReadOnlyDictionary<string, string> RunnerByLabel,
    // ADCP#24: which runner takes over when the dispatched one is out of quota,
    // and ONLY then. Null disables the fallback. An ordinary failure stays with
    // the vendor that produced it - only exhaustion, which no number of retries
    // can clear, justifies changing who runs.
    string? FallbackRunner = null);

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
