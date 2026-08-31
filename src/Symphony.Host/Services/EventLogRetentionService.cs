using Microsoft.EntityFrameworkCore;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed record EventLogPruneResult(int Deleted, bool Ran, string Reason);

/// <summary>
/// Keeps the event log from growing without bound.
///
/// The log is append-only and roughly 96% of it is agent streaming chatter -
/// 48,795 <c>item/agentMessage/delta</c> rows out of 98,299 when this was
/// written, with the database at 104 MB after three days. Left alone it grows
/// forever.
///
/// Retention is split by what a row IS, not only by how old it is. Streaming and
/// transport events stop being useful once the run they describe is over, so they
/// get days. The operational record - dispatches, phase changes, verdicts,
/// merges, escalations - is the durable audit trail and is kept far longer.
/// <see cref="DashboardEventPresentation.Classify"/> decides which is which, so
/// the rule is exactly the one the Control Room already shows you: if it is not
/// worth displaying, it is not worth keeping long. Change that classifier and you
/// change this, deliberately.
///
/// Deletes are cursor-paged and capped per run so a first prune of a large table
/// cannot hold a long write lock against the live engine.
/// </summary>
public sealed class EventLogRetentionService(
    SymphonyDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<EventLogRetentionService> logger)
{
    private const int PageSize = 5_000;
    private const int MaxDeletesPerRun = 50_000;

    public async Task<EventLogPruneResult> PruneAsync(
        WorkflowEventLogRetentionSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            return new EventLogPruneResult(0, Ran: false, "event log retention is disabled in the workflow");
        }

        var now = timeProvider.GetUtcNow();
        var protocolCutoff = now.AddDays(-settings.ProtocolRetentionDays);
        var operationalCutoff = now.AddDays(-settings.OperationalRetentionDays);

        var deleted = 0;
        long cursor = 0;

        while (deleted < MaxDeletesPerRun && !cancellationToken.IsCancellationRequested)
        {
            // Page by Id ascending: the log is append-only, so this walks oldest
            // first. Timestamps are compared in memory rather than in SQL because
            // SQLite stores DateTimeOffset as text and ordering on it is not
            // reliably translatable.
            var page = await dbContext.EventLog
                .AsNoTracking()
                .Where(entry => entry.Id > cursor)
                .OrderBy(entry => entry.Id)
                .Take(PageSize)
                .Select(entry => new
                {
                    entry.Id,
                    entry.OccurredAtUtc,
                    entry.EventName,
                    entry.Message
                })
                .ToListAsync(cancellationToken);

            if (page.Count == 0)
            {
                break;
            }

            cursor = page[^1].Id;

            var doomed = page
                .Where(entry =>
                    entry.OccurredAtUtc < operationalCutoff ||
                    (entry.OccurredAtUtc < protocolCutoff &&
                     DashboardEventPresentation.Classify(entry.EventName, entry.Message)
                         == DashboardEventPresentation.EventClass.Protocol))
                .Select(entry => entry.Id)
                .ToList();

            if (doomed.Count > 0)
            {
                deleted += await dbContext.EventLog
                    .Where(entry => doomed.Contains(entry.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            // Everything from here on is newer than both cutoffs, so nothing
            // further can qualify on age. Stop rather than scan the whole table.
            if (page[^1].OccurredAtUtc >= protocolCutoff && page[^1].OccurredAtUtc >= operationalCutoff)
            {
                break;
            }
        }

        // Backstop against a burst that outruns the age windows entirely. This can
        // remove operational rows, so it is deliberately generous and reported
        // separately - if it ever fires regularly, the age windows are wrong.
        var capDeleted = await EnforceRowCapAsync(settings.MaxRows, cancellationToken);
        if (capDeleted > 0)
        {
            logger.LogWarning(
                "Event log row cap of {MaxRows} was exceeded; {CapDeleted} of the oldest rows were removed. "
                + "Consider shortening event_log_retention.protocol_event_days instead.",
                settings.MaxRows,
                capDeleted);
        }

        var total = deleted + capDeleted;
        if (total > 0)
        {
            logger.LogInformation(
                "Pruned {Total} event log rows ({AgeDeleted} by age, {CapDeleted} by row cap).",
                total,
                deleted,
                capDeleted);
        }

        return new EventLogPruneResult(
            total,
            Ran: true,
            total == 0 ? "nothing was old enough to prune" : $"pruned {total} rows");
    }

    private async Task<int> EnforceRowCapAsync(int maxRows, CancellationToken cancellationToken)
    {
        if (maxRows <= 0)
        {
            return 0;
        }

        var total = await dbContext.EventLog.CountAsync(cancellationToken);
        var excess = total - maxRows;
        if (excess <= 0)
        {
            return 0;
        }

        var doomed = await dbContext.EventLog
            .AsNoTracking()
            .OrderBy(entry => entry.Id)
            .Take(Math.Min(excess, MaxDeletesPerRun))
            .Select(entry => entry.Id)
            .ToListAsync(cancellationToken);

        return doomed.Count == 0
            ? 0
            : await dbContext.EventLog
                .Where(entry => doomed.Contains(entry.Id))
                .ExecuteDeleteAsync(cancellationToken);
    }
}
