namespace Symphony.Core.Models;

/// <summary>
/// Pairs the tracker ids a caller is asking about with the identifiers ("#115")
/// it already holds for them.
///
/// WHY. A tracker id is a GitHub GraphQL node id and nothing else can address an
/// issue over REST; the identifier can. Every caller of an id-keyed read has both
/// to hand - runs, retry rows, ledger rows and cache rows all store the pair - so
/// this exists to stop each of them writing the same three lines of dictionary
/// construction, and to make a caller that supplies neither obvious.
/// </summary>
public static class IssueIdentifierMap
{
    public static IReadOnlyDictionary<string, string> For(string issueId, string? issueIdentifier)
    {
        return string.IsNullOrWhiteSpace(issueId) || string.IsNullOrWhiteSpace(issueIdentifier)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal) { [issueId] = issueIdentifier };
    }

    public static IReadOnlyDictionary<string, string> From<T>(
        IEnumerable<T> items,
        Func<T, string> issueIdSelector,
        Func<T, string?> issueIdentifierSelector)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var issueId = issueIdSelector(item);
            var identifier = issueIdentifierSelector(item);
            if (!string.IsNullOrWhiteSpace(issueId) && !string.IsNullOrWhiteSpace(identifier))
            {
                map[issueId] = identifier;
            }
        }

        return map;
    }
}
