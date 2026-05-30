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
