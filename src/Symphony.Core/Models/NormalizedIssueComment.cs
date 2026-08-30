namespace Symphony.Core.Models;

// A tracker issue comment normalized for directive processing. AuthorAssociation
// carries GitHub's repository relationship (OWNER, MEMBER, COLLABORATOR, ...) so
// callers can gate command-center directives to authorized authors.
public sealed record NormalizedIssueComment(
    string Id,
    string Body,
    string? AuthorLogin,
    string? AuthorAssociation,
    DateTimeOffset? CreatedAtUtc);
