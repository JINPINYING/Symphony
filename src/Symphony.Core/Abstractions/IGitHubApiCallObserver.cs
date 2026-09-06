using Symphony.Core.Models;

namespace Symphony.Core.Abstractions;

/// <summary>
/// Where a tracker adapter reports the calls it made, so the budget they spent
/// can be attributed to the read that spent it.
///
/// Separate from <see cref="IGitHubRateLimitObserver"/> because the two answer
/// different questions and one has repeatedly been mistaken for the other. The
/// rate-limit headers say HOW MUCH of a budget is gone; they never say what took
/// it. Attributing the spend is what turns "core is at 100%" - the state the
/// plane was in on 2026-09-06 - into a call site that can be changed.
///
/// Implementations must not throw: attribution is telemetry taken on the way past
/// a real call, and losing the telemetry must never lose the call.
/// </summary>
public interface IGitHubApiCallObserver
{
    void Record(GitHubApiCall call);
}
