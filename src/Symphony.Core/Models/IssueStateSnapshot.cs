namespace Symphony.Core.Models;

public sealed record IssueStateSnapshot(
    string Id,
    string State,
    IReadOnlyList<string> Labels)
{
    public IssueStateSnapshot(string id, string state)
        : this(id, state, [])
    {
    }
}
