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
    private static readonly TimeSpan OpenPullRequestPollInterval = TimeSpan.FromMinutes(2);
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
                BuildTrackerQuery(workflowDefinition, apiKey),
                (issue, phaseRequest, token) => DispatchPhaseIssueAsync(issue, workflowDefinition, instanceId, phaseRequest, token),
                cancellationToken);

            await DispatchCandidatesAsync(workflowDefinition, apiKey, instanceId, cancellationToken);

            // M1: escalations created this tick (or still pending from earlier
            // ticks) are published to GitHub before the tick ends, so a parked
            // escalation reaches the owner within one tick.
            await escalationPublisher.PublishPendingEscalationsAsync(
                BuildTrackerQuery(workflowDefinition, apiKey),
                cancellationToken);

            // M3: command-center directives on escalated issues are consumed and
            // acted on — one comment un-parks a stuck issue.
            await directiveProcessor.ProcessPendingDirectivesAsync(
                workflowDefinition,
                BuildTrackerQuery(workflowDefinition, apiKey),
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
            var openPullRequests = await trackerClient.FetchOpenPullRequestsAsync(
                BuildTrackerQuery(workflowDefinition, apiKey),
                OpenPullRequestLimit,
                cancellationToken);

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
