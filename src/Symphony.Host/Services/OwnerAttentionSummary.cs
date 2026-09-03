using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Host.Services;

/// <summary>
/// One thing the owner may need to act on. <paramref name="Url"/> is where to go
/// and do it, when there is such a place - an item that names a decision but
/// makes the reader hunt for it is only half an answer.
/// </summary>
/// <summary>
/// What the reader can actually do about an item, where that is more than "go and
/// look". A null action means the item is informational - and an informational
/// item does not belong on a panel addressed to someone.
/// </summary>
/// <param name="Kind">open | merge | directive | command - the shape of the action.</param>
/// <param name="Label">The button text. Says what happens, not where it goes.</param>
/// <param name="Url">Where to act, for the kinds that resolve on the tracker.</param>
/// <param name="Command">The exact command, for actions needing a terminal. Never a description of one.</param>
public sealed record AttentionAction(string Kind, string Label, string? Url = null, string? Command = null);

/// <summary>
/// Who can clear this. The panel is titled "needs your attention", so listing
/// something the owner cannot act on is a claim it should not be making.
/// </summary>
public static class AttentionActors
{
    /// A judgement only the owner can make.
    public const string Owner = "owner";

    /// The plane is already handling it, or will on its next tick.
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
    // site that is not the owner's says so explicitly.
    string Actor = AttentionActors.Owner,
    AttentionAction? Action = null);

/// <summary>
/// An issue the plane itself will move next: running, waiting for a free agent
/// slot, retrying, or carrying a directive it has accepted but not consumed yet.
///
/// This is the input the panel was missing. Every claim it makes is "nothing
/// will move this without you", and the state that contradicts it was sitting in
/// the same payload the whole time - the run said RUNNING and the queue named
/// the issue while the panel announced the pull request as the owner's to merge.
///
/// Carries both keys because the panel reaches an issue two ways: through the
/// phase ledger, which knows the id, and through a "symphony/115" branch name,
/// which knows only the number. <paramref name="Repository"/> is "owner/repo",
/// empty for the primary, so "#115" in one repository cannot silence "#115" in
/// another.
/// </summary>
public sealed record InFlightIssue(string IssueId, string IssueIdentifier, string Repository = "");

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
///
/// The same conservatism now applies to work in flight. Every item here is the
/// claim "nothing will move this without you", and each one is checked against
/// the rest of the payload before it is made: an issue the plane is running,
/// queueing, or holding a directive for is the plane's to move, not the owner's.
/// Without that check the rule was effectively "open pull request + CI not red",
/// which is true of every change in flight - so the count grew with healthy
/// activity, and grew fastest during exactly the busy periods when the owner
/// should have been left alone.
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
        // Issues the plane will move on its own. Required, not defaulted: a caller
        // that forgets it gets the panel back that reported healthy in-flight work
        // as the owner's obligation, and silently.
        IReadOnlyList<InFlightIssue> inFlightIssues,
        // The reason the phase machine recorded when it escalated, by issue id.
        // The phase lane can only describe the general case from a ledger row, and
        // describing the general case is how "stopped at the merge gate" came to be
        // printed over an escalation that happened during implementation.
        IReadOnlyDictionary<string, string> phaseEscalationReasons,
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
                LevelDown,
                Actor: AttentionActors.Operator,
                Action: new AttentionAction("command", "Check connectivity",
                    Command: "gh api rate_limit")));
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
                IssueUrl(run.Repository, run.IssueIdentifier),
                // A directive is a comment with a known grammar, so the panel can
                // say exactly what to write instead of naming the mechanism.
                Actor: posted ? AttentionActors.Owner : AttentionActors.Operator,
                Action: posted
                    ? new AttentionAction("directive", "Un-park it",
                        Url: IssueUrl(run.Repository, run.IssueIdentifier),
                        Command: "symphony:directive" + Environment.NewLine + "action: resume")
                    : new AttentionAction("command", "Check the publisher",
                        Command: @"Get-Content D:\AutonomousDevControlPlane\logs\symphony.log -Tail 50")));
        }

        // A phase that escalated and is not already reported through the run lane.
        //
        // What it escalated FOR is not something this lane may guess. It used to
        // print one story for every escalation - "PR #N was approved but not
        // merged, the gate escalates on a protected path" - and on 2026-09-02 that
        // story appeared over PR #147, which had never been reviewed at all and had
        // escalated during implementation. An owner reading it was being invited to
        // merge unreviewed code on the panel's word.
        //
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
        // phase recorded ("Phase orchestration: ..."). A ledger escalated with no
        // run behind it still reports here, so nothing stops being covered - and it
        // now reads the same reason back out of the event the phase wrote it to,
        // rather than describing the general case, which is what made the two lanes
        // tell different stories about one escalation.
        var issuesAlreadyReported = escalatedRuns
            .Where(run => !settledIssues.Contains(run.IssueId))
            .Select(run => run.IssueId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var phase in phases.Where(p =>
                     string.Equals(p.Stage, PhaseStages.Escalated, StringComparison.Ordinal) &&
                     !issuesAlreadyReported.Contains(p.IssueId)))
        {
            var issueLabel = Qualify(primaryRepository, phase.Repository, phase.IssueIdentifier);
            var pullRequestLabel = Qualify(primaryRepository, phase.Repository, $"PR #{phase.PrNumber}");
            var url = IssueUrl(phase.Repository, phase.IssueIdentifier);

            // The reason the phase machine recorded names the phase it stopped in
            // and, for a merge-gate refusal, the protected path itself - which is
            // the only part of that refusal the owner can act on.
            var recorded = phaseEscalationReasons.TryGetValue(phase.IssueId, out var reason) &&
                           !string.IsNullOrWhiteSpace(reason)
                ? reason.Trim()
                : null;

            // "Stopped at the merge gate" is a specific claim: reviewed, approved
            // at the head it still carries, and refused by the gate anyway. It is
            // the merge gate's own test, so it is tested the merge gate's way.
            if (IsApprovedAtHead(phase, phase.HeadSha))
            {
                items.Add(new AttentionItem(
                    $"{issueLabel} stopped at the merge gate",
                    $"{pullRequestLabel} was approved at head {Short(phase.HeadSha)} and the gate refused to merge it. " +
                    (recorded ?? "The engine did not record which condition it refused on."),
                    LevelAttention,
                    url,
                    Actor: AttentionActors.Owner,
                    Action: new AttentionAction("merge", "Merge it", Url: url)));
                continue;
            }

            items.Add(new AttentionItem(
                $"{issueLabel} stopped in the phase pipeline",
                $"{recorded ?? "The engine did not record a reason."} {VerdictAtHead(pullRequestLabel, phase)}",
                LevelAttention,
                url,
                // Stopped in the pipeline is a fault to repair, not a decision on
                // offer - the panel used to file it as the owner's anyway.
                Actor: AttentionActors.Operator,
                Action: new AttentionAction("directive", "Un-park it", Url: url,
                    Command: "symphony:directive" + Environment.NewLine + "action: resume")));
        }

        var inFlightIssueIds = inFlightIssues
            .Select(issue => issue.IssueId)
            .ToHashSet(StringComparer.Ordinal);

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

            // The pipeline's own row for this pull request, if it holds one.
            // Anything else the plane opened is outside its own machinery and will
            // not advance.
            var ledger = phases.FirstOrDefault(p =>
                p.PrNumber == pr.Number &&
                !string.Equals(p.Stage, PhaseStages.Closed, StringComparison.Ordinal) &&
                SameRepository(p.Repository, pr.Repository));

            // Listing a pull request here asserts that NOTHING will move it without
            // the owner. Before asserting it, check what the same payload already
            // knows - because on 2026-09-02 it knew otherwise for two of the three
            // items on the panel, and said them anyway.
            if (ledger is not null)
            {
                // The plane holds a run or a queue place for the issue behind it,
                // or a directive it has accepted and not consumed yet. PR #148 was
                // announced as needing the owner less than a minute after the plane
                // started a repair on that very branch.
                if (inFlightIssueIds.Contains(ledger.IssueId))
                {
                    continue;
                }

                // The phase machine is mid-flight: verifying, dispatching a review,
                // reviewing, or fenced waiting for the one bounded repair. A stage
                // that genuinely stops moving escalates on its own backstop, and is
                // then reported by the escalation lane above, with its reason.
                if (PipelineIsDrivingIt(ledger.Stage))
                {
                    continue;
                }

                // Escalated is reported once, above, by whichever lane holds the
                // reason. Repeating it here would both double the count and relabel
                // a parked run as an ordinary merge decision.
                if (string.Equals(ledger.Stage, PhaseStages.Escalated, StringComparison.Ordinal))
                {
                    continue;
                }

                // What is left is a pull request the pipeline holds and is not
                // acting on, which is the owner's only when the review says so at
                // the head it carries now. A head carrying CHANGES_REQUIRED, or no
                // verdict at all, is not a merge decision waiting on anyone.
                if (!IsApprovedAtHead(ledger, pr.HeadSha))
                {
                    continue;
                }
            }
            else if (IsPlaneOpened(pr.HeadRefName) && IsInFlightByBranch(pr, inFlightIssues))
            {
                // No ledger yet, but the plane is running or queueing the issue this
                // branch belongs to. "The plane is not tracking it, so no review or
                // merge will ever run" is a false alarm while the plane is working
                // on exactly that issue.
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
            // merge - nothing in the plane will ever touch them - so calling every
            // untracked PR a fault would relabel all normal work as breakage. The
            // fault is specifically a branch the PLANE created that the plane is no
            // longer tracking.
            var tracked = ledger is not null || !IsPlaneOpened(pr.HeadRefName);

            var prUrl = string.IsNullOrWhiteSpace(pr.Url) ? null : pr.Url;

            // Whose it is, said out loud. Red CI belongs to whoever authored the
            // change, usually the plane; a branch the plane opened and then stopped
            // tracking is a fault for someone at the machine; only a green pull
            // request nothing else will move is a decision on offer.
            var actor = failing
                ? AttentionActors.Plane
                : tracked ? AttentionActors.Owner : AttentionActors.Operator;

            var action = failing
                ? new AttentionAction("open", "See the failing checks", Url: prUrl)
                : tracked
                    ? new AttentionAction("merge", "Merge it", Url: prUrl)
                    : new AttentionAction("command", "Re-enter it into the pipeline",
                        Command: $"python scripts/command-center.py --readmit {pr.Number}");

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
                prUrl,
                Actor: actor,
                Action: action));
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
            // came to ask what to do about it. It ran clean six minutes later while
            // the plane had been dispatching throughout. Every part of the claim was
            // false, and the certainty was the worst part.
            //
            // Only a task with nothing booked cannot recover without a person.
            var willRunAgain = task.NextRunUtc is { } nextRun && nextRun > now;
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
                // Nobody decides a scheduled task. Someone runs it - and the exact
                // command beats an exit code every time.
                Actor: AttentionActors.Operator,
                Action: new AttentionAction("command", "Run it now",
                    Command: $"schtasks /run /tn \"{task.Name}\"")));
        }

        if (retryQueueCount > 0)
        {
            items.Add(new AttentionItem(
                $"{retryQueueCount} run{(retryQueueCount == 1 ? string.Empty : "s")} waiting to retry",
                "Transient failures retry on their own. Persistent ones escalate.",
                LevelAttention,
                Actor: AttentionActors.Plane,
                Action: null));
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
        // It was a function of severity alone, so anything `down` produced
        // "Something is wrong and it will not clear itself" and "nothing new will
        // be picked up" - both stated as fact, neither measured. A killed and
        // rescheduled task triggered exactly that while the plane went on
        // dispatching, and the owner had to come and ask what to do.
        //
        // A headline addressed to the owner counts the owner's items. The rest are
        // shown so they can see why work is or is not moving, and they say whose
        // they are.
        var ownerItems = items.Count(item => item.Actor == AttentionActors.Owner);
        var otherItems = items.Count - ownerItems;
        var othersPhrase = otherItems == 1
            ? "1 other thing is being handled"
            : $"{otherItems} other things are being handled";

        if (level == LevelDown)
        {
            headline = ownerItems > 0
                ? (ownerItems == 1
                    ? "One thing needs you, and something has stopped"
                    : $"{ownerItems} things need you, and something has stopped")
                : "Something has stopped, and it is not yours to fix";
            detail = runningCount == 0 && retryQueueCount == 0
                ? "Nothing is running. The items marked below will not clear on their own."
                : "The plane is still working. The items marked below will not clear on their own.";
        }
        else if (level == LevelAttention && ownerItems == 0)
        {
            // Everything listed belongs to the plane or to an operator. Saying "N
            // things are waiting on you" here is the exact claim the owner objected
            // to: it counts other people's work at them.
            headline = "Nothing needs you";
            detail = $"{othersPhrase}, listed below so you can see what the plane is doing.";
        }
        else if (level == LevelAttention)
        {
            headline = ownerItems == 1 ? "One thing is waiting on you" : $"{ownerItems} things are waiting on you";
            detail = otherItems == 0
                ? "The plane is running normally. These are decisions it will not make on its own."
                : $"These are decisions the plane will not make on its own. {othersPhrase}.";
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

    /// <summary>
    /// The merge gate's own approval test, applied to what the panel is about to
    /// claim: the recorded verdict is APPROVED and it was recorded against the head
    /// the pull request carries now.
    ///
    /// Approval inferred from anything else - a stage name, a green tick, an open
    /// pull request that has been open a while - is exactly the inference that put
    /// "PR #147 was approved but not merged" on the panel for a change no reviewer
    /// had ever seen. An unknown head fails the test rather than passing it: not
    /// being able to check is not the same as having checked.
    /// </summary>
    private static bool IsApprovedAtHead(PhaseLedgerEntity ledger, string? headSha) =>
        string.Equals(ledger.LastVerdict, ReviewVerdicts.Approved, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(ledger.LastVerdictHeadSha) &&
        !string.IsNullOrWhiteSpace(headSha) &&
        string.Equals(ledger.LastVerdictHeadSha, headSha, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stages the phase machine advances by itself on the next tick. None of them
    /// is waiting on a person, and each one that stops moving escalates on the
    /// stuck-stage backstop rather than sitting silent - so staying quiet here
    /// cannot lose a stall.
    /// </summary>
    private static bool PipelineIsDrivingIt(string stage) =>
        string.Equals(stage, PhaseStages.AwaitingVerify, StringComparison.Ordinal) ||
        string.Equals(stage, PhaseStages.AwaitingReview, StringComparison.Ordinal) ||
        string.Equals(stage, PhaseStages.Reviewing, StringComparison.Ordinal) ||
        string.Equals(stage, PhaseStages.WaitForRepair, StringComparison.Ordinal);

    /// <summary>
    /// What the review says about the head the pull request carries now, said
    /// plainly. The panel's job when it cannot claim an approval is to report the
    /// absence, not to fill it with the commonest story.
    /// </summary>
    private static string VerdictAtHead(string pullRequestLabel, PhaseLedgerEntity ledger)
    {
        var head = Short(ledger.HeadSha);

        if (string.IsNullOrWhiteSpace(ledger.LastVerdict) ||
            string.IsNullOrWhiteSpace(ledger.LastVerdictHeadSha) ||
            !string.Equals(ledger.LastVerdictHeadSha, ledger.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return $"{pullRequestLabel} carries no review verdict at head {head}, so nothing has approved it.";
        }

        return $"{pullRequestLabel} carries {ledger.LastVerdict} at head {head}, so it is not merge-ready as it stands.";
    }

    private static bool IsPlaneOpened(string? headRefName) =>
        headRefName?.StartsWith("symphony/", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether a "symphony/115" branch belongs to an issue the plane is already
    /// moving. The branch carries the issue number and nothing else, so the match
    /// is on the identifier - and on the repository too, because "#115" exists in
    /// every repository the plane watches.
    /// </summary>
    private static bool IsInFlightByBranch(OpenPullRequest pr, IReadOnlyList<InFlightIssue> inFlightIssues)
    {
        var number = pr.HeadRefName!["symphony/".Length..];
        if (number.Length == 0 || !number.All(char.IsDigit))
        {
            return false;
        }

        return inFlightIssues.Any(issue =>
            string.Equals(issue.IssueIdentifier.TrimStart('#'), number, StringComparison.Ordinal) &&
            SameRepository(issue.Repository, pr.Repository));
    }

    /// <summary>
    /// An empty repository key means "the repository that was the only one at the
    /// time", so it matches anything rather than nothing - the same rule
    /// <see cref="Qualify"/> follows for rows written before multi-repository
    /// tracking.
    /// </summary>
    private static bool SameRepository(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) ||
        string.IsNullOrWhiteSpace(right) ||
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

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
