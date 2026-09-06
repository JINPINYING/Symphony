using Symphony.Core.Configuration;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Host.Services;

/// <summary>
/// Process-wide cadence gates for GitHub reads made by scoped tick services.
/// </summary>
public sealed class GitHubTrackerPollCadence
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> nextByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PhaseLedgerPollState> phaseLedgers = new(StringComparer.Ordinal);

    public bool TryEnter(string key, DateTimeOffset nowUtc, TimeSpan interval)
    {
        lock (gate)
        {
            if (nextByKey.TryGetValue(key, out var nextUtc) && nowUtc < nextUtc)
            {
                return false;
            }

            nextByKey[key] = nowUtc + interval;
            return true;
        }
    }

    public bool IsDue(string key, DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            return !nextByKey.TryGetValue(key, out var nextUtc) || nowUtc >= nextUtc;
        }
    }

    public void SetNext(string key, DateTimeOffset nextUtc)
    {
        lock (gate)
        {
            nextByKey[key] = nextUtc;
        }
    }

    public bool SetNextIfLater(string key, DateTimeOffset nextUtc)
    {
        lock (gate)
        {
            if (nextByKey.TryGetValue(key, out var existing) && existing >= nextUtc)
            {
                return false;
            }

            nextByKey[key] = nextUtc;
            return true;
        }
    }

    public bool ShouldPollPhaseLedger(PhaseLedgerEntity ledger, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        lock (gate)
        {
            var key = PhaseLedgerKey(ledger);
            if (!phaseLedgers.TryGetValue(key, out var state))
            {
                return true;
            }

            return ledger.UpdatedAtUtc > state.LedgerUpdatedAtUtc ||
                   !string.Equals(ledger.Stage, state.Stage, StringComparison.Ordinal) ||
                   nowUtc >= state.NextPollUtc;
        }
    }

    public void MarkPhaseLedgerPolled(
        PhaseLedgerEntity ledger,
        string stage,
        DateTimeOffset ledgerUpdatedAtUtc,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        lock (gate)
        {
            phaseLedgers[PhaseLedgerKey(ledger)] = new PhaseLedgerPollState(
                stage,
                ledgerUpdatedAtUtc,
                nowUtc + TrackerReadCadence.PhaseLedgerPoll);
        }
    }

    private static string PhaseLedgerKey(PhaseLedgerEntity ledger) =>
        $"{ledger.Repository}\n{ledger.IssueId}\n{ledger.PrNumber}";

    private sealed record PhaseLedgerPollState(
        string Stage,
        DateTimeOffset LedgerUpdatedAtUtc,
        DateTimeOffset NextPollUtc);
}
