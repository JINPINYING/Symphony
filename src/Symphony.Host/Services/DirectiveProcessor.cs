using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

// A validated directive authorizing a dispatch (M3).
public sealed record DirectiveDispatchContext(
    string Action,
    string? Phase,
    string? Instructions);

// M3: consumes command-center directives posted as comments on escalated issues.
//
// One comment un-parks a stuck issue: resume / reimplement / custom dispatch the
// issue (at the recorded or named phase, with instructions embedded in the
// prompt), close closes the source issue. Exactly-once is layered like the M1
// escalation publisher: the durable directive_log row (keyed by comment id) is
// the primary dedupe, and the ack comment's marker covers the crash window
// between acting and recording. A malformed directive is answered with the parse
// error and consumed — the processor never guesses. A directive that cannot be
// acted on yet (no free agent slot, claim refused, tracker outage) stays
// unconsumed and is retried by the ordinary tick loop.
public sealed class DirectiveProcessor(
    SymphonyDbContext dbContext,
    IGitHubTrackerClient trackerClient,
    TimeProvider timeProvider,
    ILogger<DirectiveProcessor> logger)
{
    private static readonly string[] AuthorizedAssociations = ["OWNER", "MEMBER", "COLLABORATOR"];

    public static string AckMarkerFor(string commentId) => $"<!-- symphony:directive-ack:{commentId} -->";

    public async Task ProcessPendingDirectivesAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        Func<NormalizedIssue, DirectiveDispatchContext, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        List<RunEntity> escalatedRuns;
        try
        {
            escalatedRuns = await dbContext.Runs
                .Where(run => run.Status == RunStatusNames.NeedsCommandCenter)
                .ToListAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load escalated runs for directive processing; will retry next tick.");
            return;
        }

        foreach (var issueRuns in escalatedRuns.GroupBy(run => run.IssueId, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await ProcessIssueDirectivesAsync(
                    workflowDefinition,
                    query,
                    issueRuns.ToList(),
                    dispatchAsync,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Directive processing failed for issue {IssueIdentifier}; unconsumed directives will retry next tick.",
                    issueRuns.First().IssueIdentifier);
            }
        }
    }

    private async Task ProcessIssueDirectivesAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        List<RunEntity> stuckRuns,
        Func<NormalizedIssue, DirectiveDispatchContext, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        var issueId = stuckRuns[0].IssueId;
        var issueIdentifier = stuckRuns[0].IssueIdentifier;

        var comments = await trackerClient.FetchIssueCommentsAsync(query, issueId, cancellationToken);
        if (comments.Count == 0)
        {
            return;
        }

        var consumedCommentIds = new HashSet<string>(
            await dbContext.DirectiveLog
                .Where(entry => entry.IssueId == issueId)
                .Select(entry => entry.CommentId)
                .ToListAsync(cancellationToken),
            StringComparer.Ordinal);

        foreach (var comment in comments.OrderBy(item => item.CreatedAtUtc ?? DateTimeOffset.MinValue))
        {
            if (consumedCommentIds.Contains(comment.Id))
            {
                continue;
            }

            var parsed = DirectiveParser.Parse(comment.Body);
            if (parsed.Outcome == DirectiveParseOutcome.NotADirective)
            {
                continue;
            }

            if (!IsAuthorized(comment))
            {
                logger.LogWarning(
                    "Ignoring directive comment {CommentId} on {IssueIdentifier} from unauthorized author '{Author}' ({Association}).",
                    comment.Id,
                    issueIdentifier,
                    comment.AuthorLogin ?? "unknown",
                    comment.AuthorAssociation ?? "none");
                continue;
            }

            // Crash-window dedupe: an ack comment already exists but the ledger row
            // was lost — record it as consumed without re-acting.
            var ackMarker = AckMarkerFor(comment.Id);
            if (comments.Any(item => item.Body.Contains(ackMarker, StringComparison.Ordinal)))
            {
                await RecordConsumptionAsync(
                    comment.Id, issueId, issueIdentifier,
                    parsed.Action ?? "unknown", parsed.Phase,
                    "consumed_already_acked",
                    "Ack marker found on the issue; ledger row restored without re-acting.",
                    cancellationToken);
                continue;
            }

            if (parsed.Outcome == DirectiveParseOutcome.Invalid)
            {
                await ConsumeInvalidDirectiveAsync(
                    query, comment, issueId, issueIdentifier, parsed.Error ?? "unparseable directive", cancellationToken);
                continue;
            }

            var handled = await ExecuteDirectiveAsync(
                workflowDefinition, query, comment, parsed, stuckRuns, dispatchAsync, cancellationToken);
            if (handled)
            {
                // One dispatching directive per issue per tick; later directives wait
                // so their view of the issue's state is fresh.
                return;
            }
        }
    }

    private async Task<bool> ExecuteDirectiveAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuery query,
        NormalizedIssueComment comment,
        DirectiveParseResult parsed,
        List<RunEntity> stuckRuns,
        Func<NormalizedIssue, DirectiveDispatchContext, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        var issueId = stuckRuns[0].IssueId;
        var issueIdentifier = stuckRuns[0].IssueIdentifier;
        var action = parsed.Action!;

        if (string.Equals(action, DirectiveActions.Close, StringComparison.Ordinal))
        {
            await trackerClient.CloseIssueAsync(query, issueId, cancellationToken);
            ResolveStuckRuns(stuckRuns, $"Issue closed by command-center directive (comment {comment.Id}).");
            await AckAsync(
                query, comment, issueId,
                $"**Directive executed** — `close`. The source issue was closed and its escalated run(s) marked `resolved_by_directive`.",
                cancellationToken);
            await RecordConsumptionAsync(
                comment.Id, issueId, issueIdentifier, action, parsed.Phase,
                "consumed_closed", null, cancellationToken);
            return true;
        }

        // resume / reimplement / custom all re-dispatch the issue.
        var recordedPhase = stuckRuns
            .OrderByDescending(run => run.LastEventAtUtc ?? run.StartedAtUtc)
            .First()
            .Phase;
        var targetPhase = action switch
        {
            DirectiveActions.Reimplement => RunPhaseNames.Implementation,
            _ => parsed.Phase ?? recordedPhase
        };

        // Respect the global agent slot limit; an undispatchable directive stays
        // unconsumed and retries next tick.
        var runningCount = await dbContext.Runs
            .CountAsync(run => run.Status == RunStatusNames.Running, cancellationToken);
        if (runningCount >= workflowDefinition.Runtime.Agent.MaxConcurrentAgents)
        {
            logger.LogInformation(
                "Deferring directive {CommentId} on {IssueIdentifier}: no free agent slot ({Running}/{Max}).",
                comment.Id, issueIdentifier, runningCount, workflowDefinition.Runtime.Agent.MaxConcurrentAgents);
            return false;
        }

        var issues = await trackerClient.FetchIssuesByIdsAsync(query, [issueId], cancellationToken);
        var issue = issues.FirstOrDefault();
        if (issue is null)
        {
            await ConsumeInvalidDirectiveAsync(
                query, comment, issueId, issueIdentifier,
                "the source issue could not be reloaded from the tracker by id", cancellationToken);
            return true;
        }

        if (IssueStateMatcherProxy.IsClosed(issue.State))
        {
            await ConsumeInvalidDirectiveAsync(
                query, comment, issueId, issueIdentifier,
                $"the source issue is {issue.State}; reopen it before dispatching '{action}'", cancellationToken);
            return true;
        }

        var context = new DirectiveDispatchContext(action, targetPhase, parsed.Instructions);
        var dispatched = await dispatchAsync(issue, context, cancellationToken);
        if (!dispatched)
        {
            logger.LogWarning(
                "Directive {CommentId} on {IssueIdentifier} could not dispatch (claim refused); it stays pending and will retry next tick.",
                comment.Id, issueIdentifier);
            return false;
        }

        ResolveStuckRuns(stuckRuns, $"Escalation resolved by command-center directive (comment {comment.Id}); issue re-dispatched at phase '{targetPhase}'.");
        await AckAsync(
            query, comment, issueId,
            $"**Directive executed** — `{action}` at phase `{targetPhase}`. The issue was re-dispatched" +
            (string.IsNullOrWhiteSpace(parsed.Instructions) ? "." : " with your instructions embedded in the worker prompt."),
            cancellationToken);
        await RecordConsumptionAsync(
            comment.Id, issueId, issueIdentifier, action, targetPhase,
            "consumed_dispatched", null, cancellationToken);
        return true;
    }

    private void ResolveStuckRuns(List<RunEntity> stuckRuns, string reason)
    {
        var nowUtc = timeProvider.GetUtcNow();
        foreach (var run in stuckRuns)
        {
            run.Status = RunStatusNames.ResolvedByDirective;
            run.CompletedAtUtc ??= nowUtc;
            run.LastEvent = "resolved_by_directive";
            run.LastMessage = reason;
            run.LastEventAtUtc = nowUtc;
        }
    }

    private async Task ConsumeInvalidDirectiveAsync(
        TrackerQuery query,
        NormalizedIssueComment comment,
        string issueId,
        string issueIdentifier,
        string error,
        CancellationToken cancellationToken)
    {
        await AckAsync(
            query, comment, issueId,
            $"**Directive could not be executed:** {error}.\n\nThe escalation remains open; post a corrected `symphony:directive` block. Nothing was dispatched — the control plane does not guess.",
            cancellationToken);
        await RecordConsumptionAsync(
            comment.Id, issueId, issueIdentifier,
            "invalid", null, "consumed_invalid", error, cancellationToken);
    }

    private async Task AckAsync(
        TrackerQuery query,
        NormalizedIssueComment comment,
        string issueId,
        string message,
        CancellationToken cancellationToken)
    {
        var body = $"{AckMarkerFor(comment.Id)}\n{message}\n\n— Symphony directive processor";
        await trackerClient.PostIssueCommentAsync(query, issueId, body, cancellationToken);
    }

    private async Task RecordConsumptionAsync(
        string commentId,
        string issueId,
        string issueIdentifier,
        string action,
        string? phase,
        string outcome,
        string? detail,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        dbContext.DirectiveLog.Add(new DirectiveLogEntity
        {
            CommentId = commentId,
            IssueId = issueId,
            IssueIdentifier = issueIdentifier,
            Action = action,
            Phase = phase,
            Outcome = outcome,
            Detail = detail,
            ConsumedAtUtc = nowUtc
        });
        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = issueId,
            IssueIdentifier = issueIdentifier,
            EventName = "directive_" + outcome,
            Level = LogLevel.Information.ToString(),
            Message = $"Directive comment {commentId}: {outcome}{(detail is null ? "." : $" — {detail}")}",
            OccurredAtUtc = nowUtc
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Directive comment {CommentId} on {IssueIdentifier} consumed with outcome {Outcome}.",
            commentId, issueIdentifier, outcome);
    }

    private static bool IsAuthorized(NormalizedIssueComment comment) =>
        comment.AuthorAssociation is not null &&
        AuthorizedAssociations.Contains(comment.AuthorAssociation, StringComparer.OrdinalIgnoreCase);

    // The tracker normalizes issue state to "Open"/"Closed"; keep the check in one
    // place so a future state vocabulary change has a single seam.
    private static class IssueStateMatcherProxy
    {
        public static bool IsClosed(string state) =>
            string.Equals(state, "Closed", StringComparison.OrdinalIgnoreCase);
    }
}
