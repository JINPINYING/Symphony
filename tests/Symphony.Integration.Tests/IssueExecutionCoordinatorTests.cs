using System.Reflection;
using Symphony.Core.Models;
using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Integration.Tests;

public sealed class IssueExecutionCoordinatorTests
{
    [Fact]
    public void ApplyTokenTotals_ShouldAccumulateTurnUsageDeltasAndReconcileAbsoluteSnapshots()
    {
        var run = new RunEntity();
        var applyTokenTotals = typeof(IssueExecutionCoordinator)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method =>
            {
                if (method.Name != "ApplyTokenTotals")
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 2 &&
                       parameters[0].ParameterType == typeof(RunEntity) &&
                       parameters[1].ParameterType == typeof(AgentRunUpdate);
            });

        applyTokenTotals.Invoke(null, [run, CreateTokenUpdate(10, 4, 14, tokenUsageIsDelta: true)]);
        applyTokenTotals.Invoke(null, [run, CreateTokenUpdate(10, 4, 14, tokenUsageIsDelta: true)]);
        applyTokenTotals.Invoke(null, [run, CreateTokenUpdate(30, 12, 42, tokenUsageIsDelta: false)]);

        Assert.Equal(30, run.InputTokens);
        Assert.Equal(12, run.OutputTokens);
        Assert.Equal(42, run.TotalTokens);
        Assert.Equal(30, run.LastReportedInputTokens);
        Assert.Equal(12, run.LastReportedOutputTokens);
        Assert.Equal(42, run.LastReportedTotalTokens);
    }

    // ADCP#23. The live-agent half of the same defect: when the tick asks a running
    // agent to stop because the startup budget is spent, the coordinator must finalize
    // that run terminally. Mapping it to "stalled" schedules a retry the claim store
    // then fences forever, and the run never leaves 'retrying'.
    [Theory]
    [InlineData(RunStopReasons.Terminal, RunStatusNames.CanceledByReconciliation, false)]
    [InlineData(RunStopReasons.Inactive, RunStatusNames.CanceledByReconciliation, false)]
    [InlineData(RunStopReasons.Stalled, RunStatusNames.Stalled, true)]
    [InlineData(RunStopReasons.StartupExhausted, RunStatusNames.NeedsCommandCenter, false)]
    [InlineData(null, RunStatusNames.Failed, true)]
    public void ResolveStopOutcome_ShouldOnlyRetryStopReasonsThatCanStillMakeProgress(
        string? stopReason,
        string expectedStatus,
        bool expectedRetry)
    {
        var outcome = IssueExecutionCoordinator.ResolveStopOutcome(stopReason, cleanupWorkspaceOnStop: false);

        Assert.Equal(expectedStatus, outcome.FinalStatus);
        Assert.Equal(expectedRetry, outcome.Retry);
    }

    [Fact]
    public void ResolveStopOutcome_ShouldReleaseTheClaimWhenStartupBudgetIsExhausted()
    {
        var outcome = IssueExecutionCoordinator.ResolveStopOutcome(
            RunStopReasons.StartupExhausted,
            cleanupWorkspaceOnStop: false);

        // Without this the issue keeps its active claim and the only agent slot with it.
        Assert.True(outcome.ReleaseClaim);
        Assert.Equal(RunStatusNames.NeedsCommandCenter, outcome.ReleaseStatus);
    }

    private static AgentRunUpdate CreateTokenUpdate(
        int inputTokens,
        int outputTokens,
        int totalTokens,
        bool tokenUsageIsDelta)
    {
        return new AgentRunUpdate(
            EventType: "turn/completed",
            Timestamp: DateTimeOffset.UtcNow,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: totalTokens,
            TokenUsageIsDelta: tokenUsageIsDelta);
    }
}
