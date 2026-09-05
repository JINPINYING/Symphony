using System.Text;

namespace Symphony.Host.Services;

/// <summary>
/// Turns the raw event log into something a human can read.
///
/// The log records every protocol event an agent transport emits. That is right
/// for diagnostics and wrong for a status page: roughly 96% of rows are streaming
/// deltas, and many of the rest carry a "message" that is just the event name
/// echoed back, so the feed renders as screens of identical cards that look like
/// duplicate execution when they are only stream lifecycle.
///
/// Nothing here deletes or stops persisting anything. This is presentation only:
/// the default view shows operational activity, and the raw feed stays available
/// on demand (ADCP #4).
/// </summary>
internal static class DashboardEventPresentation
{
    public enum EventClass
    {
        /// <summary>Something actually happened: dispatch, phase change, verdict, merge, error.</summary>
        Operational,

        /// <summary>Transport and streaming lifecycle. Hidden unless raw events are requested.</summary>
        Protocol
    }

    // Streaming and transport chatter. These are the bulk of the log by volume and
    // carry no standalone meaning - the useful content arrives on completion.
    // Exposed so the query layer can exclude them in SQL instead of paging through
    // them; this set remains the authority either way.
    internal static readonly HashSet<string> ProtocolEventNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "item/agentMessage/delta",
        "item/commandExecution/outputDelta",
        "item/commandExecution/terminalInteraction",
        "item/started",
        "thread/started",
        "thread/status/changed",
        "thread/tokenUsage/updated",
        "turn/started",
        "turn/diff/updated",
        "account/rateLimits/updated",
        "rate_limits_updated",
        "mcpServer/startupStatus/updated",
        "skills/changed",
        "remoteControl/status/changed",
        "claude_assistant",
        "claude_user",
        "claude_system_thinking_tokens",
        "claude_system_init",
        "claude_system_task_started",
        "claude_system_task_notification",
        "claude_system_vcs_state_changed",
        "claude_rate_limit_event"

        // Deliberately NOT listed: "other_message". It is a fallback name that
        // sometimes carries a real note worth reading, and the echo rule below
        // already suppresses the empty and self-naming cases. This list is only
        // for channels that are noise even when they carry text.
    };

    // Human-readable names for the operational vocabulary. Anything not listed
    // falls back to a title-cased form of the event name, so a new event type is
    // readable the day it is introduced rather than only after this map is edited.
    private static readonly Dictionary<string, string> FriendlyLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["candidate_discovered"] = "Candidate found",
        ["candidate_acquisition_delayed"] = "Acquisition delayed",
        ["claim_attempted"] = "Claim attempted",
        ["claim_succeeded"] = "Claimed",
        ["claim_refused"] = "Claim refused",
        ["issue_dispatched"] = "Dispatched",
        ["dispatch_started"] = "Dispatch started",
        ["implementation_redispatch_blocked"] = "Redispatch blocked",
        ["phase_ledger_created"] = "Phases started",
        ["phase_verify_passed"] = "Verified",
        ["phase_verify_failed"] = "Verification failed",
        ["phase_review_dispatched"] = "Review dispatched",
        ["phase_review_redispatch"] = "Review re-dispatched",
        ["phase_repair_dispatched"] = "Repair dispatched",
        ["phase_repair_deferred"] = "Repair deferred",
        ["phase_repair_redispatch"] = "Repair re-dispatched",
        ["phase_ready"] = "Approved",
        ["phase_merged"] = "Merged",
        ["phase_escalated"] = "Escalated",
        ["phase_parked_run_reconciled"] = "Un-parked",
        ["run_completed"] = "Run completed",
        ["retry_scheduled"] = "Retry scheduled",
        ["needs_command_center"] = "Needs command center",
        ["escalation_posted"] = "Escalation posted",
        ["directive_consumed_closed"] = "Directive: closed",
        ["directive_consumed_resumed"] = "Directive: resumed",
        ["session_started"] = "Session started",
        ["turn_completed"] = "Turn completed",
        ["process_started"] = "Process started",
        ["process_completed"] = "Process completed",
        ["claude_started"] = "Claude started",
        ["claude_result"] = "Claude result",
        ["item/completed"] = "Step completed",
        ["error"] = "Error",
        ["warning"] = "Warning"
    };

    public static EventClass Classify(string? eventName, string? message)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return EventClass.Protocol;
        }

        // An event whose message is only its own name carries no information. This
        // is what made the feed unreadable: claude_assistant -> "claude_assistant"
        // repeated down the page. Judge by content, not just by a name list, so
        // new echo-only event types are caught without a code change.
        if (IsEchoMessage(eventName, message))
        {
            return EventClass.Protocol;
        }

        return ProtocolEventNames.Contains(eventName) ? EventClass.Protocol : EventClass.Operational;
    }

    public static bool ShouldInclude(string? eventName, string? message) =>
        Classify(eventName, message) == EventClass.Operational;

    /// <summary>The message worth showing, or null when the event only echoed its own name.</summary>
    public static string? GetVisibleMessage(string? eventName, string? message) =>
        IsEchoMessage(eventName, message) ? null : NullIfBlank(message);

    /// <summary>A short human label for the event, always non-empty.</summary>
    public static string GetLabel(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return "Event";
        }

        return FriendlyLabels.TryGetValue(eventName, out var label) ? label : Humanize(eventName);
    }

    private static bool IsEchoMessage(string? eventName, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        return string.Equals(message.Trim(), eventName?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    // "phase_review_dispatched" -> "Phase review dispatched"
    // "item/commandExecution/outputDelta" -> "Output delta"
    private static string Humanize(string eventName)
    {
        var segments = eventName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var lastSegment = segments.Length > 0 ? segments[^1] : eventName;

        var spaced = new StringBuilder(lastSegment.Length + 8);
        foreach (var character in lastSegment.Replace('_', ' ').Replace('-', ' '))
        {
            // Split camelCase so "outputDelta" reads as "output delta".
            if (char.IsUpper(character) && spaced.Length > 0 && spaced[^1] != ' ')
            {
                spaced.Append(' ');
            }

            spaced.Append(char.ToLowerInvariant(character));
        }

        var text = spaced.ToString().Trim();
        return text.Length == 0 ? "Event" : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
