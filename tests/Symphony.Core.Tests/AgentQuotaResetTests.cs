using Symphony.Core.Models;

namespace Symphony.Core.Tests;

public sealed class AgentQuotaResetTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 21, 31, 0, TimeSpan.Zero);

    // The message that parked four pull requests, verbatim from the CLI.
    [Fact]
    public void TryParse_ShouldReadTheWallClockTheCodexCliPrints()
    {
        var reset = AgentQuotaReset.TryParse(
            "ERROR: You've hit your usage limit. Upgrade to Pro or try again at 7:24 PM.",
            Now);

        Assert.NotNull(reset);

        // Printed by a CLI running on this host, so it is read in this host's zone.
        var local = reset!.Value.ToLocalTime();
        Assert.Equal(19, local.Hour);
        Assert.Equal(24, local.Minute);
        Assert.True(reset > Now, "a reset is always ahead of now");
    }

    [Fact]
    public void TryParse_ShouldReadTheWallClockTheClaudeCliPrints()
    {
        var reset = AgentQuotaReset.TryParse(
            "You've hit your session limit · resets 1:40am (America/New_York)",
            Now);

        Assert.NotNull(reset);

        var local = reset!.Value.ToLocalTime();
        Assert.Equal(1, local.Hour);
        Assert.Equal(40, local.Minute);
        Assert.True(reset > Now);
    }

    // A structured reset is not open to the zone ambiguity of a printed one, so it
    // wins over everything else in the same payload.
    [Fact]
    public void TryParse_ShouldPreferSecondsFromAnAppServerRateLimitsBlock()
    {
        var reset = AgentQuotaReset.TryParse(
            """{"rate_limits":{"primary":{"used_percent":100,"resets_in_seconds":900}},"message":"try again at 7:24 PM"}""",
            Now);

        Assert.Equal(Now.AddMinutes(15), reset);
    }

    [Fact]
    public void TryParse_ShouldReadAUnixInstantAsAnInstantRatherThanADelay()
    {
        var resetsAt = Now.AddHours(2).ToUnixTimeSeconds();

        var reset = AgentQuotaReset.TryParse("{\"rate_limits\":{\"resets_at\":" + resetsAt + "}}", Now);

        Assert.Equal(Now.AddHours(2), reset);
    }

    [Fact]
    public void TryParse_ShouldReadAnAbsoluteTimestamp()
    {
        var reset = AgentQuotaReset.TryParse("rate limit; retry after 2026-09-04T23:24:00Z", Now);

        Assert.Equal(new DateTimeOffset(2026, 9, 4, 23, 24, 0, TimeSpan.Zero), reset);
    }

    [Theory]
    [InlineData("rate limit reached, try again in 45 minutes", 45)]
    [InlineData("quota exhausted; retry in 2 hours", 120)]
    public void TryParse_ShouldReadADurationAsTimeFromNow(string text, int expectedMinutes)
    {
        Assert.Equal(Now.AddMinutes(expectedMinutes), AgentQuotaReset.TryParse(text, Now));
    }

    // Fails closed. A refusal that named no clock this can read must not produce an
    // invented one: the caller falls back to a bounded default instead.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("You've hit your usage limit.")]
    [InlineData("build failed: 3 errors in Symphony.Host")]
    public void TryParse_ShouldReturnNullWhenNoClockIsNamed(string? text)
    {
        Assert.Null(AgentQuotaReset.TryParse(text, Now));
    }

    [Fact]
    public void Resolve_ShouldFallBackToABoundedHoldWhenNoClockIsNamed()
    {
        Assert.Equal(Now + AgentQuotaReset.DefaultHold, AgentQuotaReset.Resolve("You've hit your usage limit.", Now));
    }

    [Fact]
    public void Resolve_ShouldUseTheNamedClockWhenThereIsOne()
    {
        Assert.Equal(Now.AddMinutes(15), AgentQuotaReset.Resolve("""{"resets_in_seconds":900}""", Now));
    }
}
