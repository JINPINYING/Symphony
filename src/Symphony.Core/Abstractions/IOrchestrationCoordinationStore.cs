namespace Symphony.Core.Abstractions;

public interface IOrchestrationCoordinationStore
{
    Task<bool> AcquireOrRenewLeaseAsync(
        string leaseName,
        string instanceId,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken = default);

    Task ReleaseLeaseAsync(
        string leaseName,
        string instanceId,
        CancellationToken cancellationToken = default);

    Task<IssueClaimResult> TryClaimIssueAsync(
        string issueId,
        string issueIdentifier,
        string leaseName,
        string instanceId,
        CancellationToken cancellationToken = default);

    Task ReleaseIssueClaimAsync(
        string issueId,
        string instanceId,
        string releaseStatus,
        CancellationToken cancellationToken = default);
}

public readonly record struct IssueClaimResult(bool Claimed, string Reason)
{
    public static IssueClaimResult Accepted() => new(true, "claimed");

    public static IssueClaimResult Refused(string reason) => new(false, reason);
}
