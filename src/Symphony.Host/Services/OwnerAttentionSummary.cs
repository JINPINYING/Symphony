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
        if (tracker?.UnreachableSinceUtc is { } blindSince &&
            now - blindSince > TrackerReachability.UnreachableGrace)
        {
            items.Add(new AttentionItem(
                "The issue tracker cannot be reached",
                $"No candidate scan has succeeded for {Humanise(now - blindSince)}, so nothing new will be picked up. Last cause: {tracker.LastFailureReason}",
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
        foreach (var phase in phases.Where(p => string.Equals(p.Stage, PhaseStages.Escalated, StringComparison.Ordinal)))
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
                         _ => 3
                     }))
        {
            // Disabled and failing are hard stops: the task will not recover on
            // its own. Late and unknown are reported at attention level, because a
            // busy host can legitimately delay a run and a page that escalates
            // ordinary jitter to "down" is one that gets ignored.
            var severity = task.Health is WatchedTaskReport.HealthDisabled or WatchedTaskReport.HealthFailing
                ? LevelDown
                : LevelAttention;

            items.Add(new AttentionItem(
                $"{task.Name} is not running as scheduled",
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
