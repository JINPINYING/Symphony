namespace Symphony.Host.Services;

/// <summary>
/// A note from an agent working outside the engine's own queue.
///
/// Symphony reports what it dispatched. That was the whole story while the plane
/// was building itself, and it stopped being the whole story the moment an agent
/// started doing product work directly - the page showed an empty queue and
/// called it idle while real work was underway, which is the one thing a status
/// page must never do.
///
/// So agents that are not runs can say what they are doing. Deliberately a
/// report, not a claim of control: nothing here dispatches, schedules or blocks
/// anything. It exists to keep the page honest about whether something is
/// happening.
/// </summary>
public sealed record AgentActivityReport(
    string Actor,
    string Summary,
    string? Detail,
    string? Url,
    DateTimeOffset AtUtc);

/// <summary>The request body agents post. Every field is optional at the wire
/// level so a malformed post fails validation with a clear reason rather than a
/// deserialization error.</summary>
public sealed record AgentActivityRequest(string? Actor, string? Summary, string? Detail, string? Url);

public static class AgentActivity
{
    public const string EventName = "agent_activity_reported";

    /// <summary>
    /// How long a report keeps the page saying work is underway. Long enough to
    /// span a slow build or a long agent turn, short enough that a session which
    /// died without saying goodbye stops claiming to be alive.
    /// </summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(15);

    public const int MaxFieldLength = 400;

    /// <summary>
    /// Reports arrive as free text over a local endpoint and are rendered on the
    /// owner's page, so they are bounded here rather than trusted. Length only -
    /// escaping belongs to the renderer, which already does it.
    /// </summary>
    public static string? Clamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= MaxFieldLength ? trimmed : trimmed[..MaxFieldLength];
    }
}
