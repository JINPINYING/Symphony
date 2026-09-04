using System.Text;
using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Tracker.GitHub;

namespace Symphony.Host.Services;

// M1: publishes every needs_command_center escalation as a durable GitHub comment
// on the source issue so escalations reach the owner outside the local machine.
//
// Idempotency is layered. The durable EscalationPostedAtUtc flag on the run is the
// primary dedupe; the HTML marker embedded in the comment plus check-before-post
// covers the crash window between a successful post and the flag being saved. A
// failed post (or an issue that cannot be resolved by id) leaves the escalation
// pending, and the ordinary tick loop retries it until it is durably published.
public sealed class EscalationPublisher(
    SymphonyDbContext dbContext,
    IGitHubTrackerClient trackerClient,
    TimeProvider timeProvider,
    ILogger<EscalationPublisher> logger)
{
    // Reasons can carry arbitrary blocker payloads; keep the public comment bounded.
    private const int MaxReasonLength = 1500;

    public static string MarkerFor(string runId) => $"<!-- symphony:escalation:{runId} -->";

    public async Task PublishPendingEscalationsAsync(TrackerQuery query, CancellationToken cancellationToken)
    {
        List<RunEntity> pending;
        try
        {
            // SQLite cannot ORDER BY DateTimeOffset; sort in memory (see the
            // startup-attempt guard fix for the same provider limitation).
            pending = await dbContext.Runs
                .Where(run => run.Status == RunStatusNames.NeedsCommandCenter && run.EscalationPostedAtUtc == null)
                .ToListAsync(cancellationToken);
            pending.Sort(static (left, right) => Comparer<DateTimeOffset?>.Default.Compare(left.LastEventAtUtc, right.LastEventAtUtc));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load pending escalations; publication will retry next tick.");
            return;
        }

        foreach (var run in pending)
        {
            try
            {
                await PublishOneAsync(query, run, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Publication must never take down the tick, and a failed post must
                // keep the escalation pending — lost escalations are the exact
                // failure class M1 exists to eliminate.
                logger.LogWarning(
                    ex,
                    "Escalation for run {RunId} (issue {IssueIdentifier}) could not be published; it stays pending and will retry next tick.",
                    run.Id,
                    run.IssueIdentifier);
            }
        }
    }

    private async Task PublishOneAsync(TrackerQuery query, RunEntity run, CancellationToken cancellationToken)
    {
        var marker = MarkerFor(run.Id);
        var snapshot = await trackerClient.FetchIssueCommentMarkerAsync(
            query,
            run.IssueId,
            marker,
            run.IssueIdentifier,
            cancellationToken);
        if (snapshot is null)
        {
            logger.LogWarning(
                "Escalation for run {RunId} could not resolve issue {IssueIdentifier} by id; it stays pending and will retry next tick.",
                run.Id,
                run.IssueIdentifier);
            return;
        }

        string? commentUrl = null;
        var alreadyPosted = snapshot.MarkerFound;
        if (!alreadyPosted)
        {
            commentUrl = await trackerClient.PostIssueCommentAsync(
                query,
                run.IssueId,
                BuildCommentBody(run, marker),
                cancellationToken);
        }

        var nowUtc = timeProvider.GetUtcNow();
        run.EscalationPostedAtUtc = nowUtc;
        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = run.IssueId,
            IssueIdentifier = run.IssueIdentifier,
            RunId = run.Id,
            EventName = "escalation_posted",
            Level = LogLevel.Information.ToString(),
            Message = alreadyPosted
                ? "Escalation comment already present on the issue (marker found); recorded as posted."
                : $"Escalation comment posted on {run.IssueIdentifier}{(commentUrl is null ? "." : $": {commentUrl}")}",
            OccurredAtUtc = nowUtc
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Escalation for run {RunId} (issue {IssueIdentifier}) is durably published on GitHub.",
            run.Id,
            run.IssueIdentifier);
    }

    private static string BuildCommentBody(RunEntity run, string marker)
    {
        var builder = new StringBuilder();
        builder.AppendLine(marker);
        builder.AppendLine("## Symphony escalation — needs command center");
        builder.AppendLine();
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine($"| Issue | {EscapeCell(run.IssueIdentifier)} |");
        builder.AppendLine($"| Status | `{RunStatusNames.NeedsCommandCenter}` |");
        builder.AppendLine($"| Phase | `{EscapeCell(run.Phase)}` |");
        builder.AppendLine($"| Reason | {EscapeCell(Truncate(run.LastMessage))} |");
        builder.AppendLine($"| Run id | `{EscapeCell(run.Id)}` |");
        builder.AppendLine($"| Session | {(string.IsNullOrWhiteSpace(run.SessionId) ? "none" : $"`{EscapeCell(run.SessionId)}`")} |");
        builder.AppendLine($"| Escalated at | {(run.LastEventAtUtc ?? run.CompletedAtUtc)?.ToString("u") ?? "unknown"} |");
        builder.AppendLine($"| Tokens in/out/total | {run.InputTokens}/{run.OutputTokens}/{run.TotalTokens} |");
        builder.AppendLine();
        builder.AppendLine(
            "**To resolve:** reply on this issue with a `symphony:directive` block, or handle it in the command center. " +
            "This escalation is posted exactly once per run; the hidden marker at the top keys idempotency.");
        return builder.ToString();
    }

    private static string? Truncate(string? value)
    {
        if (value is null || value.Length <= MaxReasonLength)
        {
            return value;
        }

        return $"{value[..MaxReasonLength]} … (truncated)";
    }

    private static string EscapeCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal)
            .Replace("\r", "<br>", StringComparison.Ordinal);
    }
}
