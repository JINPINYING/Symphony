namespace Symphony.Core.Models;

// The agent runners Symphony can dispatch (M4). Recorded per run so orchestration
// (stall detection, dashboards) can reason per-runner.
public static class AgentRunnerNames
{
    public const string Codex = "codex";
    public const string Claude = "claude";

    public static readonly string[] All = [Codex, Claude];

    public static bool IsKnown(string? name) =>
        name is not null && All.Contains(name, StringComparer.Ordinal);
}

// build-stamp: m4b-1788130263
