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

/// <summary>
/// A directive the owner posts from the status page. The repository is carried
/// because an issue id is global but the tracker query is not, and "#142" exists
/// in every repository the plane watches.
/// </summary>
public sealed record DirectiveActionRequest(
    string? IssueId,
    string? IssueIdentifier,
    string? Repository,
    string? Action,
    string? Phase);

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

    /// <summary>The shortest thing that can honestly be called a report.</summary>
    public const int MinSummaryLength = 12;

    /// <summary>
    /// The longest run of non-space characters a summary may contain. A real
    /// sentence has none this long; a pasted token, a base64 blob or a stuck key
    /// does, and it renders as an unbroken bar across the most prominent panel on
    /// the page.
    /// </summary>
    public const int MaxWordLength = 60;

    /// <summary>
    /// Why a summary is not a report, or null when it is fine.
    ///
    /// Endpoint probes reached the owner's page and stayed there: a bare "a", the
    /// word "test", and two hundred consecutive x's, sitting above real work in
    /// the panel that answers "what is the team doing". Nothing rejected them,
    /// because the only rule was a length ceiling.
    ///
    /// These checks are deliberately blunt - length, a space, no absurd run of
    /// characters. Anything cleverer starts guessing at meaning and will one day
    /// throw away a real report that happened to be terse, which is the worse
    /// failure: a dropped report looks exactly like an idle plane, and that is the
    /// bug this whole feed exists to prevent. The caller is told why, rather than
    /// having the post silently discarded.
    /// </summary>
    public static string? DescribeRejection(string summary)
    {
        if (summary.Length < MinSummaryLength)
        {
            return $"a summary of {summary.Length} character(s) is too short to be a report; say what is being worked on.";
        }

        if (!summary.Any(char.IsWhiteSpace))
        {
            return "a summary should be a sentence, not a single token.";
        }

        var longestWord = summary
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Max(word => word.Length);
        if (longestWord > MaxWordLength)
        {
            return $"a summary contains a {longestWord}-character run with no break, which is not prose and does not render.";
        }

        return null;
    }
}
