using Symphony.Core.Models;

namespace Symphony.Core.Tests;

public sealed class AgentQuotaSignalsTests
{
    [Theory]
    // The message that actually stopped the plane, verbatim from the run record.
    [InlineData("You've hit your session limit · resets 1:40am (America/New_York)", true)]
    [InlineData("Usage limit reached for this account", true)]
    [InlineData("429 Too Many Requests", true)]
    [InlineData("insufficient_quota", true)]
    // Ordinary failures must NOT reroute: repairing your own work and having a
    // different vendor redo it are not the same thing.
    [InlineData("stall timeout exceeded", false)]
    [InlineData("build failed: 3 errors in Symphony.Host", false)]
    [InlineData("the tests are red", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsQuotaExhaustion_ShouldOnlyMatchExhaustion(string? text, bool expected)
    {
        Assert.Equal(expected, AgentQuotaSignals.IsQuotaExhaustion(text));
    }

    [Theory]
    [InlineData("codex", "claude", "codex")]
    [InlineData("claude", "codex", "claude")]
    // Falling back to the vendor that just ran out cannot help, so it is not a
    // fallback at all.
    [InlineData("codex", "codex", null)]
    [InlineData("CODEX", "codex", null)]
    // Nothing configured, or something unrecognised: stay with the current runner.
    [InlineData(null, "claude", null)]
    [InlineData("", "claude", null)]
    [InlineData("gemini", "claude", null)]
    public void ResolveFallbackRunner_ShouldRefuseMeaninglessFallbacks(
        string? configuredFallback,
        string? exhaustedRunner,
        string? expected)
    {
        Assert.Equal(expected, AgentQuotaSignals.ResolveFallbackRunner(configuredFallback, exhaustedRunner));
    }
}
