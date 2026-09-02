namespace Symphony.Infrastructure.Tracker.GitHub;

public sealed class GitHubTrackerException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>
    /// The one failure that retrying cannot help. A rate limit clears on a clock,
    /// not on effort, so the caller has to wait rather than try again - the same
    /// distinction the agent quota fallback already makes.
    /// </summary>
    public const string RateLimitedCode = "github_rate_limited";

    public string Code { get; } = code;

    public bool IsRateLimited => string.Equals(Code, RateLimitedCode, StringComparison.Ordinal);

    /// <summary>True when this exception, or anything it wraps, is a rate limit.</summary>
    public static bool IsRateLimit(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is GitHubTrackerException tracker && tracker.IsRateLimited)
            {
                return true;
            }
        }

        return false;
    }
}
