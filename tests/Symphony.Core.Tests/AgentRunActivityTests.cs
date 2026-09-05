using Symphony.Core.Models;

namespace Symphony.Core.Tests;

public sealed class AgentRunActivityTests
{
    // ADCP#29: the four reviews of 2026-09-04 landed `Succeeded` reporting
    // 0/0/0 tokens and no output. Nothing about that is a success.
    [Fact]
    public void ProducedNothing_ShouldBeTrueForARunThatConsumedAndProducedNothing()
    {
        var activity = new AgentRunActivity();

        Assert.True(activity.ProducedNothing);
    }

    [Fact]
    public void ProducedNothing_ShouldBeFalseOnceTokensWereConsumed()
    {
        var activity = new AgentRunActivity();

        activity.RecordTokens(inputTokens: 1_204, outputTokens: 0, totalTokens: null);

        Assert.True(activity.SawTokens);
        Assert.False(activity.ProducedNothing);
    }

    [Fact]
    public void ProducedNothing_ShouldBeFalseOnceTheAgentSaidSomething()
    {
        var activity = new AgentRunActivity();

        activity.RecordAssistantOutput("Nothing needed changing; the guard already exists.");

        Assert.True(activity.SawAssistantOutput);
        Assert.False(activity.ProducedNothing);
    }

    // Reported zeroes are the exact shape of the defect, so they must not be
    // mistaken for evidence that the run did something.
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(null, null, null)]
    [InlineData(0, null, 0)]
    public void RecordTokens_ShouldNotCountZeroesAsActivity(int? input, int? output, int? total)
    {
        var activity = new AgentRunActivity();

        activity.RecordTokens(input, output, total);

        Assert.False(activity.SawTokens);
        Assert.True(activity.ProducedNothing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordAssistantOutput_ShouldNotCountEmptyTextAsActivity(string? text)
    {
        var activity = new AgentRunActivity();

        activity.RecordAssistantOutput(text);

        Assert.False(activity.SawAssistantOutput);
        Assert.True(activity.ProducedNothing);
    }

    [Fact]
    public void RecordTokens_ShouldReadTheUpdateTheRunnerAlreadyBuilt()
    {
        var activity = new AgentRunActivity();

        activity.RecordTokens(new AgentRunUpdate(
            EventType: "thread/tokenUsage/updated",
            Timestamp: DateTimeOffset.UnixEpoch,
            InputTokens: 11,
            OutputTokens: 7,
            TotalTokens: 18));

        Assert.True(activity.SawTokens);
    }

    // The floor has to say why in the run record: "failed" with no cause is what
    // sent the commander re-probing pull requests for an hour.
    [Fact]
    public void FloorMessage_ShouldNameTheCodeAndTheExitCode()
    {
        var message = new AgentRunActivity().FloorMessage(0);

        Assert.Contains(AgentRunActivity.FloorErrorCode, message, StringComparison.Ordinal);
        Assert.Contains("exited with code 0", message, StringComparison.Ordinal);
        Assert.Contains("0 tokens", message, StringComparison.Ordinal);
    }

    // The runner's own code is a quota signal, so a refusal it recognised is still
    // recognisable to the phase machine after a trip through the database.
    [Fact]
    public void QuotaErrorCode_ShouldBeRecognisedAsQuotaExhaustion()
    {
        Assert.True(AgentQuotaSignals.IsQuotaExhaustion(
            $"{AgentRunActivity.QuotaErrorCode}: the Codex runner refused turn/start."));
    }
}
