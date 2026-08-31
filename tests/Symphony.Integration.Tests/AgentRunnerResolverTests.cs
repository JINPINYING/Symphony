using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;
using Symphony.Host.Services;
using Symphony.Infrastructure.Agent.Claude;
using Symphony.Infrastructure.Agent.Codex;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Integration.Tests;

public sealed class AgentRunnerResolverTests
{
    [Fact]
    public void Resolve_ShouldUseDefaultRunnerWhenNoLabelMatches()
    {
        var resolver = CreateResolver();
        var definition = BuildDefinition(defaultRunner: "codex", runnerByLabel: new Dictionary<string, string>
        {
            ["lane:control-plane"] = "claude"
        });

        var selection = resolver.Resolve(definition, BuildIssue(labels: ["backend"]));

        Assert.Equal(AgentRunnerNames.Codex, selection.RunnerName);
        Assert.IsType<CodexAgentRunner>(selection.Runner);
        Assert.Equal("codex app-server", selection.Command);
        Assert.Equal(30_000, selection.TurnTimeoutMs);
    }

    [Fact]
    public void Resolve_ShouldRouteLabeledIssueToClaudeWithClaudeSettings()
    {
        var resolver = CreateResolver();
        var definition = BuildDefinition(defaultRunner: "codex", runnerByLabel: new Dictionary<string, string>
        {
            ["lane:control-plane"] = "claude"
        });

        var selection = resolver.Resolve(definition, BuildIssue(labels: ["symphony-ready", "lane:control-plane"]));

        Assert.Equal(AgentRunnerNames.Claude, selection.RunnerName);
        var claudeRunner = Assert.IsType<ClaudeAgentRunner>(selection.Runner);
        Assert.Equal("claude", selection.Command);
        Assert.Equal(1_800_000, selection.TurnTimeoutMs);
        Assert.Equal("acceptEdits", claudeRunner.PermissionMode);
        Assert.Equal("claude-sonnet-5", claudeRunner.Model);
        Assert.Equal(480_000, claudeRunner.StallTimeoutMs);
    }

    [Fact]
    public void Resolve_ShouldUseClaudeAsDefaultRunnerWhenConfigured()
    {
        var resolver = CreateResolver();
        var definition = BuildDefinition(defaultRunner: "claude", runnerByLabel: new Dictionary<string, string>());

        var selection = resolver.Resolve(definition, BuildIssue(labels: []));

        Assert.Equal(AgentRunnerNames.Claude, selection.RunnerName);
    }

    private static AgentRunnerResolver CreateResolver()
    {
        return new AgentRunnerResolver(
            new CodexAgentRunner(new StubTrackerClient(), NullLogger<CodexAgentRunner>.Instance),
            NullLoggerFactory.Instance);
    }

    private static WorkflowDefinition BuildDefinition(
        string defaultRunner,
        IReadOnlyDictionary<string, string> runnerByLabel)
    {
        var runtime = new WorkflowRuntimeSettings(
            new WorkflowTrackerSettings(
                "github", "https://api.github.com/graphql", "token", "released", "symphony",
                null, true, [], ["Open"], ["Closed"]),
            new WorkflowPollingSettings(600_000),
            new WorkflowAgentSettings(
                MaxConcurrentAgents: 1,
                MaxTurns: 20,
                MaxRetryBackoffMs: 300_000,
                MaxConcurrentAgentsByState: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                DefaultRunner: defaultRunner,
                RunnerByLabel: runnerByLabel),
            new WorkflowServerSettings(null),
            new WorkflowWorkspaceSettings("./workspaces", "./workspaces/repo", "./workspaces/worktrees", "main", null),
            new WorkflowHooksSettings(null, null, null, null, 60_000),
            new WorkflowCodexSettings("codex app-server", 30_000, "never", "danger-full-access", "danger-full-access", 5_000, 300_000),
            new WorkflowClaudeSettings("claude", 1_800_000, "acceptEdits", "claude-sonnet-5", 480_000),
            new WorkflowMergePolicySettings(false, "squash", [], 50));

        return new WorkflowDefinition(new Dictionary<string, object?>(), "Prompt", runtime, "WORKFLOW.md", DateTimeOffset.UtcNow);
    }

    private static NormalizedIssue BuildIssue(IReadOnlyList<string> labels)
    {
        return new NormalizedIssue(
            "issue-1", "#1", "Issue #1", null, 1, "Open", null, null, null,
            labels, [], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private sealed class StubTrackerClient : ITrackerClient
    {
        public Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(TrackerQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);

        public Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(TrackerQuery query, IReadOnlyList<string> states, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);

        public Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(TrackerQuery query, IReadOnlyList<string> issueIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IssueStateSnapshot>>([]);

        public Task<GitHubGraphQlExecutionResult> ExecuteGitHubGraphQlAsync(TrackerQuery query, string graphQlDocument, string? variablesJson, CancellationToken cancellationToken = default)
            => Task.FromResult(new GitHubGraphQlExecutionResult(true, "{\"data\":{}}"));

        public Task<IssueCommentMarkerSnapshot?> FetchIssueCommentMarkerAsync(TrackerQuery query, string issueId, string marker, CancellationToken cancellationToken = default)
            => Task.FromResult<IssueCommentMarkerSnapshot?>(null);

        public Task<string?> PostIssueCommentAsync(TrackerQuery query, string issueId, string body, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<NormalizedIssueComment>> FetchIssueCommentsAsync(TrackerQuery query, string issueId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NormalizedIssueComment>>([]);

        public Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByIdsAsync(TrackerQuery query, IReadOnlyList<string> issueIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);

        public Task CloseIssueAsync(TrackerQuery query, string issueId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<PullRequestStatus?> FetchPullRequestStatusAsync(TrackerQuery query, int pullRequestNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<PullRequestStatus?>(null);

        public Task<PullRequestStatus?> FetchOpenPullRequestByHeadBranchAsync(TrackerQuery query, string headRefName, CancellationToken cancellationToken = default)
            => Task.FromResult<PullRequestStatus?>(null);

        public Task<IReadOnlyList<string>> FetchPullRequestFilesAsync(TrackerQuery query, int pullRequestNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> MergePullRequestAsync(TrackerQuery query, int pullRequestNumber, string expectedHeadSha, string method, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("merging is not supported by this fake");

        public Task RemoveIssueLabelsAsync(TrackerQuery query, string issueId, IReadOnlyList<string> labelNames, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
