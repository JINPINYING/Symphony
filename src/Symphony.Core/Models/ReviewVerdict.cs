namespace Symphony.Core.Models;

public static class ReviewVerdicts
{
    public const string Approved = "APPROVED";
    public const string ChangesRequired = "CHANGES_REQUIRED";
    public const string NeedsCommandCenter = "NEEDS_COMMAND_CENTER";
}

// M4 review result contract: the reviewer's output must contain exactly one
// line of the form `VERDICT: <APPROVED|CHANGES_REQUIRED|NEEDS_COMMAND_CENTER>`.
// Code parses it — the orchestrator never infers a verdict from prose. Zero or
// multiple verdict lines, or an unknown token, parse as null and the caller
// escalates instead of guessing.
public static class ReviewVerdictParser
{
    public static string? Parse(string? reviewOutput)
    {
        if (string.IsNullOrWhiteSpace(reviewOutput))
        {
            return null;
        }

        string? found = null;
        foreach (var rawLine in reviewOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("VERDICT:", StringComparison.Ordinal))
            {
                continue;
            }

            var token = line["VERDICT:".Length..].Trim();
            if (token is not (ReviewVerdicts.Approved or ReviewVerdicts.ChangesRequired or ReviewVerdicts.NeedsCommandCenter))
            {
                return null;
            }

            if (found is not null && !string.Equals(found, token, StringComparison.Ordinal))
            {
                // Conflicting verdict lines: refuse to pick one.
                return null;
            }

            found = token;
        }

        return found;
    }
}
