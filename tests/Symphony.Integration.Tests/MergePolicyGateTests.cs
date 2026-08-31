using Symphony.Core.Models;
using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Integration.Tests;

public sealed class MergePolicyGateTests
{
    [Fact]
    public void Evaluate_ShouldAllowApprovedPrAtTheExactHeadWithGreenChecks()
    {
        var result = MergePolicyGate.Evaluate(Policy(), Ledger(), Pr(), ["docs/notes.md"]);

        Assert.True(result.Allowed, result.Reason);
        Assert.False(result.Escalate);
    }

    [Fact]
    public void Evaluate_ShouldRefuseWhenTheHeadMovedSinceApproval()
    {
        var result = MergePolicyGate.Evaluate(Policy(), Ledger(), Pr(headSha: "bbb222"), ["docs/notes.md"]);

        Assert.False(result.Allowed);
        Assert.False(result.Escalate); // re-verify, not a command-center matter
        Assert.Contains("moved", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ShouldEscalateWhenTheVerdictIsNotApproved()
    {
        var ledger = Ledger();
        ledger.LastVerdict = ReviewVerdicts.ChangesRequired;

        var result = MergePolicyGate.Evaluate(Policy(), ledger, Pr(), ["docs/notes.md"]);

        Assert.False(result.Allowed);
        Assert.True(result.Escalate);
    }

    [Fact]
    public void Evaluate_ShouldEscalateWhenChecksAreNotGreen()
    {
        var result = MergePolicyGate.Evaluate(Policy(), Ledger(), Pr(checks: "FAILURE"), ["docs/notes.md"]);

        Assert.False(result.Allowed);
        Assert.True(result.Escalate);
    }

    [Fact]
    public void Evaluate_ShouldWaitWhileGitHubStillComputesMergeability()
    {
        var result = MergePolicyGate.Evaluate(Policy(), Ledger(), Pr(mergeable: "CONFLICTING"), ["docs/notes.md"]);

        Assert.False(result.Allowed);
        Assert.False(result.Escalate);
    }

    [Theory]
    [InlineData("src/Symphony.Host/Symphony.Host.csproj")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("src/auth/TokenStore.cs")]
    public void Evaluate_ShouldEscalateWhenAProtectedPathIsTouched(string path)
    {
        var result = MergePolicyGate.Evaluate(Policy(), Ledger(), Pr(), ["docs/notes.md", path]);

        Assert.False(result.Allowed);
        Assert.True(result.Escalate);
        Assert.Contains("protected", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ShouldEscalateOnAnEmptyChangedFileList()
    {
        var result = MergePolicyGate.Evaluate(Policy(), Ledger(), Pr(), []);

        Assert.False(result.Allowed);
        Assert.True(result.Escalate);
    }

    [Fact]
    public void Evaluate_ShouldEscalateWhenTooManyFilesChanged()
    {
        var files = Enumerable.Range(0, 60).Select(index => $"docs/file{index}.md").ToList();

        var result = MergePolicyGate.Evaluate(Policy(), Ledger(), Pr(), files);

        Assert.False(result.Allowed);
        Assert.True(result.Escalate);
    }

    [Fact]
    public void Evaluate_ShouldRefuseQuietlyWhenThePolicyIsDisabled()
    {
        var result = MergePolicyGate.Evaluate(Policy(enabled: false), Ledger(), Pr(), ["docs/notes.md"]);

        Assert.False(result.Allowed);
        Assert.False(result.Escalate);
    }

    [Theory]
    [InlineData("src/a/b/File.csproj", "**/*.csproj", true)]
    [InlineData("File.csproj", "**/*.csproj", true)]
    [InlineData("docs/readme.md", "**/*.csproj", false)]
    [InlineData(".github/workflows/ci.yml", ".github/**", true)]
    [InlineData("docs/github/notes.md", ".github/**", false)]
    [InlineData("src/auth/x.cs", "**/auth/**", true)]
    public void IsProtected_ShouldMatchGlobsWithoutOverreaching(string path, string pattern, bool expected)
    {
        Assert.Equal(expected, MergePolicyGate.IsProtected(path, [pattern]));
    }

    private static WorkflowMergePolicySettings Policy(bool enabled = true) =>
        new(enabled, "squash", ["**/*.csproj", ".github/**", "**/auth/**"], MaxChangedFiles: 50);

    private static PhaseLedgerEntity Ledger() =>
        new()
        {
            IssueId = "issue-1",
            IssueIdentifier = "#1",
            Stage = PhaseStages.Ready,
            PrNumber = 5,
            HeadSha = "aaa111",
            ImplementerRunner = AgentRunnerNames.Claude,
            LastVerdict = ReviewVerdicts.Approved,
            LastVerdictHeadSha = "aaa111"
        };

    private static PullRequestStatus Pr(
        string headSha = "aaa111",
        string? checks = "SUCCESS",
        string? mergeable = "MERGEABLE") =>
        new(5, "OPEN", IsDraft: false, headSha, checks, mergeable);
}

// build-stamp: sac-1788138095
