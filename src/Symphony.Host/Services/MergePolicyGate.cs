using System.Text.RegularExpressions;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed record MergeGateResult(bool Allowed, string Reason, bool Escalate);

// M6 / blueprint decision 8, tier 1: decides — in code, never by model judgement —
// whether an approved pull request may be merged autonomously.
//
// Every condition is a fact the ledger or the tracker states plainly. The exact
// head is the spine: the PR head must still equal the head the reviewer approved,
// so a push landing after approval can never ride in on that approval.
public static class MergePolicyGate
{
    public static MergeGateResult Evaluate(
        WorkflowMergePolicySettings policy,
        PhaseLedgerEntity ledger,
        PullRequestStatus pullRequest,
        IReadOnlyList<string> changedPaths)
    {
        if (!policy.Enabled)
        {
            return new MergeGateResult(false, "merge policy is disabled in the workflow", Escalate: false);
        }

        if (!string.Equals(ledger.LastVerdict, ReviewVerdicts.Approved, StringComparison.Ordinal))
        {
            return new MergeGateResult(false, $"ledger verdict is '{ledger.LastVerdict ?? "none"}', not APPROVED", Escalate: true);
        }

        if (string.IsNullOrWhiteSpace(ledger.LastVerdictHeadSha))
        {
            return new MergeGateResult(false, "the ledger has no recorded head for its verdict", Escalate: true);
        }

        if (!string.Equals(ledger.LastVerdictHeadSha, pullRequest.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            // Not an escalation: the branch moved, so the issue simply needs to
            // re-verify and be reviewed again at the new head.
            return new MergeGateResult(
                false,
                $"the PR head moved since approval ({Short(ledger.LastVerdictHeadSha)} -> {Short(pullRequest.HeadSha)})",
                Escalate: false);
        }

        if (!string.Equals(pullRequest.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            return new MergeGateResult(false, $"the PR is {pullRequest.State}", Escalate: false);
        }

        if (pullRequest.IsDraft)
        {
            return new MergeGateResult(false, "the PR is a draft", Escalate: false);
        }

        if (!string.Equals(pullRequest.ChecksState, "SUCCESS", StringComparison.OrdinalIgnoreCase) &&
            pullRequest.ChecksState is not null)
        {
            return new MergeGateResult(false, $"CI rollup is {pullRequest.ChecksState} at the approved head", Escalate: true);
        }

        if (pullRequest.Mergeable is not null &&
            !string.Equals(pullRequest.Mergeable, "MERGEABLE", StringComparison.OrdinalIgnoreCase))
        {
            // MERGEABLE:null means GitHub is still computing; wait rather than escalate.
            return new MergeGateResult(false, $"GitHub reports mergeable={pullRequest.Mergeable}", Escalate: false);
        }

        if (changedPaths.Count == 0)
        {
            return new MergeGateResult(false, "the changed-file list came back empty", Escalate: true);
        }

        if (changedPaths.Count > policy.MaxChangedFiles)
        {
            return new MergeGateResult(
                false,
                $"the PR changes {changedPaths.Count} files, above the merge-policy limit of {policy.MaxChangedFiles}",
                Escalate: true);
        }

        var protectedHit = changedPaths.FirstOrDefault(path => IsProtected(path, policy.ProtectedPaths));
        if (protectedHit is not null)
        {
            return new MergeGateResult(
                false,
                $"the PR touches a protected path ('{protectedHit}'); tier-2 changes need the command center",
                Escalate: true);
        }

        return new MergeGateResult(true, $"approved at exact head {Short(pullRequest.HeadSha)}, CI green, no protected paths", Escalate: false);
    }

    public static bool IsProtected(string path, IReadOnlyList<string> patterns) =>
        patterns.Any(pattern => MatchesGlob(path, pattern));

    // Minimal glob: '**' spans separators, '*' does not, '?' is one character.
    private static bool MatchesGlob(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var normalizedPath = path.Replace('\\', '/');
        var normalizedPattern = pattern.Replace('\\', '/').Trim();

        var regex = "^"
            + Regex.Escape(normalizedPattern)
                .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
                .Replace(@"\*\*", ".*", StringComparison.Ordinal)
                .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
                .Replace(@"\?", "[^/]", StringComparison.Ordinal)
            + "$";

        return Regex.IsMatch(normalizedPath, regex, RegexOptions.IgnoreCase);
    }

    private static string Short(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? "unknown" : sha.Length > 8 ? sha[..8] : sha;
}
