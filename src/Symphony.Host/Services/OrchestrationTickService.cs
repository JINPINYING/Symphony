using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Symphony.Core.Abstractions;
using Symphony.Core.Configuration;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed partial class OrchestrationTickService
{
    private readonly IWorkflowDefinitionProvider workflowDefinitionProvider;
    private readonly IGitHubTrackerClient trackerClient;
    private readonly IOrchestrationCoordinationStore coordinationStore;
    private readonly SymphonyDbContext dbContext;
    private readonly IWorkspaceManager workspaceManager;
    private readonly IIssueExecutionCoordinator issueExecutionCoordinator;
    private readonly EscalationPublisher escalationPublisher;
    private readonly DirectiveProcessor directiveProcessor;
    private readonly PhaseOrchestrator phaseOrchestrator;
    private readonly EventLogRetentionService eventLogRetentionService;
    private readonly TrackerReachability trackerReachability;
    private readonly OrchestrationOptions orchestrationOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OrchestrationTickService> logger;

    // Pruning is hourly, not per tick: the tick runs every 15 seconds and the
    // event log does not need attention that often. In-memory is fine - a
    // restart simply prunes once on the first tick, which is harmless.
    private DateTimeOffset nextEventLogPruneUtc = DateTimeOffset.MinValue;

    // Open pull requests are polled on their own slower clock. The tick runs every
    // 15 seconds; a merge decision does not move that fast, and one GraphQL call
    // per tick would spend rate limit on a question whose answer rarely changes.
    internal const string OpenPullRequestsEventName = "open_pull_requests_updated";
    private const int OpenPullRequestLimit = 25;
    // The candidate scan asks GitHub "is there new work?", and it is the expensive
    // question: one GraphQL query per tracked repository, every time.
    //
    // Multi-repository tracking turned that from one query a tick into three, and
    // on a 15-second tick that is 720 an hour before anything else - which
    // exhausted the 5000-point GraphQL budget twice on 2026-09-01 and left the
    // plane blind until the window reset. Raising the whole tick to 60s fixed the
    // spend and cost something that was not the problem: phase transitions advance
    // on the tick, so every verify, review and merge step also waited a minute.
    //
    // The two are separate questions and now have separate clocks, exactly as the
    // open-pull-request poll already did. The tick stays fast for everything local
    // and for advancing work already known about; only the GitHub scan is slow.
    // A rate limit clears on a clock, not on effort. Retrying it every tick spends
    // the request that would have succeeded later and keeps the plane blind for
    // longer, which is what happened on 2026-09-01: the budget was exhausted and
    // every tick went on asking, 197 times per repository, until the window reset.
    // Ten minutes is short enough to recover well inside GitHub's hourly window and
    // long enough to stop hammering it.
    // Ten minutes is the FIRST wait, not the only one. A limit that is still there
    // ten minutes later is one the plane is walking back into, so each consecutive
    // rate-limited scan doubles the pause, up to the cap below - which is the
    // longest a wait can usefully be, because the primary budget refills hourly and
    // waiting past the reset only postpones work that could already have run.
    //
    // Whenever GitHub names its own clock - Retry-After on a secondary limit, or
    // x-ratelimit-reset on a primary one - that number wins over both. It is the
    // real answer; these are the fallback for when it did not say.
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxRateLimitBackoff = TimeSpan.FromMinutes(60);
    // The cadence lives in Symphony.Core.Configuration.TrackerReadCadence, beside
    // the arithmetic that decides whether it is affordable. It used to be a private
    // constant here, which is how three separate changes each moved one number with
    // nothing recomputing the hourly product. See GitHubTrackerGraphQlCost.
    private static readonly TimeSpan CandidateScanInterval = TrackerReadCadence.CandidateScan;
    private DateTimeOffset nextCandidateScanUtc = DateTimeOffset.MinValue;
    // Consecutive rate-limited scans. In memory on purpose: a restart has observed
    // nothing and should not inherit an escalated backoff, and the pause itself is
    // durable (candidate_scan_pause) so restarting still cannot shorten the wait.
    private int candidateScanRateLimitStreak;
    private IReadOnlyList<NormalizedIssue> lastCandidates = [];
    // Guards the one-time read of a pause recorded by a previous process.
    // Checked every tick, so it must cost nothing after the first.
    private bool candidateScanPauseRestored;

    // The tracked-issue cache refresh asks GitHub about every issue it has ever
    // seen, and it used to do that on every tick because one GraphQL query
    // answered a hundred ids. It now runs on the same clock as the candidate scan
    // it mirrors: a tick can be fifteen seconds, and refreshing a whole cache four
    // times a minute spends the budget this change exists to protect. The
    // dashboard is no staler for it - the scan beside it already moves at 60s.
    private static readonly TimeSpan TrackedIssueRefreshInterval = TrackerReadCadence.TrackedIssueRefresh;
    private DateTimeOffset nextTrackedIssueRefreshUtc = DateTimeOffset.MinValue;

    private static readonly TimeSpan OpenPullRequestPollInterval = TrackerReadCadence.OpenPullRequestPoll;
    private DateTimeOffset nextOpenPullRequestPollUtc = DateTimeOffset.MinValue;

    public OrchestrationTickService(
        IWorkflowDefinitionProvider workflowDefinitionProvider,
        IGitHubTrackerClient trackerClient,
        IOrchestrationCoordinationStore coordinationStore,
        SymphonyDbContext dbContext,
        IWorkspaceManager workspaceManager,
        IIssueExecutionCoordinator issueExecutionCoordinator,
        EscalationPublisher escalationPublisher,
        DirectiveProcessor directiveProcessor,
        PhaseOrchestrator phaseOrchestrator,
        EventLogRetentionService eventLogRetentionService,
        TrackerReachability trackerReachability,
        IOptions<OrchestrationOptions> orchestrationOptions,
        TimeProvider timeProvider,
        ILogger<OrchestrationTickService> logger)
    {
        this.workflowDefinitionProvider = workflowDefinitionProvider;
        this.trackerClient = trackerClient;
        this.coordinationStore = coordinationStore;
        this.dbContext = dbContext;
        this.workspaceManager = workspaceManager;
        this.issueExecutionCoordinator = issueExecutionCoordinator;
        this.escalationPublisher = escalationPublisher;
        this.directiveProcessor = directiveProcessor;
        this.phaseOrchestrator = phaseOrchestrator;
        this.eventLogRetentionService = eventLogRetentionService;
        this.trackerReachability = trackerReachability;
        this.orchestrationOptions = orchestrationOptions.Value;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task RunStartupCleanupAsync(CancellationToken cancellationToken)
    {
        var workflowDefinition = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
        await PersistWorkflowSnapshotAsync(workflowDefinition, cancellationToken);

        string apiKey;
        try
        {
            apiKey = WorkflowDispatchPreflightValidator.ValidateAndResolveApiKey(workflowDefinition);
        }
        catch (WorkflowLoadException ex)
        {
            logger.LogWarning(
                ex,
                "Skipping startup terminal cleanup because workflow preflight validation failed with code {Code} for {WorkflowPath}.",
                ex.Code,
                workflowDefinition.SourcePath);
            return;
        }

        var instanceId = ResolveInstanceId();
        var hasLease = await coordinationStore.AcquireOrRenewLeaseAsync(
            ResolveLeaseName(),
            instanceId,
            ResolveLeaseTtl(),
            cancellationToken);

        if (!hasLease)
        {
            logger.LogDebug(
                "Skipping startup terminal cleanup because lease '{LeaseName}' is owned by another instance. InstanceId={InstanceId}",
                ResolveLeaseName(),
                instanceId);
            return;
        }

        try
        {
            await RunStartupCleanupCoreAsync(workflowDefinition, apiKey, cancellationToken);
        }
        finally
        {
            await coordinationStore.ReleaseLeaseAsync(ResolveLeaseName(), instanceId, CancellationToken.None);
        }
    }

    public async Task<int?> RunTickAsync(CancellationToken cancellationToken)
    {
        var workflowDefinition = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
        await PersistWorkflowSnapshotAsync(workflowDefinition, cancellationToken);

        string? apiKey = null;
        WorkflowLoadException? preflightError = null;
        try
        {
            apiKey = WorkflowDispatchPreflightValidator.ValidateAndResolveApiKey(workflowDefinition);
        }
        catch (WorkflowLoadException ex)
        {
            preflightError = ex;
        }

        var instanceId = ResolveInstanceId();
        var hasLease = await coordinationStore.AcquireOrRenewLeaseAsync(
            ResolveLeaseName(),
            instanceId,
            ResolveLeaseTtl(),
            cancellationToken);

        if (!hasLease)
        {
            logger.LogDebug(
                "Skipping tick because lease '{LeaseName}' is owned by another instance. InstanceId={InstanceId}",
                ResolveLeaseName(),
                instanceId);
            return workflowDefinition.Runtime.Polling.IntervalMs;
        }

        try
        {
            await RecoverOrphanedStateAsync(instanceId, workflowDefinition, cancellationToken);
            await ReconcileStartupAttemptsAsync(workflowDefinition, instanceId, cancellationToken);
            await ReconcileWedgedRetriesAsync(workflowDefinition, instanceId, cancellationToken);
            await ReconcileRunningIssuesAsync(workflowDefinition, apiKey, preflightError, instanceId, cancellationToken);
            await PruneEventLogIfDueAsync(workflowDefinition, cancellationToken);
            if (preflightError is null && !string.IsNullOrWhiteSpace(apiKey))
            {
                await RefreshTrackedIssueCacheStatesAsync(workflowDefinition, apiKey, instanceId, cancellationToken);
            }

            if (preflightError is not null || string.IsNullOrWhiteSpace(apiKey))
            {
                logger.LogError(
                    preflightError,
                    "Skipping dispatch for workflow {WorkflowPath} because preflight validation failed with code {Code}.",
                    workflowDefinition.SourcePath,
                    preflightError?.Code ?? "missing_tracker_api_key");
                return workflowDefinition.Runtime.Polling.IntervalMs;
            }

            // M4: phases run BEFORE ordinary dispatch. An issue whose
            // implementation is durable belongs to the phase machine (verify ->
            // cross-vendor review -> bounded repair); seeding its ledger first is
            // what makes DispatchCandidatesAsync skip it. With the order reversed,
            // the candidate loop claimed such an issue on the same tick and
            // overwrote the review run's phase and runner.
            await phaseOrchestrator.ProcessPhasesAsync(
                workflowDefinition,
                BuildTrackerQueries(workflowDefinition, apiKey),
                (issue, phaseRequest, token) => DispatchPhaseIssueAsync(issue, workflowDefinition, instanceId, phaseRequest, token),
                cancellationToken);

            await DispatchCandidatesAsync(workflowDefinition, apiKey, instanceId, cancellationToken);

            // M1: escalations created this tick (or still pending from earlier
            // ticks) are published to GitHub before the tick ends, so a parked
            // escalation reaches the owner within one tick.
            //
            // Every query, not the primary one: an escalation is published on the
            // issue in the repository its run belongs to, and asking the wrong
            // repository about a global node id returns nothing rather than
            // failing loudly.
            await escalationPublisher.PublishPendingEscalationsAsync(
                BuildTrackerQueries(workflowDefinition, apiKey),
                cancellationToken);

            // M3: command-center directives on escalated issues are consumed and
            // acted on — one comment un-parks a stuck issue. Same reason as above
            // for handing over the whole set: a directive on an ADCP or Symphony
            // issue used to be read against the primary repository, come back
            // empty, and be discarded as "the issue does not exist".
            await directiveProcessor.ProcessPendingDirectivesAsync(
                workflowDefinition,
                BuildTrackerQueries(workflowDefinition, apiKey),
                (issue, directive, token) => DispatchDirectiveIssueAsync(issue, workflowDefinition, instanceId, directive, token),
                cancellationToken);

            await RecordOpenPullRequestsAsync(workflowDefinition, apiKey, cancellationToken);
            return workflowDefinition.Runtime.Polling.IntervalMs;
        }
        finally
        {
            await coordinationStore.ReleaseLeaseAsync(ResolveLeaseName(), instanceId, CancellationToken.None);
        }
    }

    // The status page must not call GitHub. It is rendered on every poll and by
    // the published copy, so a slow or unreachable API there would make the page
    // slow or blank exactly when the owner is checking whether anything is wrong.
    // So the tick fetches, and the page reads the newest snapshot from the event
    // log - the same shape rate limits already use, which needs no schema change.
    //
    // Never fatal. Pull requests are a reporting concern; failing to read them
    // must not stop a tick that is otherwise dispatching work.
    private async Task RecordOpenPullRequestsAsync(
        WorkflowDefinition workflowDefinition,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (timeProvider.GetUtcNow() < nextOpenPullRequestPollUtc)
        {
            return;
        }

        try
        {
            // Every tracked repository, merged into one snapshot: "what is sitting
            // open waiting for a person" is a question about the plane's whole
            // surface, not about whichever repository happens to be first.
            var openPullRequests = new List<OpenPullRequest>();
            foreach (var repositoryQuery in BuildTrackerQueries(workflowDefinition, apiKey).All)
            {
                openPullRequests.AddRange(await trackerClient.FetchOpenPullRequestsAsync(
                    repositoryQuery,
                    OpenPullRequestLimit,
                    cancellationToken));
            }

            var now = timeProvider.GetUtcNow();
            nextOpenPullRequestPollUtc = now + OpenPullRequestPollInterval;

            dbContext.EventLog.Add(new EventLogEntity
            {
                EventName = OpenPullRequestsEventName,
                Level = LogLevel.Information.ToString(),
                Message = openPullRequests.Count == 1
                    ? "1 open pull request."
                    : $"{openPullRequests.Count} open pull requests.",
                DataJson = JsonSerializer.Serialize(openPullRequests),
                OccurredAtUtc = now
            });

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Back off on failure too, so a broken token does not retry every tick.
            nextOpenPullRequestPollUtc = timeProvider.GetUtcNow() + OpenPullRequestPollInterval;
            logger.LogWarning(exception, "Could not read open pull requests; the status page will show the previous snapshot.");
        }
    }

    private string ResolveInstanceId()
    {
        if (!string.IsNullOrWhiteSpace(orchestrationOptions.InstanceId))
        {
            return orchestrationOptions.InstanceId;
        }

        return $"{Environment.MachineName}-{Environment.ProcessId}";
    }

    private string ResolveLeaseName()
    {
        return string.IsNullOrWhiteSpace(orchestrationOptions.LeaseName)
            ? "poll-dispatch"
            : orchestrationOptions.LeaseName;
    }

    private TimeSpan ResolveLeaseTtl() => TimeSpan.FromSeconds(orchestrationOptions.LeaseTtlSeconds);

    private async Task PruneEventLogIfDueAsync(
        WorkflowDefinition workflowDefinition,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < nextEventLogPruneUtc)
        {
            return;
        }

        nextEventLogPruneUtc = now.AddHours(1);

        try
        {
            await eventLogRetentionService.PruneAsync(
                workflowDefinition.Runtime.EventLogRetention,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Housekeeping must never take the tick down. It retries next hour.
            logger.LogError(ex, "Event log pruning failed; the tick continues.");
        }
    }
}
