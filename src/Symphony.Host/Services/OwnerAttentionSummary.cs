using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Host.Services;

/// <summary>One thing the owner may need to act on.</summary>
public sealed record AttentionItem(string Label, string Detail, string Severity);

/// <summary>
/// The answer to "does this need me?", computed by the engine rather than
/// assembled by hand somewhere else.
///
/// This exists because the owner-facing status page and the engine's diagnostic
/// page were two separate things with two names, and the owner-facing one went
/// stale whenever nobody was around to write it. The engine already holds every
/// fact needed to answer the question, so it answers it here and both surfaces
/// render the same result.
///
/// Deliberately conservative about what counts as needing a person. An idle
/// plane is the normal, healthy state - it means the queue is empty, not that
/// something is wrong - and reporting idleness as a problem is how a status page
/// teaches its reader to ignore it.
/// </summary>
public static class OwnerAttentionSummary
{
    public const string LevelClear   = "clear";
    public const string LevelAttention = "attention";
    public const string LevelDown    = "down";

    public static (string Level, string Headline, string Detail, IReadOnlyList<AttentionItem> Items) Build(
        bool engineHealthy,
        IReadOnlyList<RunEntity> escalatedRuns,
        int runningCount,
        int retryQueueCount,
        IReadOnlyList<PhaseLedgerEntity> phases,
        DateTimeOffset? lastEventAtUtc,
        DateTimeOffset now)
    {
        var items = new List<AttentionItem>();

        if (!engineHealthy)
        {
            items.Add(new AttentionItem(
                "The engine is not answering",
                "Nothing will be picked up until it is back. Check the service and its logs.",
                LevelDown));
        }

        foreach (var run in escalatedRuns)
        {
            var posted = run.EscalationPostedAtUtc is not null;
            items.Add(new AttentionItem(
                $"{run.IssueIdentifier} needs a decision",
                posted
                    ? "Escalated and posted to GitHub. Reply with a symphony:directive comment to un-park it."
                    : "Escalated but the GitHub comment has not posted yet - the publisher may be failing.",
                posted ? LevelAttention : LevelDown));
        }

        // A PR that reached ready and stayed there means the merge gate refused it
        // for a reason a person has to resolve.
        foreach (var phase in phases.Where(p => string.Equals(p.Stage, PhaseStages.Escalated, StringComparison.Ordinal)))
        {
            items.Add(new AttentionItem(
                $"{phase.IssueIdentifier} stopped at the merge gate",
                $"PR #{phase.PrNumber} was approved but not merged. The gate escalates rather than merging when a change touches a protected path.",
                LevelAttention));
        }

        if (retryQueueCount > 0)
        {
            items.Add(new AttentionItem(
                $"{retryQueueCount} run{(retryQueueCount == 1 ? string.Empty : "s")} waiting to retry",
                "Transient failures retry on their own. Persistent ones escalate.",
                LevelAttention));
        }

        var level = items.Any(i => i.Severity == LevelDown) ? LevelDown
                  : items.Count > 0 ? LevelAttention
                  : LevelClear;

        string headline;
        string detail;

        if (level == LevelDown)
        {
            headline = "Something is wrong and it will not clear itself";
            detail = "The items below are blocking work. Nothing new will be picked up until they are resolved.";
        }
        else if (level == LevelAttention)
        {
            headline = items.Count == 1 ? "One thing is waiting on you" : $"{items.Count} things are waiting on you";
            detail = "The plane is running normally. These are decisions it will not make on its own.";
        }
        else if (runningCount > 0)
        {
            headline = runningCount == 1 ? "Working on one issue" : $"Working on {runningCount} issues";
            detail = "Nothing needs you. Progress appears below as each phase completes.";
        }
        else
        {
            headline = "Nothing needs you";
            // Idle is the correct resting state, and saying so plainly is the
            // difference between a page you trust and one you learn to ignore.
            detail = lastEventAtUtc is null
                ? "The plane is idle and waiting for work to be labelled."
                : $"The plane is idle by construction - the queue is empty, not stalled. Last activity {Humanise(now - lastEventAtUtc.Value)} ago.";
        }

        return (level, headline, detail, items);
    }

    private static string Humanise(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "less than a minute";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} minute{Plural((int)span.TotalMinutes)}";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hour{Plural((int)span.TotalHours)}";
        return $"{(int)span.TotalDays} day{Plural((int)span.TotalDays)}";
    }

    private static string Plural(int value) => value == 1 ? string.Empty : "s";
}
