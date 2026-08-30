namespace Symphony.Core.Models;

public sealed record AgentRunRequest
{
    public AgentRunRequest(
        string IssueId,
        string IssueIdentifier,
        string IssueTitle,
        string WorkspacePath,
        string Prompt,
        string Command,
        int TimeoutMs,
        int MaxTurns,
        string ApprovalPolicy,
        string ThreadSandbox,
        string TurnSandboxPolicy,
        int ReadTimeoutMs,
        TrackerQuery? TrackerQuery = null)
    {
        this.IssueId = IssueId;
        this.IssueIdentifier = IssueIdentifier;
        this.IssueTitle = IssueTitle;
        this.WorkspacePath = WorkspacePath;
        this.Prompt = Prompt;
        this.Command = Command;
        this.TimeoutMs = TimeoutMs;
        this.MaxTurns = MaxTurns <= 0 ? MaxTurns : 1;
        this.ApprovalPolicy = ApprovalPolicy;
        this.ThreadSandbox = ThreadSandbox;
        this.TurnSandboxPolicy = TurnSandboxPolicy;
        this.ReadTimeoutMs = ReadTimeoutMs;
        this.TrackerQuery = TrackerQuery;
    }

    public string IssueId { get; }
    public string IssueIdentifier { get; }
    public string IssueTitle { get; }
    public string WorkspacePath { get; }
    public string Prompt { get; }
    public string Command { get; }
    public int TimeoutMs { get; }

    // Safety invariant: one Symphony dispatch owns exactly one Codex turn.
    // Further implementation work must be an explicit new control-plane phase/dispatch,
    // never an implicit continuation inside the same agent session.
    public int MaxTurns { get; }

    public string ApprovalPolicy { get; }
    public string ThreadSandbox { get; }
    public string TurnSandboxPolicy { get; }
    public int ReadTimeoutMs { get; }
    public TrackerQuery? TrackerQuery { get; }
}
