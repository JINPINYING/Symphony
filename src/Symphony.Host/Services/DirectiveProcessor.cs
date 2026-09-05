using System.Text.Json;
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
// prompt), close closes the source issue. Exactly-once rests on the durable
// directive_log row, keyed by comment id, and every consumption writes that row -
// together with the settled run statuses and the ack still owed - before it says
// anything to the tracker, so the only thing a crash or a refused write can lose
// is a comment, which a later tick posts. A malformed directive is answered with the parse
// error and consumed — the processor never guesses. A directive that cannot be
// acted on yet (no free agent slot, claim refused, tracker outage) stays
// unconsumed and is retried by the ordinary tick loop. The one retry that is
// bounded is the issue reload: it cannot tell a rate limit from an issue that no
// longer exists, so after UnreadableDirectiveMinimumAttempts attempts over
// UnreadableDirectiveRetryWindow it is answered and consumed rather than retried
// for ever.
public sealed class DirectiveProcessor(
    SymphonyDbContext dbContext,
    IGitHubTrackerClient trackerClient,
    TimeProvider timeProvider,
    ILogger<DirectiveProcessor> logger)
{
    private static readonly string[] AuthorizedAssociations = ["OWNER", "MEMBER", "COLLABORATOR"];

    /// <summary>
    /// The event name a deferred directive is recorded under.
    ///
    /// A directive the plane has accepted but cannot act on yet - no free agent
    /// slot, claim refused - was previously visible only in a log line, so from
    /// the outside the issue looked untouched. The owner-attention panel then told
    /// the owner that its pull request was theirs to merge, while the answer they
    /// had already given sat in the queue waiting for a slot.
    /// </summary>
    public const string PendingDirectiveEvent = "directive_pending";

    /// <summary>
    /// How long a recorded pending directive is believed for.
    ///
    /// The marker is rewritten while the directive genuinely waits, so a live one
    /// is never stale. The window matters for the other case: a directive comment
    /// deleted before the plane could act on it stops being re-recorded, and a
    /// marker that never expired would go on suppressing that issue's pull request
    /// for ever.
    /// </summary>
    public static readonly TimeSpan PendingDirectiveWindow = TimeSpan.FromMinutes(15);

    // Long enough that a directive parked behind a busy queue does not add a row
    // per tick, short enough that the marker stays well inside the window above.
    private static readonly TimeSpan PendingDirectiveRefresh = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The event name the failed reloads of one directive's issue are counted
    /// under. One row per directive comment, rewritten in place.
    /// </summary>
    public const string UnreadableDirectiveEvent = "directive_unreadable";

    /// <summary>
    /// How long the plane keeps re-reading an issue it cannot read before it stops
    /// asking.
    ///
    /// An hour outlasts a GitHub REST rate-limit window, which is the longest
    /// routine outage this read can hit; anything still empty after that is not
    /// waiting on a quota.
    /// </summary>
    public static readonly TimeSpan UnreadableDirectiveRetryWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// How many empty reloads must be seen before the window above can end the
    /// retry.
    ///
    /// The window alone is crossed by a single retry after the plane has been down
    /// overnight, which would abandon a directive on the strength of one failed
    /// read. Attempts alone mean nothing, because the tick interval is
    /// configuration - three of them is seconds in a test and half an hour in
    /// production. Both together say what the abandonment notice claims: the plane
    /// asked repeatedly, over a real stretch of time, and never got an answer.
    /// </summary>
    public const int UnreadableDirectiveMinimumAttempts = 3;

    public static string AckMarkerFor(string commentId) => $"<!-- symphony:directive-ack:{commentId} -->";

    /// <summary>
    /// The event name an ack the owner is owed — persisted, not yet posted — is
    /// recorded under. One row per directive comment, deleted once the comment
    /// lands.
    /// </summary>
    /// <remarks>
    /// This row is what makes the write ordering safe. The ledger row, the settled
    /// run statuses and this debt are written in ONE SaveChanges, and the ack
    /// comment is posted afterwards.
    ///
    /// Acking first left a window the other way round: the marker was on the issue
    /// and nothing was in the database. The next tick then found the marker, wrote a
    /// bare <c>consumed_already_acked</c> ledger row — which settles no run — and
    /// skipped the directive for ever after because it was now in the ledger. The
    /// directive was consumed and the issue stayed parked at
    /// <see cref="RunStatusNames.NeedsCommandCenter"/>: the exact failure this issue
    /// exists to close, reached by a crash instead of by a misdirected read.
    ///
    /// Persisting first inverts the window into a harmless one. The directive is
    /// consumed and the runs are settled; the only thing missing is a comment, and
    /// this row is how a later tick posts it.
    /// </remarks>
    public const string AckPendingEvent = "directive_ack_pending";

    /// <summary>The ack a consumed directive is still owed, and where to post it.</summary>
    public sealed record PendingAckState(
        string CommentId,
        string RepositoryKey,
        string Body,
        int Attempts,
        DateTimeOffset? FirstAttemptAtUtc);

    /// <summary>
    /// The marker on the notice that says a valid directive is understood but could
    /// not be acted on yet.
    ///
    /// Deliberately NOT the ack marker: the ack marker means "this directive has
    /// been dealt with" and is what the crash-window dedupe looks for, so posting
    /// one for a deferral would consume the directive permanently - the exact
    /// failure this class of notice exists to report.
    /// </summary>
    public static string DeferralMarkerFor(string commentId) => $"<!-- symphony:directive-deferred:{commentId} -->";

    /// <summary>
    /// Consume the pending directives on every escalated issue, reading each issue
    /// through the query for the repository it belongs to.
    /// </summary>
    /// <remarks>
    /// This used to take a single <see cref="TrackerQuery"/> - the PRIMARY
    /// repository - whatever repository the escalated issue lived in. A node id is
    /// global and an issue number is unique only within a repository, so the wrong
    /// repository returns nothing rather than erroring; every directive on a
    /// non-primary issue read as "the issue does not exist" and was discarded, and
    /// the documented way to un-park an escalation did not work anywhere except
    /// the primary repository.
    /// </remarks>
    public async Task ProcessPendingDirectivesAsync(
        WorkflowDefinition workflowDefinition,
        TrackerQuerySet queries,
        Func<NormalizedIssue, DirectiveDispatchContext, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        // Before anything else: pay the acks that were recorded as owed but never
        // posted. Driven from the ledger of debts rather than from the escalated
        // runs below, because consuming a directive settles those runs in the same
        // SaveChanges that records the debt - an issue whose ack is owed is usually
        // an issue this loop will never look at again.
        await ReplayOwedAcksAsync(queries, cancellationToken);

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
                    queries,
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
        TrackerQuerySet queries,
        List<RunEntity> stuckRuns,
        Func<NormalizedIssue, DirectiveDispatchContext, CancellationToken, Task<bool>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        var issueId = stuckRuns[0].IssueId;
        var issueIdentifier = stuckRuns[0].IssueIdentifier;

        // The repository the escalated work actually ran in. Everything below -
        // reading the comments, reloading the issue, posting the ack, closing the
        // issue - asks GitHub about this issue by number or by node id, and both
        // answers depend on which repository is asked.
        var query = queries.For(LatestRun(stuckRuns).Repository);

        var comments = await trackerClient.FetchIssueCommentsAsync(query, issueId, issueIdentifier, cancellationToken);
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

            // An ack comment exists but no ledger row does. Since the ledger row,
            // the settled runs and the owed ack are all written BEFORE the comment
            // is posted, this can no longer be produced by the plane itself; it
            // covers rows written by an older build and markers put on the issue by
            // hand. Recorded without re-acting - the owner has already been told.
            var ackMarker = AckMarkerFor(comment.Id);
            if (comments.Any(item => item.Body.Contains(ackMarker, StringComparison.Ordinal)))
            {
                await RecordConsumptionAsync(
                    comment.Id, issueId, issueIdentifier,
                    parsed.Action ?? "unknown", parsed.Phase,
                    "consumed_already_acked",
                    "Ack marker found on the issue; ledger row restored without re-acting.",
                    owedAck: null,
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
                workflowDefinition, query, comment, comments, parsed, stuckRuns, dispatchAsync, cancellationToken);
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
        IReadOnlyList<NormalizedIssueComment> comments,
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
            await ConsumeAndAckAsync(
                query, comment, issueId, issueIdentifier, action, parsed.Phase,
                "consumed_closed", null,
                "**Directive executed** — `close`. The source issue was closed and its escalated run(s) marked `resolved_by_directive`.",
                cancellationToken);
            return true;
        }

        // resume / reimplement / custom all re-dispatch the issue.
        var recordedPhase = LatestRun(stuckRuns).Phase;
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
            await RecordPendingDirectiveAsync(
                comment.Id, issueId, issueIdentifier, action,
                $"no free agent slot ({runningCount}/{workflowDefinition.Runtime.Agent.MaxConcurrentAgents})",
                cancellationToken);
            return false;
        }

        var issues = await trackerClient.FetchIssuesByIdsAsync(
            query,
            [issueId],
            IssueIdentifierMap.For(issueId, issueIdentifier),
            cancellationToken);
        var issue = issues.FirstOrDefault();
        if (issue is null)
        {
            return await HandleUnreadableIssueAsync(
                query, comment, comments, parsed, stuckRuns, cancellationToken);
        }

        // A read that succeeded and says "Closed" is an answer, not a failure, so
        // it is consumed and answered - unlike the unreadable case above, retrying
        // it would say the same thing on every tick for ever.
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
            await RecordPendingDirectiveAsync(
                comment.Id, issueId, issueIdentifier, action, "the dispatch claim was refused", cancellationToken);
            return false;
        }

        ResolveStuckRuns(stuckRuns, $"Escalation resolved by command-center directive (comment {comment.Id}); issue re-dispatched at phase '{targetPhase}'.");
        await ConsumeAndAckAsync(
            query, comment, issueId, issueIdentifier, action, targetPhase,
            "consumed_dispatched", null,
            $"**Directive executed** — `{action}` at phase `{targetPhase}`. The issue was re-dispatched" +
            (string.IsNullOrWhiteSpace(parsed.Instructions) ? "." : " with your instructions embedded in the worker prompt."),
            cancellationToken);
        return true;
    }

    /// <summary>
    /// The reload came back empty: retry the directive, but not for ever.
    /// </summary>
    /// <remarks>
    /// An empty read says nothing about the directive, so consuming it here was
    /// the original defect - the tracker may be rate limited, flaky, or, as it was
    /// for every non-primary repository, being asked the wrong question. Only the
    /// comment text can make a directive invalid, and the parser decides that with
    /// no network call.
    ///
    /// But an empty read says nothing about the ISSUE either, and this one cannot
    /// tell the two apart: <c>ReadRestObjectAsync</c> turns a genuine 404 into the
    /// same null a rate limit produces. Retrying for ever is the first defect
    /// inverted - a permanent absence read as a transient empty - and leaves a
    /// directive that is never answered and a run parked at
    /// <see cref="RunStatusNames.NeedsCommandCenter"/>, reprocessed every tick,
    /// for good.
    ///
    /// So the retry is bounded by
    /// <see cref="UnreadableDirectiveMinimumAttempts"/> attempts AND
    /// <see cref="UnreadableDirectiveRetryWindow"/> of elapsed time, and when the
    /// bound is spent the directive IS consumed - with its own wording, which says
    /// how many times the plane asked and over how long, and which is neither the
    /// rejection ("fix your comment") nor the deferral ("still trying").
    /// </remarks>
    private async Task<bool> HandleUnreadableIssueAsync(
        TrackerQuery query,
        NormalizedIssueComment comment,
        IReadOnlyList<NormalizedIssueComment> comments,
        DirectiveParseResult parsed,
        List<RunEntity> stuckRuns,
        CancellationToken cancellationToken)
    {
        var issueId = stuckRuns[0].IssueId;
        var issueIdentifier = stuckRuns[0].IssueIdentifier;
        var action = parsed.Action!;
        var repositoryKey = TrackerQuerySet.KeyOf(query);

        var reload = await RecordUnreadableReloadAsync(
            comment.Id, issueId, issueIdentifier, action, repositoryKey, cancellationToken);

        if (!reload.Exhausted)
        {
            logger.LogWarning(
                "Directive {CommentId} on {IssueIdentifier} could not reload the issue from {Repository} " +
                "(attempt {Attempts}); it stays pending and will retry next tick.",
                comment.Id, issueIdentifier, repositoryKey, reload.Attempts);
            await RecordPendingDirectiveAsync(
                comment.Id, issueId, issueIdentifier, action,
                $"the source issue could not be read back from {repositoryKey}",
                cancellationToken);
            await PostDeferralNoticeAsync(
                query, comment, comments, issueId,
                $"the plane could not read {issueIdentifier} back from `{repositoryKey}` to act on it",
                cancellationToken);
            return false;
        }

        var attemptSummary = $"{reload.Attempts} attempts over {DescribeElapsed(reload.Elapsed)}";
        logger.LogWarning(
            "Directive {CommentId} on {IssueIdentifier} is abandoned: the issue could not be read from " +
            "{Repository} in {AttemptSummary}. The escalated run(s) are recorded as {Status}.",
            comment.Id, issueIdentifier, repositoryKey, attemptSummary, RunStatusNames.AbandonedUnreadableIssue);

        SettleStuckRuns(
            stuckRuns,
            RunStatusNames.AbandonedUnreadableIssue,
            "abandoned_unreadable_issue",
            $"Directive comment {comment.Id} was abandoned: {issueIdentifier} could not be read from " +
            $"{repositoryKey} in {attemptSummary}.");
        // The comment is the courtesy; the consumption is the guarantee. The
        // permanent absences this branch exists for - an issue deleted,
        // transferred, or recorded against a repository it no longer lives in -
        // are precisely the ones that cannot be commented on either, and
        // PostIssueCommentAsync throws on an HTTP or GraphQL error. ConsumeAndAck
        // writes the ledger row, the settled run statuses and the owed ack in one
        // SaveChanges BEFORE it goes near the tracker, so neither a refused write
        // nor a process that dies mid-post can leave this issue parked.
        await ConsumeAndAckAsync(
            query, comment, issueId, issueIdentifier, action, parsed.Phase,
            "consumed_unreadable",
            $"the source issue could not be read from {repositoryKey} in {attemptSummary}",
            $"**Directive abandoned:** the plane could not read {issueIdentifier} back from " +
            $"`{repositoryKey}` in {attemptSummary}, so it has stopped retrying and consumed this directive.\n\n" +
            "Your comment parsed correctly — this is not a rejection, and nothing was dispatched. A read that " +
            "never succeeds usually means the issue was deleted, transferred, or is recorded against a " +
            $"repository it no longer lives in. Check that {issueIdentifier} is visible in `{repositoryKey}`, " +
            "then post a fresh `symphony:directive` block. The escalated run(s) are recorded as " +
            $"`{RunStatusNames.AbandonedUnreadableIssue}` rather than left parked for ever.",
            cancellationToken);
        return true;
    }

    /// <summary>
    /// Count this failed reload against the bound, durably, and say whether the
    /// bound is now spent.
    /// </summary>
    /// <remarks>
    /// One row per directive comment, rewritten rather than appended - the tick
    /// runs every few seconds and an hour of it must not be an hour of rows. The
    /// count and the first-attempt time live in DataJson rather than in the
    /// message, for the same reason the pending marker's comment id does: a value
    /// recovered by parsing prose breaks the next time somebody improves the
    /// wording. A pruned or lost row only restarts the count, which lengthens the
    /// retry rather than shortening it.
    /// </remarks>
    private async Task<UnreadableReloadOutcome> RecordUnreadableReloadAsync(
        string commentId,
        string issueId,
        string issueIdentifier,
        string action,
        string repositoryKey,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();

        // Matched on the deserialized comment id rather than on the serialized
        // payload, because the payload changes on every attempt.
        var recorded = await dbContext.EventLog
            .Where(entry => entry.EventName == UnreadableDirectiveEvent && entry.IssueId == issueId)
            .OrderByDescending(entry => entry.Id)
            .ToListAsync(cancellationToken);

        EventLogEntity? existing = null;
        UnreadableDirectiveState? state = null;
        foreach (var entry in recorded)
        {
            var candidate = ReadUnreadableDirectiveState(entry.DataJson);
            if (candidate is not null && string.Equals(candidate.CommentId, commentId, StringComparison.Ordinal))
            {
                existing = entry;
                state = candidate;
                break;
            }
        }

        var attempts = (state?.Attempts ?? 0) + 1;
        var firstAttemptAtUtc = state?.FirstAttemptAtUtc ?? nowUtc;
        var elapsed = nowUtc - firstAttemptAtUtc;
        var exhausted = attempts >= UnreadableDirectiveMinimumAttempts && elapsed >= UnreadableDirectiveRetryWindow;

        var payload = JsonSerializer.Serialize(
            new UnreadableDirectiveState(commentId, attempts, firstAttemptAtUtc));
        var message =
            $"Directive comment {commentId} ({action}): {issueIdentifier} could not be read from {repositoryKey} " +
            $"on {attempts} attempts over {DescribeElapsed(elapsed)}" +
            (exhausted ? "; the retry bound is spent and the directive is being consumed." : "; retrying next tick.");

        if (existing is not null)
        {
            existing.Message = message;
            existing.OccurredAtUtc = nowUtc;
            existing.DataJson = payload;
        }
        else
        {
            dbContext.EventLog.Add(new EventLogEntity
            {
                IssueId = issueId,
                IssueIdentifier = issueIdentifier,
                EventName = UnreadableDirectiveEvent,
                Level = LogLevel.Warning.ToString(),
                Message = message,
                DataJson = payload,
                OccurredAtUtc = nowUtc
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new UnreadableReloadOutcome(attempts, elapsed, exhausted);
    }

    private static UnreadableDirectiveState? ReadUnreadableDirectiveState(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UnreadableDirectiveState>(dataJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>How many times a directive's issue has failed to reload, and since when.</summary>
    public sealed record UnreadableDirectiveState(string CommentId, int Attempts, DateTimeOffset FirstAttemptAtUtc);

    private sealed record UnreadableReloadOutcome(int Attempts, TimeSpan Elapsed, bool Exhausted);

    // Reported to the owner, so it reads as a person would say it rather than as
    // "01:04:37.2190000".
    private static string DescribeElapsed(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "under a minute",
        { TotalHours: < 1 } => DescribeQuantity(elapsed.TotalMinutes, "minute"),
        { TotalDays: < 1 } => DescribeQuantity(elapsed.TotalHours, "hour"),
        _ => DescribeQuantity(elapsed.TotalDays, "day")
    };

    private static string DescribeQuantity(double value, string unit)
    {
        var rounded = Math.Round(value, 1);
        return $"{rounded:0.#} {unit}{(rounded == 1 ? string.Empty : "s")}";
    }

    // The run that last said anything about this issue. Its phase is where a
    // `resume` picks up, and its repository is the one every tracker call for this
    // issue has to be aimed at.
    private static RunEntity LatestRun(List<RunEntity> stuckRuns) =>
        stuckRuns.OrderByDescending(run => run.LastEventAtUtc ?? run.StartedAtUtc).First();

    private void ResolveStuckRuns(List<RunEntity> stuckRuns, string reason) =>
        SettleStuckRuns(stuckRuns, RunStatusNames.ResolvedByDirective, "resolved_by_directive", reason);

    // Move the escalated runs off needs_command_center. The status is a parameter
    // because an answered escalation and an abandoned one are not the same record:
    // one was resolved, the other only stopped being asked about.
    private void SettleStuckRuns(List<RunEntity> stuckRuns, string status, string lastEvent, string reason)
    {
        var nowUtc = timeProvider.GetUtcNow();
        foreach (var run in stuckRuns)
        {
            run.Status = status;
            run.CompletedAtUtc ??= nowUtc;
            run.LastEvent = lastEvent;
            run.LastMessage = reason;
            run.LastEventAtUtc = nowUtc;
        }
    }

    // Consumption is permanent, so it is reserved for directives the plane can
    // answer for good: a comment the parser refuses, an issue GitHub says is
    // closed, or - through HandleUnreadableIssueAsync - one whose issue stayed
    // unreadable past the retry bound. Anything that only means "not right now"
    // defers instead - see PostDeferralNoticeAsync.
    private async Task ConsumeInvalidDirectiveAsync(
        TrackerQuery query,
        NormalizedIssueComment comment,
        string issueId,
        string issueIdentifier,
        string error,
        CancellationToken cancellationToken)
    {
        await ConsumeAndAckAsync(
            query, comment, issueId, issueIdentifier,
            "invalid", null, "consumed_invalid", error,
            $"**Directive rejected:** {error}.\n\nThis directive will not be retried. The escalation remains open; post a corrected `symphony:directive` block. Nothing was dispatched — the control plane does not guess.",
            cancellationToken);
    }

    /// <summary>
    /// Consume a directive for good, then tell the owner what happened.
    /// </summary>
    /// <remarks>
    /// Durable first, comment second, and never the other way round. Everything a
    /// later tick has to agree on - the ledger row that stops the directive being
    /// acted on twice, the run statuses the caller has already moved off
    /// <see cref="RunStatusNames.NeedsCommandCenter"/>, and the ack the owner is
    /// owed - lands in a single SaveChanges before the first tracker call. A
    /// process that dies, or a tracker that refuses the write, can then only lose
    /// the comment, and <see cref="ReplayOwedAcksAsync"/> posts it on a later tick.
    /// </remarks>
    private async Task ConsumeAndAckAsync(
        TrackerQuery query,
        NormalizedIssueComment comment,
        string issueId,
        string issueIdentifier,
        string action,
        string? phase,
        string outcome,
        string? detail,
        string ackMessage,
        CancellationToken cancellationToken)
    {
        var body = BuildAckBody(comment.Id, ackMessage);
        await RecordConsumptionAsync(
            comment.Id, issueId, issueIdentifier, action, phase, outcome, detail,
            new PendingAckState(comment.Id, TrackerQuerySet.KeyOf(query), body, 0, null),
            cancellationToken);
        await DeliverOwedAckAsync(query, comment.Id, issueId, issueIdentifier, body, cancellationToken);
    }

    private static string BuildAckBody(string commentId, string message) =>
        $"{AckMarkerFor(commentId)}\n{message}\n\n— Symphony directive processor";

    // The ack immediately after consumption. The marker is not looked for first:
    // the directive loop has just established that it is not on the issue, and
    // this runs in the same tick.
    private async Task DeliverOwedAckAsync(
        TrackerQuery query,
        string commentId,
        string issueId,
        string issueIdentifier,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            await trackerClient.PostIssueCommentAsync(query, issueId, body, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not post the ack for directive {CommentId} on {IssueIdentifier}; the directive is " +
                "consumed and the escalated run(s) settled regardless, and the ack stays owed for a later tick.",
                commentId, issueIdentifier);
            return;
        }

        await ClearOwedAckAsync(commentId, issueId, cancellationToken);
    }

    /// <summary>
    /// Post the acks recorded as owed on an earlier tick, once each.
    /// </summary>
    /// <remarks>
    /// Idempotent on replay in both directions. The marker is looked for on the
    /// issue first, so a process that died between posting the comment and deleting
    /// the debt re-reads its own marker and deletes the debt rather than posting a
    /// second time; and the debt is only deleted once the comment is known to be
    /// there, so a death before the delete re-posts rather than losing the record.
    ///
    /// Bounded for the same reason the reload it usually follows is: an issue that
    /// cannot be read cannot be commented on either, and a debt retried for ever
    /// spends a tracker call per tick for good. The bound only ever costs a
    /// courtesy comment - the directive is already consumed and the runs already
    /// settled - so exhausting it drops the debt and says so in the log.
    /// </remarks>
    private async Task ReplayOwedAcksAsync(TrackerQuerySet queries, CancellationToken cancellationToken)
    {
        List<EventLogEntity> owed;
        try
        {
            owed = await dbContext.EventLog
                .Where(entry => entry.EventName == AckPendingEvent)
                .OrderBy(entry => entry.Id)
                .ToListAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load the directive acks still owed; will retry next tick.");
            return;
        }

        foreach (var entry in owed)
        {
            var state = ReadPendingAckState(entry.DataJson);
            if (state is null || string.IsNullOrWhiteSpace(entry.IssueId))
            {
                // Nothing left to post with. Keeping the row would retry a payload
                // that can never be read.
                await DiscardOwedAckAsync(entry, "its recorded payload could not be read", cancellationToken);
                continue;
            }

            var issueId = entry.IssueId;
            var issueIdentifier = entry.IssueIdentifier ?? issueId;
            Exception? failure = null;
            try
            {
                var query = queries.For(state.RepositoryKey);
                var marker = AckMarkerFor(state.CommentId);
                var comments = await trackerClient.FetchIssueCommentsAsync(
                    query, issueId, issueIdentifier, cancellationToken);
                if (!comments.Any(item => item.Body.Contains(marker, StringComparison.Ordinal)))
                {
                    await trackerClient.PostIssueCommentAsync(query, issueId, state.Body, cancellationToken);
                    logger.LogInformation(
                        "Posted the ack owed for directive {CommentId} on {IssueIdentifier} on a later tick.",
                        state.CommentId, issueIdentifier);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure is null)
            {
                await ClearOwedAckAsync(state.CommentId, issueId, cancellationToken);
                continue;
            }

            await CountOwedAckFailureAsync(entry, state, issueIdentifier, failure, cancellationToken);
        }
    }

    private async Task CountOwedAckFailureAsync(
        EventLogEntity entry,
        PendingAckState state,
        string issueIdentifier,
        Exception failure,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var attempts = state.Attempts + 1;
        var firstAttemptAtUtc = state.FirstAttemptAtUtc ?? nowUtc;
        var elapsed = nowUtc - firstAttemptAtUtc;

        if (attempts >= UnreadableDirectiveMinimumAttempts && elapsed >= UnreadableDirectiveRetryWindow)
        {
            logger.LogWarning(
                failure,
                "Giving up on the ack owed for directive {CommentId} on {IssueIdentifier} after {Attempts} " +
                "attempts over {Elapsed}. The directive stays consumed and its run(s) stay settled.",
                state.CommentId, issueIdentifier, attempts, DescribeElapsed(elapsed));
            await DiscardOwedAckAsync(
                entry,
                $"it could not be posted in {attempts} attempts over {DescribeElapsed(elapsed)}",
                cancellationToken);
            return;
        }

        logger.LogWarning(
            failure,
            "Could not post the ack owed for directive {CommentId} on {IssueIdentifier} (attempt {Attempts}); " +
            "the debt stays recorded and will retry next tick.",
            state.CommentId, issueIdentifier, attempts);

        try
        {
            entry.DataJson = JsonSerializer.Serialize(
                state with { Attempts = attempts, FirstAttemptAtUtc = firstAttemptAtUtc });
            entry.Message =
                $"Directive comment {state.CommentId}: the ack is owed and could not be posted on " +
                $"{attempts} attempts over {DescribeElapsed(elapsed)}.";
            entry.OccurredAtUtc = nowUtc;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Could not count the failed ack attempt for directive {CommentId}.", state.CommentId);
        }
    }

    private Task ClearOwedAckAsync(string commentId, string issueId, CancellationToken cancellationToken) =>
        RemoveOwedAckRowsAsync(commentId, issueId, null, cancellationToken);

    private Task DiscardOwedAckAsync(EventLogEntity entry, string reason, CancellationToken cancellationToken)
    {
        dbContext.EventLog.Remove(entry);
        return SaveOwedAckChangeAsync(reason, cancellationToken);
    }

    private async Task RemoveOwedAckRowsAsync(
        string commentId,
        string issueId,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await dbContext.EventLog
                .Where(entry => entry.EventName == AckPendingEvent && entry.IssueId == issueId)
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                var state = ReadPendingAckState(row.DataJson);
                if (state is null || string.Equals(state.CommentId, commentId, StringComparison.Ordinal))
                {
                    dbContext.EventLog.Remove(row);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The debt outliving the comment costs one duplicate ack at worst; the
            // marker check in ReplayOwedAcksAsync catches it before it is posted.
            logger.LogWarning(
                ex,
                "Could not clear the ack debt for directive {CommentId}{Reason}.",
                commentId,
                reason is null ? string.Empty : $" ({reason})");
        }
    }

    private async Task SaveOwedAckChangeAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not drop an unpostable directive ack ({Reason}).", reason);
        }
    }

    private static PendingAckState? ReadPendingAckState(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingAckState>(dataJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tell the owner, once, that a valid directive was understood but could not be
    /// acted on yet — and that the plane will keep trying.
    /// </summary>
    /// <remarks>
    /// The owner previously saw one wording for both outcomes: "the directive could
    /// not be executed". One of those meant "fix the comment", the other meant "the
    /// plane could not reach the tracker", and only the first was true of the
    /// comment. Worse, both were final, so the second read as an instruction to
    /// repost a directive that would be discarded the same way.
    ///
    /// The notice carries its own marker rather than the ack marker, because the
    /// ack marker is the crash-window dedupe for a CONSUMED directive: posting one
    /// here would make the next tick record the directive as already handled and
    /// discard it — precisely the bug this path exists to avoid. The marker is only
    /// used to keep a long outage from posting a notice per tick.
    /// </remarks>
    private async Task PostDeferralNoticeAsync(
        TrackerQuery query,
        NormalizedIssueComment comment,
        IReadOnlyList<NormalizedIssueComment> comments,
        string issueId,
        string reason,
        CancellationToken cancellationToken)
    {
        var marker = DeferralMarkerFor(comment.Id);
        if (comments.Any(item => item.Body.Contains(marker, StringComparison.Ordinal)))
        {
            return;
        }

        var body =
            $"{marker}\n**Directive accepted, not yet executed:** {reason}.\n\n" +
            "The directive has **not** been discarded — the plane retries it on every tick. Nothing needs " +
            $"reposting. If the read has still not succeeded after {DescribeElapsed(UnreadableDirectiveRetryWindow)} " +
            "the plane stops asking and says so in a separate comment; until then this is the only notice.\n\n" +
            "— Symphony directive processor";

        try
        {
            await trackerClient.PostIssueCommentAsync(query, issueId, body, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The notice is a courtesy; the retry is the guarantee. A tracker that
            // cannot be read often cannot be written either, and failing to say so
            // must not stop the directive from being tried again.
            logger.LogWarning(
                ex,
                "Could not post the deferral notice for directive {CommentId} on issue {IssueId}; the directive still retries next tick.",
                comment.Id,
                issueId);
        }
    }

    /// <summary>
    /// Record that a valid directive is accepted and waiting for the plane, so the
    /// fact survives this process and can be read by anything that needs to know
    /// whether an issue is still the owner's to move.
    /// </summary>
    /// <remarks>
    /// One row per directive, rewritten rather than appended, and only once every
    /// <see cref="PendingDirectiveRefresh"/>: a directive can sit deferred for
    /// hours behind a full queue, and the tick that defers it runs every few
    /// seconds. The comment id goes in DataJson rather than being read back out of
    /// the message, following the candidate-scan pause - a value recovered by
    /// parsing prose breaks the next time somebody improves the wording.
    /// </remarks>
    private async Task RecordPendingDirectiveAsync(
        string commentId,
        string issueId,
        string issueIdentifier,
        string action,
        string reason,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new PendingDirectiveState(commentId, action));
        var message = $"Directive comment {commentId} ({action}) is accepted and waiting for the plane: {reason}.";

        // Ordered by Id for the same reason the pause restore is: SQLite cannot
        // ORDER BY a DateTimeOffset, and the identity column answers "written last"
        // exactly.
        var existing = await dbContext.EventLog
            .Where(entry => entry.EventName == PendingDirectiveEvent && entry.DataJson == payload)
            .OrderByDescending(entry => entry.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            if (nowUtc - existing.OccurredAtUtc < PendingDirectiveRefresh)
            {
                return;
            }

            existing.Message = message;
            existing.OccurredAtUtc = nowUtc;
        }
        else
        {
            dbContext.EventLog.Add(new EventLogEntity
            {
                IssueId = issueId,
                IssueIdentifier = issueIdentifier,
                EventName = PendingDirectiveEvent,
                Level = LogLevel.Information.ToString(),
                Message = message,
                DataJson = payload,
                OccurredAtUtc = nowUtc
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The comment a pending directive belongs to, as persisted.</summary>
    public sealed record PendingDirectiveState(string CommentId, string Action);

    /// <summary>
    /// Write everything a later tick has to agree on about a consumed directive, in
    /// one SaveChanges: the exactly-once ledger row, the run statuses the caller
    /// moved off <see cref="RunStatusNames.NeedsCommandCenter"/> (tracked entities,
    /// flushed here), and - when there is one - the ack still owed to the owner.
    /// </summary>
    private async Task RecordConsumptionAsync(
        string commentId,
        string issueId,
        string issueIdentifier,
        string action,
        string? phase,
        string outcome,
        string? detail,
        PendingAckState? owedAck,
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
        if (owedAck is not null)
        {
            dbContext.EventLog.Add(new EventLogEntity
            {
                IssueId = issueId,
                IssueIdentifier = issueIdentifier,
                EventName = AckPendingEvent,
                Level = LogLevel.Information.ToString(),
                Message = $"Directive comment {commentId}: the ack is owed and has not been posted yet.",
                DataJson = JsonSerializer.Serialize(owedAck),
                OccurredAtUtc = nowUtc
            });
        }

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
