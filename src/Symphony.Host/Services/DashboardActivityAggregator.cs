using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Host.Services;

/// <summary>One row of the Control Room event stream, after presentation.</summary>
internal sealed record DashboardActivityEntry(
    DateTimeOffset At,
    string? IssueId,
    string? IssueIdentifier,
    string? SessionId,
    string Level,
    string EventName,
    string Label,
    string? Message,
    int RepeatCount,
    bool IsProtocol);

/// <summary>
/// Folds the event log into an activity feed (ADCP #4).
///
/// Two things make the raw log unreadable. Protocol chatter drowns the signal -
/// handled by <see cref="DashboardEventPresentation"/> - and genuine repeats of
/// the same event render as a column of near-identical cards that read as
/// duplicate execution. This collapses those repeats into one row carrying a
/// count, which is the difference between "something is looping" and "one step
/// streamed nine times".
///
/// Only *adjacent* repeats collapse, so chronological order is never rearranged.
/// </summary>
internal static class DashboardActivityAggregator
{
    /// <param name="entries">Newest first.</param>
    /// <param name="includeProtocol">True for the raw diagnostic feed.</param>
    /// <param name="limit">Maximum rows to return, counted after collapsing.</param>
    public static IReadOnlyList<DashboardActivityEntry> Build(
        IEnumerable<EventLogEntity> entries,
        bool includeProtocol,
        int limit)
    {
        var result = new List<DashboardActivityEntry>(Math.Min(limit, 64));

        foreach (var entry in entries)
        {
            var eventClass = DashboardEventPresentation.Classify(entry.EventName, entry.Message);
            var isProtocol = eventClass == DashboardEventPresentation.EventClass.Protocol;

            if (isProtocol && !includeProtocol)
            {
                continue;
            }

            var message = DashboardEventPresentation.GetVisibleMessage(entry.EventName, entry.Message);

            // Collapse only against the row immediately before it: same event, same
            // issue, same visible text. Anything else starts a new row, so a real
            // change in what is happening is never hidden inside a count.
            var previous = result.Count > 0 ? result[^1] : null;
            if (previous is not null &&
                string.Equals(previous.EventName, entry.EventName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.IssueIdentifier, entry.IssueIdentifier, StringComparison.Ordinal) &&
                string.Equals(previous.Message, message, StringComparison.Ordinal))
            {
                // Keep the newest timestamp (the list is newest first) and count the repeat.
                result[^1] = previous with { RepeatCount = previous.RepeatCount + 1 };
                continue;
            }

            if (result.Count == limit)
            {
                break;
            }

            result.Add(new DashboardActivityEntry(
                At: entry.OccurredAtUtc,
                IssueId: entry.IssueId,
                IssueIdentifier: entry.IssueIdentifier,
                SessionId: entry.SessionId,
                Level: entry.Level,
                EventName: entry.EventName,
                Label: DashboardEventPresentation.GetLabel(entry.EventName),
                Message: message,
                RepeatCount: 1,
                IsProtocol: isProtocol));
        }

        return result;
    }
}
