namespace Symphony.Core.Models;

public sealed record IssueStateSnapshot(
    string Id,
    string State,
    // The labels the issue carries NOW.
    //
    // The cache used to refresh State here and nothing else, and labels were
    // written only by the candidate scan - which returns issues MATCHING the
    // execution label. So an issue that lost `symphony-ready` dropped out of the
    // scan and its cached labels froze with the label still on them, forever. Six
    // issues sat in the owner's queue reading "next to be picked up" that the
    // plane could never claim.
    //
    // Empty rather than null when the tracker reports none, so "no labels" and
    // "not asked" stay distinguishable at the call site.
    IReadOnlyList<string> Labels);
