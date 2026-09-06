namespace Symphony.Infrastructure.Tracker.GitHub;

/// <summary>
/// What GitHub last answered for a URL, kept so the next read of it can be
/// conditional.
///
/// WHY THIS EXISTS. REST is charged one point per request whatever comes back,
/// and the plane asks the same questions over and over: is this pull request
/// still open, has this issue a new comment, has CI reported yet. GitHub does not
/// charge a request it answers <c>304 Not Modified</c>, so a read that carries
/// <c>If-None-Match</c> and gets a 304 is a FREE fresh read - not a stale cache.
/// That distinction is the whole reason this is safe: a 304 is GitHub asserting
/// that the representation is unchanged, so serving the stored body is serving
/// what GitHub would have sent.
///
/// The stored next-page URL is stored for the same reason. A <c>Link</c> header
/// is part of the representation the ETag identifies, and GitHub is not required
/// to repeat it on a 304, so a listing page revalidated to 304 continues from the
/// link it carried when it was fetched. Every page is still requested every time:
/// this makes a page walk cheaper, never shorter.
/// </summary>
public sealed class GitHubConditionalRequestCache
{
    /// <summary>One stored response.</summary>
    /// <param name="ETag">The validator to send back as <c>If-None-Match</c>.</param>
    /// <param name="Body">The body GitHub sent, replayed verbatim on a 304.</param>
    /// <param name="NextPageUrl">The <c>Link: rel="next"</c> URL that came with it, if any.</param>
    public sealed record Entry(string ETag, string Body, string? NextPageUrl);

    /// <summary>
    /// How many responses are kept. The plane's repeated reads are a small set -
    /// a handful of repositories, the open ledgers, the escalated issues - so this
    /// is generous rather than tight.
    /// </summary>
    private const int MaxEntries = 256;

    /// <summary>
    /// And the ceiling on what they may weigh. A comments listing runs to tens of
    /// kilobytes and an unbounded cache in a service that runs for weeks is a leak
    /// with a good excuse. Least-recently-used goes first.
    /// </summary>
    private const long MaxBytes = 8L * 1024 * 1024;

    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<Stored>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Stored> recency = new();
    private long bytes;

    private sealed record Stored(string Key, Entry Entry, long Size);

    /// <summary>What is stored, and how heavy it is. Exposed for the bound's test.</summary>
    public (int Entries, long Bytes) Usage
    {
        get
        {
            lock (gate)
            {
                return (entries.Count, bytes);
            }
        }
    }

    public Entry? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (gate)
        {
            if (!entries.TryGetValue(key, out var node))
            {
                return null;
            }

            recency.Remove(node);
            recency.AddFirst(node);
            return node.Value.Entry;
        }
    }

    public void Store(string key, Entry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(entry);

        // A body that alone would blow the ceiling is not stored at all rather
        // than evicting everything else to hold it.
        var size = (long)entry.Body.Length * sizeof(char);
        if (size > MaxBytes)
        {
            lock (gate)
            {
                RemoveKey(key);
            }

            return;
        }

        lock (gate)
        {
            RemoveKey(key);

            var node = recency.AddFirst(new Stored(key, entry, size));
            entries[key] = node;
            bytes += size;

            while (entries.Count > MaxEntries || bytes > MaxBytes)
            {
                var oldest = recency.Last;
                if (oldest is null)
                {
                    break;
                }

                RemoveKey(oldest.Value.Key);
            }
        }
    }

    private void RemoveKey(string key)
    {
        if (!entries.TryGetValue(key, out var existing))
        {
            return;
        }

        recency.Remove(existing);
        entries.Remove(key);
        bytes -= existing.Value.Size;
    }
}
