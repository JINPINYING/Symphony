using Symphony.Core.Configuration;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Tracker.GitHub;

/// <summary>
/// How much GitHub REST <c>core</c> budget the plane spends in ordinary running.
/// </summary>
public static class GitHubTrackerRestCost
{
    public sealed record Load(
        int RepositoryCount,
        int OpenPullRequestsPerRepository,
        int ActivePhaseLedgers,
        int EscalatedIssues,
        int ParkedSweepBranchLookupsPerRepository);

    public static readonly Load PessimisticSteadyState = new(
        RepositoryCount: TrackerReadCadence.ModelledRepositoryCount,
        OpenPullRequestsPerRepository: 5,
        ActivePhaseLedgers: 4,
        EscalatedIssues: 1,
        ParkedSweepBranchLookupsPerRepository: 1);

    public static RestHourlyCost Model(Load load)
    {
        ArgumentNullException.ThrowIfNull(load);

        var repositories = Math.Max(1, load.RepositoryCount);
        var candidateScans = TrackerReadCadence.CallsPerHour(TrackerReadCadence.CandidateScan) * repositories;
        var issueRefreshes = TrackerReadCadence.CallsPerHour(TrackerReadCadence.TrackedIssueRefresh) * repositories;
        var openPullRequestPolls = TrackerReadCadence.CallsPerHour(TrackerReadCadence.OpenPullRequestPoll) * repositories;
        var phasePolls = TrackerReadCadence.CallsPerHour(TrackerReadCadence.PhaseLedgerPoll);
        var directivePolls = TrackerReadCadence.CallsPerHour(TrackerReadCadence.EscalatedIssueDirectivePoll);
        var parkedSweeps = TrackerReadCadence.CallsPerHour(TrackerReadCadence.ParkedRunSweep) * repositories;

        var reads = new List<RestReadCost>
        {
            new(
                GitHubRestCallSites.CandidateScan,
                RequestsPerCall: 1,
                candidateScans,
                $"{repositories} repositories, candidate scan every {TrackerReadCadence.CandidateScan.TotalSeconds:0}s"),
            new(
                GitHubRestCallSites.IssueStateListing,
                RequestsPerCall: 1,
                issueRefreshes,
                $"{repositories} repositories, tracked issue refresh every {TrackerReadCadence.TrackedIssueRefresh.TotalSeconds:0}s")
        };

        if (load.OpenPullRequestsPerRepository > 0)
        {
            reads.Add(new RestReadCost(
                GitHubRestCallSites.OpenPullRequests,
                RequestsPerCall: 1,
                openPullRequestPolls,
                $"{repositories} repositories, open PR list every {TrackerReadCadence.OpenPullRequestPoll.TotalSeconds:0}s"));
            reads.Add(new RestReadCost(
                GitHubRestCallSites.CommitStatus,
                RequestsPerCall: load.OpenPullRequestsPerRepository,
                openPullRequestPolls,
                $"{load.OpenPullRequestsPerRepository} open PRs per repository in the status snapshot"));
            reads.Add(new RestReadCost(
                GitHubRestCallSites.CommitCheckRuns,
                RequestsPerCall: load.OpenPullRequestsPerRepository,
                openPullRequestPolls,
                $"{load.OpenPullRequestsPerRepository} open PRs per repository in the status snapshot"));
        }

        if (load.ActivePhaseLedgers > 0)
        {
            reads.Add(new RestReadCost(
                GitHubRestCallSites.PullRequestByNumber,
                RequestsPerCall: 1,
                phasePolls * load.ActivePhaseLedgers,
                $"{load.ActivePhaseLedgers} active phase ledgers, PR status every {TrackerReadCadence.PhaseLedgerPoll.TotalSeconds:0}s"));
            reads.Add(new RestReadCost(
                GitHubRestCallSites.CommitStatus,
                RequestsPerCall: 1,
                phasePolls * load.ActivePhaseLedgers,
                $"{load.ActivePhaseLedgers} active phase ledgers, commit status every {TrackerReadCadence.PhaseLedgerPoll.TotalSeconds:0}s"));
            reads.Add(new RestReadCost(
                GitHubRestCallSites.CommitCheckRuns,
                RequestsPerCall: 1,
                phasePolls * load.ActivePhaseLedgers,
                $"{load.ActivePhaseLedgers} active phase ledgers, check runs every {TrackerReadCadence.PhaseLedgerPoll.TotalSeconds:0}s"));
            reads.Add(new RestReadCost(
                GitHubRestCallSites.IssueComments,
                RequestsPerCall: 2,
                phasePolls * load.ActivePhaseLedgers,
                $"{load.ActivePhaseLedgers} active phase ledgers may wait on review or repair comments every {TrackerReadCadence.PhaseLedgerPoll.TotalSeconds:0}s"));
        }

        if (load.EscalatedIssues > 0)
        {
            reads.Add(new RestReadCost(
                GitHubRestCallSites.IssueComments,
                RequestsPerCall: 2,
                directivePolls * load.EscalatedIssues,
                $"{load.EscalatedIssues} escalated issue(s), comments may walk two pages every {TrackerReadCadence.EscalatedIssueDirectivePoll.TotalSeconds:0}s"));
            reads.Add(new RestReadCost(
                GitHubRestCallSites.IssueCommentMarker,
                RequestsPerCall: 2,
                directivePolls * load.EscalatedIssues,
                $"{load.EscalatedIssues} pending escalation marker probe(s) read the issue plus comment pages per directive poll"));
        }

        reads.Add(new RestReadCost(
            GitHubRestCallSites.IssueByNumber,
            RequestsPerCall: 1,
            parkedSweeps,
            $"{repositories} repositories, parked-run sweep every {TrackerReadCadence.ParkedRunSweep.TotalSeconds:0}s"));
        reads.Add(new RestReadCost(
            GitHubRestCallSites.OpenPullRequests,
            RequestsPerCall: 1,
            parkedSweeps,
            $"{repositories} repositories, parked-run open PR check every {TrackerReadCadence.ParkedRunSweep.TotalSeconds:0}s"));

        if (load.ParkedSweepBranchLookupsPerRepository > 0)
        {
            reads.Add(new RestReadCost(
                GitHubRestCallSites.PullRequestByHeadBranch,
                RequestsPerCall: load.ParkedSweepBranchLookupsPerRepository,
                parkedSweeps,
                $"{load.ParkedSweepBranchLookupsPerRepository} branch lookup(s) per repository per parked-run sweep"));
        }

        // Both halves of the no-phase-owns-this recovery confirm a pull request at a
        // specific head before writing anything durable against it (#94): entering
        // an unowned pull request into the pipeline, and re-arming an escalated
        // ledger whose head has moved. Neither is a poll - the first is bounded per
        // head and stops once a ledger exists, and the second is triggered by the
        // open-pull-request snapshot showing a head that no judgement covers. One
        // such confirmation per repository per sweep is the pessimistic shape, not
        // the expected one.
        reads.Add(new RestReadCost(
            GitHubRestCallSites.PullRequestByNumber,
            RequestsPerCall: 1,
            parkedSweeps,
            $"one unowned/re-armed pull request confirmation per repository, modelled every {TrackerReadCadence.ParkedRunSweep.TotalSeconds:0}s"));
        reads.Add(new RestReadCost(
            GitHubRestCallSites.CommitStatus,
            RequestsPerCall: 1,
            parkedSweeps,
            "commit status on the unowned/re-armed pull request confirmation"));
        reads.Add(new RestReadCost(
            GitHubRestCallSites.CommitCheckRuns,
            RequestsPerCall: 1,
            parkedSweeps,
            "check runs on the unowned/re-armed pull request confirmation"));

        reads.Add(new RestReadCost(
            GitHubRestCallSites.PullRequestFiles,
            RequestsPerCall: 1,
            CallsPerHour: 10,
            "merge-policy file checks are operator/phase events, modelled as ten PRs per hour"));

        return new RestHourlyCost(reads);
    }
}
