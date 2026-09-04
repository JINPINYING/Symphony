namespace Symphony.Core.Models;

// Recognises "this vendor's account is out of quota" in whatever a runner
// happened to record about a failure (ADCP#24).
//
// It has to be a text match, because that is the only form the signal arrives
// in: the Claude CLI reports "You've hit your session limit - resets 1:40am"
// as ordinary result text, and Codex surfaces its equivalents on stderr. The
// distinction matters more than it looks. An ordinary implementation failure
// must be repaired by the SAME vendor - handing it to another one silently
// substitutes different judgement for the work already done. Only exhaustion
// justifies changing who runs, because retrying into the same wall cannot
// succeed however many attempts are left.
//
// So this deliberately fails closed: anything it does not recognise is treated
// as an ordinary failure and stays with its own vendor.
public static class AgentQuotaSignals
{
    private static readonly string[] Signals =
    [
        "session limit",
        "usage limit",
        "rate limit",
        "rate_limit",
        "quota exceeded",
        "quota exhausted",
        "out of credits",
        "out of quota",
        "insufficient_quota",
        "429 too many requests",

        // The code a runner records when it recognised the refusal itself. Every
        // other entry here is a guess at someone else's wording; this one is ours,
        // so it is the only signal that cannot drift (ADCP#29).
        AgentRunActivity.QuotaErrorCode
    ];

    public static bool IsQuotaExhaustion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var signal in Signals)
        {
            if (text.Contains(signal, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // The other vendor, or null when there is nothing to fall back to. A blank or
    // unknown configured fallback, and a fallback naming the vendor that just ran
    // out, both mean "no fallback" rather than an error: the run should retry on
    // its own runner, not be routed somewhere meaningless.
    public static string? ResolveFallbackRunner(string? configuredFallback, string? exhaustedRunner)
    {
        if (string.IsNullOrWhiteSpace(configuredFallback) ||
            !AgentRunnerNames.IsKnown(configuredFallback))
        {
            return null;
        }

        return string.Equals(configuredFallback, exhaustedRunner, StringComparison.OrdinalIgnoreCase)
            ? null
            : configuredFallback;
    }
}
