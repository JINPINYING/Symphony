using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Host.Services;

/// <summary>
/// One thing the owner may need to act on. <paramref name="Url"/> is where to go
/// and do it, when there is such a place - an item that names a decision but
/// makes the reader hunt for it is only half an answer.
/// </summary>
public sealed record AttentionItem(string Label, string Detail, string Severity, string? Url = null);

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

    /// <summary>
    /// Something is degraded and the plane gets out of it by itself.
    ///
    /// The third answer this panel was missing. It only had "everything is fine"
    /// and "a person must act", so a ten-minute rate-limit backoff - a wait the
    /// engine chose, on a clock, that clears unattended - had to be filed as one
    /// or the other. It was filed as both at once: the headline said "something is
    /// wrong and it will not clear itself" while the sub-line said "the plane is
    /// running normally", and nine consequences of one pause were counted as nine
    /// decisions the owner owed.
    ///
    /// Items at this level are shown, because a page that says all is well while a
    /// core read is failing is not to be trusted either. They are NOT counted as
    /// things waiting on the owner, because they are not.
    /// </summary>
    public const string LevelRecovering = "recovering";

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
        string? primaryRepository = null)
    {
        var items = new List<AttentionItem>();

        // A tracker the engine cannot reach is a blind plane: no work is found,
        // none is dispatched, and every internal signal is indistinguishable from
        // a quiet queue - the same shape of blind spot as a scheduler that stops
        // firing. Reported only once it has lasted, because the observed failures
        // were DNS blips that cleared within a tick, and a page that flags each of
        // those is one that trains its reader to ignore red.
        //
        // A pause the engine took on purpose is a different fact from a tracker it
        // cannot reach, and telling them apart is the whole point of this issue. A
        // rate limit is refused, the engine backs off for ten minutes, the window
        // resets and scanning resumes - nobody does anything. Reported as an
        // outage it became "something is wrong and it will not clear itself",
        // which was untrue in both halves.
        //
        // The pause is still reported, because the opposite failure is just as
        // bad: a page that claims the plane is running normally while a core read
        // is refused is a page that lied on the one question it exists to answer.
        // It is reported as a wait, not as a demand.
        var blindFor = tracker?.UnreachableSinceUtc is { } since ? now - since : (TimeSpan?)null;

        // "It recovers on its own" is an observation, and it expires. Past an hour
        // the backing off has demonstrably not worked, and continuing to call it
        // self-clearing would be the same over-claim in the other direction.
        var stillSelfClearing = blindFor is null || blindFor.Value <= TrackerReachability.SelfRecoveryLimit;

        if (tracker?.ScanPausedUntilUtc is { } resumeAt && resumeAt > now && stillSelfClearing)
        {
            var cause = string.IsNullOrWhiteSpace(tracker.ScanPauseReason)
                ? string.Empty
                : $" Cause: {tracker.ScanPauseReason}";

            items.Add(new AttentionItem(
                "GitHub scanning is paused",
                $"GitHub refused further requests, so the plane stopped asking and resumes scanning in {Humanise(resumeAt - now)}. No new work is picked up until then; runs already under way are unaffected. Nothing is needed from you.{cause}",
                LevelRecovering));
        }
        else if (blindFor is { } age && age > TrackerReachability.UnreachableGrace)
        {
            // Backing off is the plane's whole answer to a refusal, so a refusal
            // that has outlived the backoff is one the plane has no answer for.
            // Saying so is the difference between this item and the one above.
            var backoffFailed = tracker?.ScanPausedUntilUtc is not null
                ? " Backing off has not cleared it."
                : string.Empty;

            items.Add(new AttentionItem(
                "The issue tracker cannot be reached",
                $"No candidate scan has succeeded for {Humanise(age)}, so nothing new will be picked up.{backoffFailed} Last cause: {tracker!.LastFailureReason}",
                LevelDown));
        }

        if (!engineHealthy)
        {
            items.Add(new AttentionItem(
                "The engine is not answering",
                "Nothing will be picked up until it is back. Check the service and its logs.",
                LevelDown));
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

            items.Add(new AttentionItem(
                failing
                    ? $"{prLabel} has failing checks"
                    : tracked
                        ? $"{prLabel} is waiting on you"
                        : $"{prLabel} fell out of the pipeline",
                failing
                    ? $"{pr.Title} - CI is red, so the merge gate will not take it.{waited}"
                    : tracked
                        ? $"{pr.Title} - open and not blocked by CI. Nothing will merge it without you.{waited}"
                        : $"{pr.Title} - open and green, but the plane is not tracking it, so no review or merge will ever run. This is a fault to repair, not a decision to make.{waited}",
                LevelAttention,
                string.IsNullOrWhiteSpace(pr.Url) ? null : pr.Url));
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
                         WatchedTaskReport.HealthRecovering => 4,
                         _ => 3
                     }))
        {
            // Disabled, and failing more than once in a row, are hard stops: the
            // task will not recover on its own. Late and unknown are reported at
            // attention level, because a busy host can legitimately delay a run and
            // a page that escalates ordinary jitter to "down" is one that gets
            // ignored.
            //
            // A SINGLE failed run on a task that is still scheduled is neither. It
            // used to read as a hard stop, and on 2026-09-02 the owner was told the
            // Commander "will keep failing on the same schedule" about a run a
            // deploy had killed - it ran clean six minutes later, unattended. One
            // observation cannot support a claim about the next run, so this level
            // says what was seen and waits for the next one.
            var severity = task.Health switch
            {
                WatchedTaskReport.HealthDisabled or WatchedTaskReport.HealthFailing => LevelDown,
                WatchedTaskReport.HealthRecovering => LevelRecovering,
                _ => LevelAttention
            };

            items.Add(new AttentionItem(
                // "Is not running as scheduled" is false for a task that ran on
                // time and exited non-zero. It ran; it failed. Those need different
                // sentences because they need different responses.
                task.Health == WatchedTaskReport.HealthRecovering
                    ? $"{task.Name} had a run that did not succeed"
                    : $"{task.Name} is not running as scheduled",
                task.Explanation,
                severity));
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

        // Two different lists that used to be one. A decision is something only a
        // person can make; a recovering item is something the plane is already
        // handling. Counting them together is what put nine consequences of one
        // rate-limit pause in front of the owner as nine things they owed.
        var decisions = items.Count(i => i.Severity != LevelRecovering);
        var recovering = items.Count - decisions;

        var level = items.Any(i => i.Severity == LevelDown) ? LevelDown
                  : decisions > 0 ? LevelAttention
                  : recovering > 0 ? LevelRecovering
                  : LevelClear;

        // Worst first, and everything self-clearing last, so the top of the list is
        // always the part that needs a person. OrderBy is stable, so the ordering
        // each lane already established survives.
        items = items
            .OrderBy(i => i.Severity switch
            {
                LevelDown => 0,
                LevelAttention => 1,
                _ => 2
            })
            .ToList();

        string headline;
        string detail;

        if (level == LevelDown)
        {
            headline = "Something is wrong and it will not clear itself";
            detail = recovering == 0
                ? "The items below are blocking work. Nothing new will be picked up until they are resolved."
                : "The items at the top are blocking work and will not resolve on their own. The rest are recovering by themselves and need nothing.";
        }
        else if (level == LevelAttention)
        {
            headline = decisions == 1 ? "One thing is waiting on you" : $"{decisions} things are waiting on you";
            // Never "running normally" while a core read is failing. That sentence
            // sat above a plane that could not see any of its repositories, and it
            // is the reason this issue exists.
            detail = recovering == 0
                ? "The plane is running normally. These are decisions it will not make on its own."
                : "These are decisions it will not make on its own. Below them, something is degraded and recovering by itself - no action needed there.";
        }
        else if (level == LevelRecovering)
        {
            // Nothing needs a person, and the plane is not at full strength. Both
            // halves have to be said: claiming normality here is the defect, and so
            // is asking for a decision that does not exist.
            //
            // A single item names itself, because "GitHub scanning is paused" is a
            // better headline than any summary of it could be.
            headline = recovering == 1
                ? items[0].Label
                : $"{recovering} things are recovering on their own";
            detail = recovering == 1
                ? items[0].Detail
                : "The plane is degraded and working its own way out. None of it needs you; if any of it is still here once it should have cleared, it will move up into the list above.";
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

    private static string Humanise(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "less than a minute";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} minute{Plural((int)span.TotalMinutes)}";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hour{Plural((int)span.TotalHours)}";
        return $"{(int)span.TotalDays} day{Plural((int)span.TotalDays)}";
    }

    private static string Plural(int value) => value == 1 ? string.Empty : "s";
}
