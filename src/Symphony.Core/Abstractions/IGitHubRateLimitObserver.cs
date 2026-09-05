using Symphony.Core.Models;

namespace Symphony.Core.Abstractions;

/// <summary>
/// Where a tracker adapter reports the budget headers it saw.
///
/// The adapter reads them because it is the only thing holding the response; it
/// does not decide what they mean. Implementations keep the readings and answer
/// "how much is left, and how fast is it going" for the status page and the
/// attention panel.
///
/// Implementations must not throw: a budget reading is telemetry taken on the way
/// past a real read, and losing the telemetry must never lose the read.
/// </summary>
public interface IGitHubRateLimitObserver
{
    void Record(GitHubRateLimitReading reading);
}
