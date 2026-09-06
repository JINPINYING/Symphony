using Symphony.Core.Abstractions;
using Symphony.Core.Models;

namespace Symphony.Host.Services;

public sealed record GitHubApiCallBudgetSnapshot(
    string CallSite,
    string Resource,
    int ChargedCalls,
    int NotModifiedCalls,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    double? PointsPerHour);

/// <summary>
/// Runtime attribution for GitHub API calls by call site and budget resource.
/// </summary>
public sealed class GitHubApiCallBudget(TimeProvider timeProvider) : IGitHubApiCallObserver
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromHours(1);

    private readonly object gate = new();
    private readonly Dictionary<(string CallSite, string Resource), Window> windows = new();

    public void Record(GitHubApiCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        var now = call.ObservedAtUtc == default ? timeProvider.GetUtcNow() : call.ObservedAtUtc;
        var callSite = string.IsNullOrWhiteSpace(call.CallSite) ? "unknown" : call.CallSite.Trim();
        var resource = string.IsNullOrWhiteSpace(call.Resource)
            ? GitHubApiCall.UnknownResource
            : call.Resource.Trim().ToLowerInvariant();

        lock (gate)
        {
            Prune(now);

            var key = (callSite, resource);
            if (!windows.TryGetValue(key, out var window) || now - window.FirstObservedAtUtc > WindowLength)
            {
                window = new Window(now);
                windows[key] = window;
            }

            window.LastObservedAtUtc = now;
            if (call.Charged)
            {
                window.ChargedCalls++;
            }
            else
            {
                window.NotModifiedCalls++;
            }
        }
    }

    public IReadOnlyList<GitHubApiCallBudgetSnapshot> Current
    {
        get
        {
            var now = timeProvider.GetUtcNow();
            lock (gate)
            {
                Prune(now);

                return windows
                    .Select(pair => Describe(pair.Key.CallSite, pair.Key.Resource, pair.Value))
                    .OrderByDescending(snapshot => snapshot.PointsPerHour ?? 0)
                    .ThenBy(snapshot => snapshot.CallSite, StringComparer.Ordinal)
                    .ThenBy(snapshot => snapshot.Resource, StringComparer.Ordinal)
                    .ToList();
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var stale in windows
                     .Where(pair => now - pair.Value.LastObservedAtUtc > WindowLength)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            windows.Remove(stale);
        }
    }

    private static GitHubApiCallBudgetSnapshot Describe(string callSite, string resource, Window window)
    {
        var elapsed = window.LastObservedAtUtc - window.FirstObservedAtUtc;
        var pointsPerHour = elapsed >= TimeSpan.FromMinutes(1) && window.ChargedCalls > 0
            ? window.ChargedCalls * TimeSpan.FromHours(1).TotalSeconds / elapsed.TotalSeconds
            : (double?)null;

        return new GitHubApiCallBudgetSnapshot(
            callSite,
            resource,
            window.ChargedCalls,
            window.NotModifiedCalls,
            window.FirstObservedAtUtc,
            window.LastObservedAtUtc,
            pointsPerHour);
    }

    private sealed class Window(DateTimeOffset firstObservedAtUtc)
    {
        public DateTimeOffset FirstObservedAtUtc { get; } = firstObservedAtUtc;
        public DateTimeOffset LastObservedAtUtc { get; set; } = firstObservedAtUtc;
        public int ChargedCalls { get; set; }
        public int NotModifiedCalls { get; set; }
    }
}
