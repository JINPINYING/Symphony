namespace Symphony.Infrastructure.Tracker.GitHub;

public sealed class GitHubTrackerException(
    string code,
    string message,
    Exception? innerException = null,
    TimeSpan? retryAfter = null,
    int? statusCode = null)
    : Exception(message, innerException)
{
    /// <summary>
    /// The one failure that retrying cannot help. A rate limit clears on a clock,
    /// not on effort, so the caller has to wait rather than try again - the same
    /// distinction the agent quota fallback already makes.
    /// </summary>
    public const string RateLimitedCode = "github_rate_limited";

    public string Code { get; } = code;

    /// <summary>
    /// How long GitHub asked us to wait, when it said so: <c>Retry-After</c> on a
    /// secondary limit, or the distance to <c>x-ratelimit-reset</c> on a primary
    /// one. Null when GitHub gave no clock, which is the caller's cue to fall back
    /// to its own backoff rather than to invent a number and call it GitHub's.
    /// </summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>
    /// The HTTP status behind a transport refusal, when there was one. Carried so
    /// "it is not there" (404) can be told from "we could not ask", which a caller
    /// that fails closed has to distinguish and a message string cannot.
    /// </summary>
    public int? StatusCode { get; } = statusCode;

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

    /// <summary>
    /// The wait GitHub itself asked for, from anywhere in the chain, or null when
    /// it named none.
    /// </summary>
    public static TimeSpan? GetRetryAfter(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is GitHubTrackerException { IsRateLimited: true, RetryAfter: { } wait })
            {
                return wait;
            }
        }

        return null;
    }
}
