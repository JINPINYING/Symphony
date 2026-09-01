namespace Symphony.Core.Models;

/// <summary>
/// The tracker queries for every repository the plane watches, and the ability to
/// pick the right one for a given piece of work.
///
/// This exists because a <see cref="TrackerQuery"/> names exactly one repository,
/// and almost everything downstream of dispatch has to talk to GitHub about an
/// issue AFTER the fetch that found it: publishing an escalation, reading a
/// directive, checking a pull request's head, merging it. Each of those needs to
/// know which repository the work came from, and an issue number cannot say -
/// "#115" is unique only within a repository.
///
/// <see cref="For"/> falls back to the primary query rather than failing when a
/// repository key is missing or unrecognised. That is deliberate: every row
/// written before multi-repository tracking existed has an empty repository, and
/// they all belong to the repository that was the only one at the time.
/// </summary>
public sealed class TrackerQuerySet
{
    public TrackerQuerySet(IReadOnlyList<TrackerQuery> queries)
    {
        if (queries.Count == 0)
        {
            throw new ArgumentException("A tracker query set needs at least one repository.", nameof(queries));
        }

        All = queries;
    }

    public IReadOnlyList<TrackerQuery> All { get; }

    public TrackerQuery Primary => All[0];

    public bool IsMultiRepository => All.Count > 1;

    public TrackerQuery For(string? repositoryKey)
    {
        if (string.IsNullOrWhiteSpace(repositoryKey))
        {
            return Primary;
        }

        foreach (var query in All)
        {
            if (KeyOf(query).Equals(repositoryKey, StringComparison.OrdinalIgnoreCase))
            {
                return query;
            }
        }

        return Primary;
    }

    public static string KeyOf(TrackerQuery query) => $"{query.Owner}/{query.Repo}";
}
