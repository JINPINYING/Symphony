using System.Net.Http;
using System.Net.Sockets;

namespace Symphony.Host.Services;

/// <summary>
/// A point-in-time view of whether the engine can still see its tracker.
/// </summary>
/// <param name="ConsecutiveFailures">Failed scans since the last successful one.</param>
/// <param name="UnreachableSinceUtc">When the current failure streak began, or null while healthy.</param>
/// <param name="LastFailureReason">The innermost cause, in the words the network gave us.</param>
/// <param name="LastFailureTransient">Whether that cause looked like connectivity rather than a real refusal.</param>
/// <param name="ScanPausedUntilUtc">
/// When the engine has deliberately stopped scanning until a clock runs out, and
/// null when it has not. This is the difference between "cannot see" and "has
/// chosen to wait", which look identical in every other field here and are
/// opposite answers to "does this need a person".
/// </param>
/// <param name="ScanPauseReason">Why the pause was taken, in GitHub's own words.</param>
public sealed record TrackerReachabilitySnapshot(
    int ConsecutiveFailures,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? UnreachableSinceUtc,
    string? LastFailureReason,
    bool LastFailureTransient,
    DateTimeOffset? ScanPausedUntilUtc = null,
    string? ScanPauseReason = null);

/// <summary>
/// Tracks whether the engine can reach its issue tracker.
///
/// WHY THIS EXISTS. Candidate scans were already failing and already being
/// logged, but as twelve identical rows reading "GitHubTrackerException." - the
/// exception's type and nothing else. Finding out that the real cause was
/// intermittent DNS (<c>No such host is known (api.github.com:443)</c>) took a
/// dig through 64 MB of rotated service log, which is not a diagnostic anyone
/// performs while glancing at a status page.
///
/// The deeper problem was not the wording. A tracker the engine cannot reach is
/// a blind plane: no work is found, no work is dispatched, and every internal
/// signal looks exactly like a quiet queue. That is the same shape of blind spot
/// as a scheduler that stops firing, and it needs the same treatment - the engine
/// has to notice that it has stopped being able to see, and say so.
///
/// Singleton, because a streak is only meaningful across ticks. Resetting on
/// restart is correct rather than merely tolerable: a process that has just
/// started has not yet observed anything, and should not inherit an alarm.
/// </summary>
public sealed class TrackerReachability(TimeProvider timeProvider)
{
    /// <summary>
    /// How long the tracker must stay unreachable before it is worth a person's
    /// attention.
    ///
    /// A single failed scan is noise. On a 15-second poll the observed DNS blips
    /// resolved within one or two ticks and cost nothing, and a page that reports
    /// each of them is a page that trains its reader to ignore red. Ten minutes
    /// is well past any blip and still well inside "before it matters": nothing
    /// is being picked up during it.
    /// </summary>
    public static readonly TimeSpan UnreachableGrace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a self-clearing pause may keep recurring before it stops being
    /// described as self-clearing.
    ///
    /// A rate limit is a pause the plane recovers from without anyone: it backs
    /// off, the window resets, scanning resumes. Reporting that to the owner as a
    /// standing demand is the defect this exists to prevent. But "it will recover"
    /// is an observation with an expiry date - a token refused for an hour is no
    /// longer recovering, it is stuck, and calling that self-clearing would be the
    /// same over-claim pointing the other way.
    ///
    /// An hour, because that is GitHub's own primary window: past it, waiting is
    /// no longer the explanation.
    /// </summary>
    public static readonly TimeSpan SelfRecoveryLimit = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset? _unreachableSinceUtc;
    private string? _lastFailureReason;
    private bool _lastFailureTransient;
    private DateTimeOffset? _scanPausedUntilUtc;
    private string? _scanPauseReason;

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _unreachableSinceUtc = null;
            _lastFailureReason = null;
            _scanPausedUntilUtc = null;
            _scanPauseReason = null;
            _lastSuccessUtc = timeProvider.GetUtcNow();
        }
    }

    /// <summary>
    /// The engine has stopped asking on purpose, until <paramref name="resumeAtUtc"/>.
    ///
    /// Recorded apart from the failure that caused it because the two say
    /// different things to a reader. A failure says the plane cannot see. A pause
    /// says it has stopped looking deliberately and knows when it starts again.
    /// Only the first is a fault, and conflating them is how a ten-minute backoff
    /// was presented to the owner as an outage.
    ///
    /// Monotonic, so a pause restored from a row written by a previous process
    /// cannot cut short one the running process has already decided on.
    /// </summary>
    public void RecordScanPause(DateTimeOffset resumeAtUtc, string? reason)
    {
        lock (_gate)
        {
            if (_scanPausedUntilUtc is null || resumeAtUtc > _scanPausedUntilUtc)
            {
                _scanPausedUntilUtc = resumeAtUtc;
            }

            _scanPauseReason = reason;
        }
    }

    public void RecordFailure(string reason, bool transient)
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            // Kept from the FIRST failure of the streak, not refreshed on each
            // one, so the age reported is how long the engine has actually been
            // blind rather than how long since it last retried.
            _unreachableSinceUtc ??= timeProvider.GetUtcNow();
            _lastFailureReason = reason;
            _lastFailureTransient = transient;
        }
    }

    public TrackerReachabilitySnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return new TrackerReachabilitySnapshot(
                    _consecutiveFailures,
                    _lastSuccessUtc,
                    _unreachableSinceUtc,
                    _lastFailureReason,
                    _lastFailureTransient,
                    _scanPausedUntilUtc,
                    _scanPauseReason);
            }
        }
    }

    /// <summary>
    /// The message a person can act on: the innermost cause, not the wrapper.
    /// <c>GitHubTrackerException: GitHub GraphQL request failed</c> says only that
    /// something went wrong; its inner <c>SocketException: No such host is known</c>
    /// says what, and points at DNS rather than at GitHub or at us.
    /// </summary>
    public static string DescribeCause(Exception ex)
    {
        var innermost = ex;
        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }

        return ReferenceEquals(innermost, ex)
            ? ex.Message
            : $"{ex.Message} ({innermost.Message})";
    }

    /// <summary>
    /// Whether a failure looks like the network rather than a refusal.
    ///
    /// The distinction earns its keep: connectivity recovers on its own and
    /// deserves patience, whereas a rejected credential or a malformed query will
    /// fail identically forever and deserves none. Treating the two the same is
    /// how a real outage hides inside a stream of blips.
    /// </summary>
    public static bool IsTransientConnectivity(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SocketException:
                case TimeoutException:
                    return true;
                case TaskCanceledException:
                    // A cancellation with no user token behind it is a timeout
                    // wearing a different name.
                    return true;
                case HttpRequestException http when http.StatusCode is null:
                    // No status code means the request never reached a server.
                    return true;
            }
        }

        return false;
    }
}
