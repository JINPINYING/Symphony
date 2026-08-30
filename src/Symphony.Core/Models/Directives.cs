namespace Symphony.Core.Models;

public static class DirectiveActions
{
    public const string Resume = "resume";
    public const string Reimplement = "reimplement";
    public const string Close = "close";
    public const string Custom = "custom";
}

public enum DirectiveParseOutcome
{
    NotADirective,
    Invalid,
    Valid
}

public sealed record DirectiveParseResult(
    DirectiveParseOutcome Outcome,
    string? Action = null,
    string? Phase = null,
    string? Instructions = null,
    string? Error = null);

// Parses the command-center directive grammar (blueprint §4 M3):
//
//   symphony:directive
//   action: resume | reimplement | close | custom
//   phase: implementation | verify | review | final_review
//   instructions: <free text handed verbatim to the worker>
//
// The block may appear anywhere in a comment (including inside a code fence).
// `action` is required. `phase` is optional for resume/reimplement/close and
// required for custom. `instructions` is optional and runs to the end of the
// comment (or the closing code fence). A malformed block parses as Invalid with
// a reason — the processor reports it back rather than guessing.
public static class DirectiveParser
{
    private static readonly string[] KnownActions =
        [DirectiveActions.Resume, DirectiveActions.Reimplement, DirectiveActions.Close, DirectiveActions.Custom];

    private static readonly string[] KnownPhases =
        [RunPhaseNames.Implementation, RunPhaseNames.Verify, RunPhaseNames.Review, RunPhaseNames.FinalReview];

    public static DirectiveParseResult Parse(string? commentBody)
    {
        if (string.IsNullOrWhiteSpace(commentBody))
        {
            return new DirectiveParseResult(DirectiveParseOutcome.NotADirective);
        }

        var lines = commentBody.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var headerIndex = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.Equals(lines[index].Trim(), "symphony:directive", StringComparison.OrdinalIgnoreCase))
            {
                headerIndex = index;
                break;
            }
        }

        if (headerIndex < 0)
        {
            return new DirectiveParseResult(DirectiveParseOutcome.NotADirective);
        }

        string? action = null;
        string? phase = null;
        string? instructions = null;

        for (var index = headerIndex + 1; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                break;
            }

            if (trimmed.Length == 0)
            {
                continue;
            }

            var separator = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return new DirectiveParseResult(
                    DirectiveParseOutcome.Invalid,
                    Error: $"Line '{Truncate(trimmed)}' is not a 'key: value' pair.");
            }

            var key = trimmed[..separator].Trim().ToLowerInvariant();
            var value = trimmed[(separator + 1)..].Trim();

            switch (key)
            {
                case "action":
                    action = value.ToLowerInvariant();
                    break;
                case "phase":
                    phase = value.ToLowerInvariant();
                    break;
                case "instructions":
                    // Instructions run from here to the end of the block.
                    var remainder = new List<string> { value };
                    for (var rest = index + 1; rest < lines.Length; rest++)
                    {
                        if (lines[rest].Trim().StartsWith("```", StringComparison.Ordinal))
                        {
                            break;
                        }

                        remainder.Add(lines[rest]);
                    }

                    instructions = string.Join('\n', remainder).Trim();
                    index = lines.Length;
                    break;
                default:
                    return new DirectiveParseResult(
                        DirectiveParseOutcome.Invalid,
                        Error: $"Unknown directive key '{key}'. Allowed keys: action, phase, instructions.");
            }
        }

        if (string.IsNullOrWhiteSpace(action))
        {
            return new DirectiveParseResult(
                DirectiveParseOutcome.Invalid,
                Error: "Directive is missing the required 'action' key.");
        }

        if (!KnownActions.Contains(action, StringComparer.Ordinal))
        {
            return new DirectiveParseResult(
                DirectiveParseOutcome.Invalid,
                Error: $"Unknown action '{action}'. Allowed: resume, reimplement, close, custom.");
        }

        if (phase is not null && !KnownPhases.Contains(phase, StringComparer.Ordinal))
        {
            return new DirectiveParseResult(
                DirectiveParseOutcome.Invalid,
                Error: $"Unknown phase '{phase}'. Allowed: implementation, verify, review, final_review.");
        }

        if (string.Equals(action, DirectiveActions.Custom, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(instructions))
        {
            return new DirectiveParseResult(
                DirectiveParseOutcome.Invalid,
                Error: "Action 'custom' requires an 'instructions' value.");
        }

        return new DirectiveParseResult(
            DirectiveParseOutcome.Valid,
            Action: action,
            Phase: phase,
            Instructions: string.IsNullOrWhiteSpace(instructions) ? null : instructions);
    }

    private static string Truncate(string value) =>
        value.Length <= 60 ? value : $"{value[..60]}…";
}
