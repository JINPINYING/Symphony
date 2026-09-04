namespace Symphony.Core.Models;

// The floor under every runner's idea of success (ADCP#29, owner item 29).
//
// Success was decided from the exit code alone. That is a proxy, and it is the
// wrong one: a vendor that refuses to start can still return cleanly, and on
// 2026-09-04 four cross-vendor reviews did exactly that - recorded `Succeeded`,
// zero tokens in, zero tokens out, no verdict comment, four escalations blaming
// the reviewer for a contract violation it was never given the chance to break.
//
// A run that consumed no tokens and produced no assistant output did not run.
// That holds whatever the exit code says and whatever shape the vendor chose to
// report its refusal in, which is why the floor is asserted here rather than by
// enumerating the error payloads any one CLI happens to emit this month.
//
// It is a floor, not a judgement: a run that got as far as saying ANYTHING is
// left alone for the ordinary success rules to decide.
public sealed class AgentRunActivity
{
    /// <summary>Error code recorded for a run that produced nothing at all.</summary>
    public const string FloorErrorCode = "no_agent_activity";

    /// <summary>Error code recorded for a runner that refused on quota or a rate limit.</summary>
    public const string QuotaErrorCode = "runner_quota_exhausted";

    /// <summary>A token count above zero was reported at some point in the run.</summary>
    public bool SawTokens { get; private set; }

    /// <summary>The agent said something - a message, an item, a tool call.</summary>
    public bool SawAssistantOutput { get; private set; }

    /// <summary>Nothing was consumed and nothing was produced: this run did not run.</summary>
    public bool ProducedNothing => !SawTokens && !SawAssistantOutput;

    public string FloorMessage(int exitCode) =>
        $"{FloorErrorCode}: the runner exited with code {exitCode} having consumed 0 tokens and produced no " +
        "assistant output. A run that produced nothing did not run, so it is not recorded as a success.";

    public void RecordTokens(int? inputTokens, int? outputTokens, int? totalTokens)
    {
        if (inputTokens > 0 || outputTokens > 0 || totalTokens > 0)
        {
            SawTokens = true;
        }
    }

    public void RecordTokens(AgentRunUpdate update) =>
        RecordTokens(update.InputTokens, update.OutputTokens, update.TotalTokens);

    public void RecordAssistantOutput(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            SawAssistantOutput = true;
        }
    }
}
