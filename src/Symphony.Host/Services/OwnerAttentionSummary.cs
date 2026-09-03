using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Host.Services;

/// <summary>
/// One thing the owner may need to act on. <paramref name="Url"/> is where to go
/// and do it, when there is such a place - an item that names a decision but
/// makes the reader hunt for it is only half an answer.
/// </summary>
/// <summary>
/// What the reader can actually do about an item, where that is anything more
/// than "go and look". A null action means the item is informational, and an
/// informational item does not belong under "needs your attention" at all.
/// </summary>
/// <param name="Kind">open | merge | directive | command - what shape the action takes.</param>
/// <param name="Label">The button text. Says what happens, not where it goes.</param>
/// <param name="Url">Where to act, for the kinds that resolve on the tracker.</param>
/// <param name="Command">The exact command to run, for actions that need a terminal. Never a description of one.</param>
public sealed record AttentionAction(string Kind, string Label, string? Url = null, string? Command = null);

/// <summary>
/// Who can clear this. The panel is titled "needs your attention", so anything
/// the owner cannot act on is a claim it should not be making.
/// </summary>
public static class AttentionActors
{
    /// The owner: a judgement only they can make.
    public const string Owner = "owner";

    /// The plane: it is already handling this, or will on its next tick.
    public const string Plane = "plane";

    /// Someone at this machine: a fault needing a terminal, not a decision.
    public const string Operator = "operator";
}

public sealed record AttentionItem(
    string Label,
    string Detail,
    string Severity,
    string? Url = null,
    // Owner by default so a new item is loud rather than silently swallowed; every
    // site that is NOT the owner's says so explicitly.
    string Actor = AttentionActors.Owner,
    AttentionAction? Action = null);

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
    /// <summary>
    /// "#115" becomes "Symphony#115" when more than one repository is watched,
    /// following GitHub's own shorthand for a cross-repository reference. Anything
    /// that is not a bare issue reference gets a space instead, so "PR #122"
    /// reads "Symphony PR #122" rather than running the words together.
    ///
    /// The owner is left off because every repository here shares one, and a label
    /// too long for the panel is a label nobody reads.
    /// </summary>
    /// <summary>
    /// Where to go and do the thing. An item that names a decision and makes the
    /// reader hunt for the issue is only half an answer - the escalation items
    /// carried no link at all until 2026-09-01, while the pull-request items
    /// beside them did.
    ///
    /// Returns null rather than a guess when the repository is unknown: a wrong
    /// link is worse than none, because none is obviously absent and a wrong one
    /// is discovered only after following it.
    /// </summary>
    private static string? IssueUrl(string? repository, string identifier)
    {
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var number = identifier.TrimStart('#');
        return number.Length > 0 && number.All(char.IsDigit)
            ? $"https://github.com/{repository}/issues/{number}"
            : null;
    }

    private static string Qualify(string? primaryRepository, string? repository, string identifier)
    {
        if (string.IsNullOrWhiteSpace(primaryRepository) || string.IsNullOrWhiteSpace(identifier))
        {
            return identifier;
        }

        var source = string.IsNullOrWhiteSpace(repository) ? primaryRepository : repository;
        var slash = source.LastIndexOf('/');
        var name = slash >= 0 && slash + 1 < source.Length ? source[(slash + 1)..] : source;
        return identifier.StartsWith('#') ? $"{name}{identifier}" : $"{name} {identifier}";
    }

    public const string LevelClear   = "clear";
    public const string LevelAttention = "attention";
    public const string LevelDown    = "down";

    public static (string Level, string Headline, string Detail, IReadOnlyList<AttentionItem> Items) Build(
        bool engineHealthy,
        IReadOnlyList<RunEntity> escalatedRuns,
        int runningCount,
        int retryQueueCount,
        IReadOnlyList<PhaseLedgerEntity> phases,
        IReadOnlyList<OpenPullRequest> openPullRequests,
        IReadOnlyList<AgentActivityReport> agentActivity,
        IReadOnlyList<WatchedTaskReport> watchedTasks,
        TrackerReachabilitySnapshot? tracker,
        DateTimeOffset? lastEventAtUtc,
        DateTimeOffset now,
        // The primary repository key when the plane watches more than one, and null
        // when it watches one. Non-null turns qualification on: "#115" stops being
        // an answer once two repositories can each have one, and a panel that says
        // "#115 needs a decision" is telling the reader to go and find out which.
        //
        // It is the primary KEY rather than a flag because rows written before
        // multi-repository tracking carry no repository, and they all belong to the
        // repository that was the only one at the time - so they can be labelled
        // correctly instead of being the one line on the panel that stays ambiguous.
        string? primaryRepository = null,
        // The issues the plane is holding right now. "Waiting on you" is a claim
        // about the plane as much as about the owner - if the plane is mid-run on
        // the issue, or has it queued, then it is not waiting on anyone.
        IReadOnlyCollection<string>? activeIssueIds = null)
    {
        var items = new List<AttentionItem>();
        var active = activeIssueIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(activeIssueIds, StringComparer.OrdinalIgnoreCase);

        // A tracker the engine cannot reach is a blind plane: no work is found,
        // none is dispatched, and every internal signal is indistinguishable from
        // a quiet queue - the same shape of blind spot as a scheduler that stops
        // firing. Reported only once it has lasted, because the observed failures
        // were DNS blips that cleared within a tick, and a page that flags each of
        // those is one that trains its reader to ignore red.
        if (tracker?.UnreachableSinceUtc is { } blindSince &&
            now - blindSince > TrackerReachability.UnreachableGrace)
        {
            items.Add(new AttentionItem(
                "The issue tracker cannot be reached",
                $"No candidate scan has succeeded for {Humanise(now - blindSince)}, so nothing new will be picked up. Last cause: {tracker.LastFailureReason}",
                LevelDown,
                Actor: AttentionActors.Operator,
                Action: new AttentionAction("command", "Check connectivity",
                    Command: "curl -s -o /dev/null -w \"%{http_code}\" https://api.github.com")));
        }

        if (!engineHealthy)
        {
            items.Add(new AttentionItem(
                "The engine is not answering",
                "Nothing will be picked up until it is back.",
                LevelDown,
                Actor: AttentionActors.Operator,
                Action: new AttentionAction("command", "Restart the service",
                    Command: "Get-Service Symphony | Restart-Service")));
        }

        // Issues whose phase has already reached a terminal stage. The reconciler
        // in PhaseOrchestrator now resolves the run when it closes the ledger, so
        // this should always be empty - it is kept because the failure it guards
        // against was silent and expensive. On 2026-09-01 the ledger said closed
        // while the runs stayed needs_command_center, and the page led with "2
        // things are waiting on you" for two issues its own event stream reported
        // as resolved. Reporting a decision that is not needed costs more trust
        // than missing one: the reader stops believing the number.
        var settledIssues = phases
            .Where(p => string.Equals(p.Stage, PhaseStages.Closed, StringComparison.Ordinal)
                     || string.Equals(p.Stage, PhaseStages.Merged, StringComparison.Ordinal))
            .Select(p => p.IssueId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var run in escalatedRuns.Where(run => !settledIssues.Contains(run.IssueId)))
        {
            var posted = run.EscalationPostedAtUtc is not null;

            // The run already knows why it stopped. Saying only "escalated and
            // posted" sends the reader to GitHub to find out what the engine had
            // in hand the whole time - "CI rollup is FAILURE at head 214a4406" is
            // the difference between a notification and something actionable.
            var why = string.IsNullOrWhiteSpace(run.LastMessage)
                ? "The engine did not record a reason."
                : run.LastMessage!.Trim();

            items.Add(new AttentionItem(
                $"{Qualify(primaryRepository, run.Repository, run.IssueIdentifier)} needs a decision",
                posted
                    ? $"{why} Reply with a symphony:directive comment to un-park it."
                    : $"{why} The GitHub comment has not posted yet - the publisher may be failing.",
                posted ? LevelAttention : LevelDown,
                IssueUrl(run.Repository, run.IssueIdentifier)));
        }

        // A PR that reached ready and stayed there means the merge gate refused it
        // for a reason a person has to resolve.
        // Only when the run lane is not already reporting this issue.
        //
        // A phase escalation marks BOTH records: EscalateAsync sets the ledger to
        // escalated and flags the newest run needs_command_center, so every phase
        // escalation raised two items for one fact - "#126 needs a decision" and
        // "#126 stopped at the merge gate" - and the headline counted six things
        // waiting when four were. Inflating the only number on the page the reader
        // is asked to trust is worse than the redundancy looks.
        //
        // The run item is the one kept, because it carries the actual reason the
        // phase recorded ("Phase orchestration: ..."), where this one can only
        // describe the general case. A ledger escalated with no run behind it still
        // reports here, so nothing stops being covered.
        var issuesAlreadyReported = escalatedRuns
            .Where(run => !settledIssues.Contains(run.IssueId))
            .Select(run => run.IssueId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var phase in phases.Where(p =>
                     string.Equals(p.Stage, PhaseStages.Escalated, StringComparison.Ordinal) &&
                     !issuesAlreadyReported.Contains(p.IssueId)))
        {
            items.Add(new AttentionItem(
                $"{Qualify(primaryRepository, phase.Repository, phase.IssueIdentifier)} stopped at the merge gate",
                $"{Qualify(primaryRepository, phase.Repository, $"PR #{phase.PrNumber}")} was approved but not merged. The gate escalates rather than merging when a change touches a protected path.",
                LevelAttention));
        }

        // Pull requests the phase pipeline holds a ledger row for. Anything else
        // the plane opened is outside its own machinery and will not advance.
        var trackedPullRequestNumbers = phases
            .Where(p => p.PrNumber > 0 && !string.Equals(p.Stage, PhaseStages.Closed, StringComparison.Ordinal))
            .Select(p => p.PrNumber)
            .ToHashSet();

        // An open pull request is the most common way work waits on a person, and
        // for a long time this summary could not see one: every other input here
        // is the engine's own run state, so a green PR nobody had merged read as
        // an empty queue and the page said "nothing needs you".
        //
        // Draft and pending-CI PRs are deliberately excluded. A draft is the
        // author's to finish and a pending check will resolve itself; listing
        // either would make the page cry wolf, which is how a status page teaches
        // its reader to stop looking.
        foreach (var pr in openPullRequests.Where(pr => !pr.IsDraft).OrderBy(pr => pr.Number))
        {
            var checks = pr.ChecksState;
            if (string.Equals(checks, "PENDING", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(checks, "EXPECTED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var failing = string.Equals(checks, "FAILURE", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(checks, "ERROR", StringComparison.OrdinalIgnoreCase);
            var waited = pr.UpdatedAtUtc > DateTimeOffset.MinValue
                ? $" Waiting {Humanise(now - pr.UpdatedAtUtc)}."
                : string.Empty;

            var prLabel = Qualify(primaryRepository, pr.Repository, $"PR #{pr.Number}");

            // "Waiting on you" and "the plane lost this" both leave a green pull
            // request sitting open, and they ask completely different things of the
            // reader - one is a judgement to make, the other is a fault to repair.
            // The page called both the first, and the owner caught it: PR #127 was
            // reported as their decision when in fact the phase pipeline had
            // dropped it and would never have picked it up.
            //
            // Both halves are required. Most open pull requests have no ledger
            // because a person opened them, and those genuinely are the owner's to
            // merge - calling every untracked PR a fault would relabel all normal
            // work as breakage. The fault is specifically a branch the PLANE
            // created that the plane is no longer tracking.
            var planeOpened = pr.HeadRefName?.StartsWith("symphony/", StringComparison.OrdinalIgnoreCase) == true;
            var droppedByPipeline = planeOpened && !trackedPullRequestNumbers.Contains(pr.Number);
            var tracked = !droppedByPipeline;

            var prUrl = string.IsNullOrWhiteSpace(pr.Url) ? null : pr.Url;

            if (failing)
            {
                // Red CI is the author's to fix, and the author is usually the
                // plane. It is listed so the owner can see why nothing is moving,
                // not because they are expected to do anything about it.
                items.Add(new AttentionItem(
                    $"{prLabel} has failing checks",
                    $"{pr.Title} - CI is red, so the merge gate will not take it.{waited}",
                    LevelAttention,
                    prUrl,
                    Actor: AttentionActors.Plane,
                    Action: new AttentionAction("open", "See the failing checks", Url: prUrl)));
                continue;
            }

            if (!tracked)
            {
                // A branch the plane opened that the plane is no longer tracking.
                // Its own text has always said "a fault to repair, not a decision
                // to make", while the panel filed it under the owner's decisions
                // anyway.
                items.Add(new AttentionItem(
                    $"{prLabel} fell out of the pipeline",
                    $"{pr.Title} - open and green, but the plane is not tracking it, so no review or merge will ever run. This is a fault to repair, not a decision to make.{waited}",
                    LevelAttention,
                    prUrl,
                    Actor: AttentionActors.Operator,
                    Action: new AttentionAction("command", "Re-enter it into the pipeline",
                        Command: $"python scripts/command-center.py --readmit {pr.Number}")));
                continue;
            }

            // WAITING ON YOU IS A CLAIM, AND IT HAS TO BE CHECKED.
            //
            // The rule used to be "open + CI not failing", which is true of every
            // pull request in flight - so the count grew during healthy activity,
            // exactly when the owner should be left alone. Three claims were
            // checked against the same payload that produced them and all three
            // were false: one said a PR "was approved" that had never been
            // reviewed, one named a PR whose issue was in the plane's own queue,
            // and one named a PR that was mid-repair and carried CHANGES_REQUIRED.
            //
            // So the same payload is consulted before the claim is made. A pull
            // request is the owner's only when the plane is demonstrably not going
            // to move it: nothing running or queued on its issue, and - where the
            // phase machine has an opinion - a verdict of APPROVED recorded at
            // THIS head, not an earlier one.
            var ledger = phases.FirstOrDefault(entry =>
                entry.PrNumber == pr.Number &&
                (pr.Repository is null || string.IsNullOrEmpty(entry.Repository) ||
                 string.Equals(entry.Repository, pr.Repository, StringComparison.OrdinalIgnoreCase)));

            if (ledger is not null && active.Contains(ledger.IssueId))
            {
                continue;
            }

            if (ledger is not null)
            {
                // The stage is the pipeline's own statement of who is holding it.
                // awaiting_verify, awaiting_review, reviewing and wait_for_repair
                // are all "the plane is going to move this next" - saying "nothing
                // will merge it without you" over one of those is the claim that
                // grows the owner's list during healthy activity.
                //
                // `ready` is the merge gate: verified, reviewed, and deliberately
                // handed over because the gate escalates rather than merging on a
                // protected path. That one really is theirs.
                var pipelineStillHoldsIt =
                    ledger.Stage is PhaseStages.AwaitingVerify or PhaseStages.AwaitingReview
                        or PhaseStages.Reviewing or PhaseStages.WaitForRepair;

                if (pipelineStillHoldsIt)
                {
                    continue;
                }

                // And where a verdict exists it has to be about the code that would
                // actually be merged. "PR #147 was approved but not merged" was said
                // of a pull request that had never been reviewed at its head at all;
                // a verdict recorded against an earlier commit is not a verdict
                // about this one.
                var verdictIsStale =
                    !string.IsNullOrWhiteSpace(ledger.LastVerdictHeadSha) &&
                    !string.IsNullOrWhiteSpace(pr.HeadSha) &&
                    !string.Equals(ledger.LastVerdictHeadSha, pr.HeadSha, StringComparison.OrdinalIgnoreCase);

                if (verdictIsStale)
                {
                    continue;
                }
            }

            items.Add(new AttentionItem(
                $"{prLabel} is waiting on you",
                ledger is not null
                    ? $"{pr.Title} - through the pipeline and stopped at the merge gate. Nothing will merge it without you.{waited}"
                    : $"{pr.Title} - open and not blocked by CI. Nothing will merge it without you.{waited}",
                LevelAttention,
                prUrl,
                Actor: AttentionActors.Owner,
                Action: new AttentionAction("merge", "Merge it", Url: prUrl)));
        }

        // A scheduler that has stopped firing is the one fault this summary used
        // to be structurally incapable of seeing. Every other input here is the
        // engine's own state, and the engine's state looks identical whether the
        // queue is empty because there is no work or because nothing is left to
        // notice the work. That blind spot cost 27 hours of silence.
        //
        // Ordered worst-first so that, when several drift at once, the headline
        // count still leads with the one that stopped rather than the one that is
        // merely late.
        foreach (var task in watchedTasks
                     .Where(t => t.Health != WatchedTaskReport.HealthOk)
                     .OrderBy(t => t.Health switch
                     {
                         WatchedTaskReport.HealthDisabled => 0,
                         WatchedTaskReport.HealthFailing => 1,
                         WatchedTaskReport.HealthLate => 2,
                         _ => 3
                     }))
        {
            // Disabled and failing are hard stops: the task will not recover on
            // its own. Late and unknown are reported at attention level, because a
            // busy host can legitimately delay a run and a page that escalates
            // ordinary jitter to "down" is one that gets ignored.
            // A task that is still scheduled has not stopped - it is due again.
            //
            // 2026-09-02: a deploy killed the Commander mid-run. The task recorded
            // ERROR_PROCESS_ABORTED, the panel said "Something is wrong and it will
            // not clear itself" and "nothing new will be picked up", and the owner
            // came to ask what to do. It ran clean six minutes later while the
            // plane had been dispatching the whole time. Every part of the claim
            // was false, and the part that made it worst was the certainty.
            //
            // So a failure with a future run booked is reported as what it is: one
            // failure, and another attempt coming. Only a task with no next run -
            // disabled, or unscheduled - cannot recover without a person.
            var willRunAgain = task.NextRunUtc is { } next && next > now;
            var stopped = task.Health == WatchedTaskReport.HealthDisabled || !willRunAgain;

            var severity = stopped ? LevelDown : LevelAttention;
            var taskDetail = stopped
                ? task.Explanation
                : $"{task.Explanation} It is still scheduled and due again {Humanise(task.NextRunUtc!.Value - now)} from now, so this may clear on its own.";

            items.Add(new AttentionItem(
                stopped
                    ? $"{task.Name} is not running as scheduled"
                    : $"{task.Name} failed its last run",
                taskDetail,
                severity,
                // Nobody merges or decides a scheduled task. Someone at this machine
                // runs it, and the exact command beats an exit code every time.
                Actor: AttentionActors.Operator,
                Action: new AttentionAction("command", "Run it now",
                    Command: $"schtasks /run /tn \"{task.Name}\"")));
        }

        if (retryQueueCount > 0)
        {
            items.Add(new AttentionItem(
                $"{retryQueueCount} run{(retryQueueCount == 1 ? string.Empty : "s")} waiting to retry",
                "Transient failures retry on their own. Persistent ones escalate.",
                LevelAttention));
        }

        // The newest report still inside the live window, if any. Stale reports
        // are ignored rather than trusted: a session that died without saying
        // goodbye must not leave the page claiming work is underway forever.
        var liveAgent = agentActivity
            .Where(report => now - report.AtUtc <= AgentActivity.LiveWindow)
            .OrderByDescending(report => report.AtUtc)
            .FirstOrDefault();

        var level = items.Any(i => i.Severity == LevelDown) ? LevelDown
                  : items.Count > 0 ? LevelAttention
                  : LevelClear;

        string headline;
        string detail;

        // THE HEADLINE IS DERIVED, NOT ASSERTED.
        //
        // It used to be a function of severity alone, so anything `down` produced
        // "Something is wrong and it will not clear itself" and "nothing new will
        // be picked up" - both stated as fact, neither measured. A killed-and-
        // rescheduled task triggered exactly that while the plane went on
        // dispatching, and the owner had to come and ask.
        //
        // A headline addressed to the owner has to count the owner's items. The
        // rest are shown so they can see why things are moving or not, and they
        // say whose they are.
        var ownerItems = items.Count(item => item.Actor == AttentionActors.Owner);
        var othersItems = items.Count - ownerItems;
        var blocking = runningCount == 0 && retryQueueCount == 0;
        var others = othersItems == 1 ? "1 other thing is being handled" : $"{othersItems} other things are being handled";

        if (level == LevelDown)
        {
            headline = ownerItems > 0
                ? (ownerItems == 1 ? "One thing needs you, and something has stopped" : $"{ownerItems} things need you, and something has stopped")
                : "Something has stopped, and it is not yours to fix";
            detail = blocking
                ? "Nothing is running. The items marked below will not clear on their own."
                : "The plane is still working. The items marked below will not clear on their own.";
        }
        else if (level == LevelAttention && ownerItems == 0)
        {
            // Every item belongs to the plane or to an operator. Saying "N things
            // are waiting on you" here is the exact claim the owner objected to.
            headline = "Nothing needs you";
            detail = $"{others}, listed below so you can see what the plane is doing.";
        }
        else if (level == LevelAttention)
        {
            headline = ownerItems == 1 ? "One thing is waiting on you" : $"{ownerItems} things are waiting on you";
            detail = othersItems == 0
                ? "The plane is running normally. These are decisions it will not make on its own."
                : $"These are decisions the plane will not make on its own. {others}.";
        }
        else if (runningCount > 0)
        {
            headline = runningCount == 1 ? "Working on one issue" : $"Working on {runningCount} issues";
            detail = "Nothing needs you. Progress appears below as each phase completes.";
        }
        else if (liveAgent is not null)
        {
            // An empty queue is not an idle project. Work done by an agent outside
            // the queue used to render as "the plane is idle", which was the page
            // being confidently wrong about the only thing it is asked.
            headline = $"{liveAgent.Actor} is working";
            detail = $"{liveAgent.Summary} Not a queued run, so it does not appear in the counts above. Reported {Humanise(now - liveAgent.AtUtc)} ago.";
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

    private static string Short(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? "unknown" : sha.Length > 8 ? sha[..8] : sha;

    private static string Humanise(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "less than a minute";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} minute{Plural((int)span.TotalMinutes)}";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hour{Plural((int)span.TotalHours)}";
        return $"{(int)span.TotalDays} day{Plural((int)span.TotalDays)}";
    }

    private static string Plural(int value) => value == 1 ? string.Empty : "s";
}
