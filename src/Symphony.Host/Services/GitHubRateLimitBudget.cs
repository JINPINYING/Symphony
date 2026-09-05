using Symphony.Core.Abstractions;
using Symphony.Core.Models;

namespace Symphony.Host.Services;

/// <summary>
/// What is left of a GitHub budget, and how fast it is going.
/// </summary>
/// <param name="Resource">"graphql" or "core" - the two budgets are separate and exhausting either blinds different things.</param>
/// <param name="BurnPointsPerHour">
/// Measured across the readings seen so far in the current window, or null when
/// too few have been seen to measure one. Null is UNKNOWN, not zero: a burn rate
/// reported as zero on one reading would say "this will never run out" about a
/// budget that is being spent.
/// </param>
/// <param name="ProjectedExhaustionUtc">
/// When the remaining allowance runs out at the measured rate, or null when the
/// rate is unknown or the window resets first.
/// </param>
public sealed record GitHubRateLimitBudgetSnapshot(
    string Resource,
    int Limit,
    int Used,
    int Remaining,
    double UsedPercent,
    DateTimeOffset? ResetAtUtc,
    DateTimeOffset ObservedAtUtc,
    double? BurnPointsPerHour,
    DateTimeOffset? ProjectedExhaustionUtc);

/// <summary>
/// Keeps what GitHub said about its own budgets, from the headers on the calls
/// the plane is already making.
///
/// WHY THIS EXISTS. On 2026-09-05 the plane spent all 5,000 points of the hourly
/// GraphQL budget and went blind for eighteen minutes, and the first sign of it
/// was the blindness. The reading had been in every response header all along -
/// <c>X-Ratelimit-Used: 5011</c>, <c>X-Ratelimit-Remaining: 0</c> - and nothing
/// was reading them. The endpoint reached for instead - <c>gh api rate_limit</c> -
/// reads 5,000 remaining at a glance, because its top-level <c>rate</c> block is
/// the core budget and the GraphQL figure is buried at <c>.resources.graphql</c>.
/// It is also a separate call, made only when someone thinks to make it. The
/// headers are on every answer the plane already receives.
///
/// Singleton, because a burn rate is only meaningful across calls. Resetting on
/// restart is correct rather than merely tolerable: a process that has just
/// started has observed one reading and cannot yet say how fast anything is
/// being spent, and it should say that rather than guess.
/// </summary>
public sealed class GitHubRateLimitBudget(TimeProvider timeProvider) : IGitHubRateLimitObserver
{
    /// <summary>
    /// Where the budget stops being a detail and starts being a warning. Chosen so
    /// there is still a fifth of the window left to act in: at the burn rate that
    /// exhausted the budget on 2026-09-05, 80% was reached about seven minutes
    /// before zero, which is enough time to stop something.
    /// </summary>
    public const double AttentionPercent = 80.0;

    /// <summary>
    /// The shortest span across which a burn rate is worth reporting. Two readings
    /// a second apart put a rounding difference over a tiny denominator and
    /// produce a rate of tens of thousands an hour, which would fire an alarm
    /// about arithmetic rather than about GitHub.
    /// </summary>
    private static readonly TimeSpan MinimumBurnWindow = TimeSpan.FromMinutes(1);

    private sealed record Window(GitHubRateLimitReading First, GitHubRateLimitReading Latest);

    private readonly object gate = new();
    private readonly Dictionary<string, Window> windows = new(StringComparer.OrdinalIgnoreCase);

    public void Record(GitHubRateLimitReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        lock (gate)
        {
            if (!windows.TryGetValue(reading.Resource, out var window) || StartsNewWindow(window.Latest, reading))
            {
                windows[reading.Resource] = new Window(reading, reading);
                return;
            }

            // Out-of-order arrival. Concurrent requests finish in whatever order
            // the network gives them, and a stale reading must not walk the
            // measured usage backwards.
            if (reading.ObservedAtUtc < window.Latest.ObservedAtUtc)
            {
                return;
            }

            windows[reading.Resource] = window with { Latest = reading };
        }
    }

    /// <summary>
    /// Whether this reading belongs to a later window than the one being measured.
    /// The reset timestamp names the window; a usage count that has fallen is the
    /// same evidence for a token whose responses omit the reset header.
    /// </summary>
    private static bool StartsNewWindow(GitHubRateLimitReading latest, GitHubRateLimitReading incoming)
    {
        if (latest.ResetAtUtc is { } previousReset && incoming.ResetAtUtc is { } incomingReset)
        {
            return incomingReset > previousReset;
        }

        return incoming.Used < latest.Used;
    }

    public IReadOnlyList<GitHubRateLimitBudgetSnapshot> Current
    {
        get
        {
            var now = timeProvider.GetUtcNow();

            lock (gate)
            {
                return windows.Values
                    .Select(window => Describe(window, now))
                    .OrderBy(snapshot => snapshot.Resource, StringComparer.Ordinal)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// The budgets at or past <see cref="AttentionPercent"/>. Empty when nothing
    /// has been observed - an unobserved budget is unknown, and reporting unknown
    /// as healthy is the failure this whole type exists to end.
    /// </summary>
    public IReadOnlyList<GitHubRateLimitBudgetSnapshot> NeedingAttention =>
        Current.Where(snapshot => snapshot.UsedPercent >= AttentionPercent).ToList();

    private static GitHubRateLimitBudgetSnapshot Describe(Window window, DateTimeOffset now)
    {
        var latest = window.Latest;
        var elapsed = latest.ObservedAtUtc - window.First.ObservedAtUtc;
        var spent = latest.Used - window.First.Used;

        double? burn = elapsed >= MinimumBurnWindow && spent >= 0
            ? spent / elapsed.TotalHours
            : null;

        DateTimeOffset? projected = null;
        if (burn is { } rate && rate > 0 && latest.Remaining > 0)
        {
            var runsOutAt = now.AddHours(latest.Remaining / rate);

            // A window that resets before the allowance runs out does not run out.
            // Saying "exhausted at 12:40" about a budget that refills at 12:19 is
            // the alarm that teaches its reader to ignore the next one.
            projected = latest.ResetAtUtc is { } reset && runsOutAt >= reset ? null : runsOutAt;
        }

        return new GitHubRateLimitBudgetSnapshot(
            latest.Resource,
            latest.Limit,
            latest.Used,
            latest.Remaining,
            latest.UsedPercent,
            latest.ResetAtUtc,
            latest.ObservedAtUtc,
            burn,
            projected);
    }
}
