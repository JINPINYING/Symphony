using System.Globalization;
using System.Text.RegularExpressions;

namespace Symphony.Core.Models;

// The clock a quota refusal names for itself (ADCP#29).
//
// A vendor that is out of quota always says when it will not be. The Codex CLI
// prints "try again at 7:24 PM", the Claude CLI prints "resets 1:40am", and the
// app-server carries a `rate_limits` block with a reset expressed in seconds or
// as an absolute timestamp. Symphony already treats GitHub's own clock as
// authoritative over its guessed backoff; a runner refusal deserves the same
// treatment, because retrying before the reset cannot succeed and escalating it
// asks a person for a decision that does not exist.
//
// This is deliberately conservative. Anything it cannot read returns null and
// the caller falls back to a bounded default hold: a wrong-but-late retry costs
// one wait, while a wrong-but-early one walks straight back into the limit.
public static partial class AgentQuotaReset
{
    /// <summary>
    /// How long to hold when the refusal named no clock at all.
    ///
    /// Half an hour: long enough not to hammer a limit that resets hourly, short
    /// enough that a five-minute window is not turned into an hour of idleness.
    /// </summary>
    public static readonly TimeSpan DefaultHold = TimeSpan.FromMinutes(30);

    /// <summary>The reset the text names, or <see cref="DefaultHold"/> from now.</summary>
    public static DateTimeOffset Resolve(string? text, DateTimeOffset nowUtc) =>
        TryParse(text, nowUtc) ?? nowUtc + DefaultHold;

    /// <summary>The reset the text names, or null when it names none this can read.</summary>
    public static DateTimeOffset? TryParse(string? text, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Most exact first: an app-server rate_limits block states the reset as a
        // number, either seconds from now or a unix instant. A number is not open
        // to the timezone ambiguity the printed forms below have, so it wins.
        return TryParseStructuredReset(text, nowUtc)
               ?? TryParseAbsoluteTimestamp(text)
               ?? TryParseDuration(text, nowUtc)
               ?? TryParseWallClock(text, nowUtc);
    }

    private static DateTimeOffset? TryParseStructuredReset(string text, DateTimeOffset nowUtc)
    {
        var secondsMatch = ResetSecondsRegex().Match(text);
        if (secondsMatch.Success &&
            long.TryParse(secondsMatch.Groups["seconds"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            // The same field name carries both conventions in the wild: a small
            // number is "seconds from now", a large one is a unix instant. The
            // split is unambiguous because no real reset window is a decade long
            // and no real unix instant is under ten million.
            return seconds >= 10_000_000
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : nowUtc.AddSeconds(Math.Max(seconds, 0));
        }

        return null;
    }

    private static DateTimeOffset? TryParseAbsoluteTimestamp(string text)
    {
        var match = IsoTimestampRegex().Match(text);
        if (match.Success &&
            DateTimeOffset.TryParse(
                match.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTimeOffset? TryParseDuration(string text, DateTimeOffset nowUtc)
    {
        var match = DurationRegex().Match(text);
        if (!match.Success ||
            !int.TryParse(match.Groups["amount"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        return unit switch
        {
            "s" or "sec" or "secs" or "second" or "seconds" => nowUtc.AddSeconds(amount),
            "m" or "min" or "mins" or "minute" or "minutes" => nowUtc.AddMinutes(amount),
            "h" or "hr" or "hrs" or "hour" or "hours" => nowUtc.AddHours(amount),
            _ => null
        };
    }

    /// <summary>
    /// "try again at 7:24 PM" / "resets 1:40am".
    /// </summary>
    /// <remarks>
    /// The printed forms carry no date and no zone. They are read as HOST LOCAL
    /// time, which is sound here and only here: the CLI that printed them ran as
    /// a child of this process, on this machine, against this machine's clock.
    /// A time already past is read as tomorrow's, because a reset is by
    /// definition still ahead.
    /// </remarks>
    private static DateTimeOffset? TryParseWallClock(string text, DateTimeOffset nowUtc)
    {
        var match = WallClockRegex().Match(text);
        if (!match.Success ||
            !int.TryParse(match.Groups["hour"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(match.Groups["minute"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
        {
            return null;
        }

        var meridiem = match.Groups["meridiem"].Value.ToLowerInvariant();
        if (meridiem.StartsWith('p') && hour < 12)
        {
            hour += 12;
        }
        else if (meridiem.StartsWith('a') && hour == 12)
        {
            hour = 0;
        }

        if (hour > 23 || minute > 59)
        {
            return null;
        }

        var localNow = nowUtc.ToLocalTime();
        var candidate = new DateTimeOffset(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            hour,
            minute,
            0,
            localNow.Offset);

        if (candidate <= localNow)
        {
            candidate = candidate.AddDays(1);
        }

        return candidate.ToUniversalTime();
    }

    // Deliberately loose about the field NAME (`reset`, `resets_at`,
    // `resets_in_seconds`, `reset_after`) and strict about its shape: a key,
    // a colon or equals, a number. Prose spellings - "resets 1:40am", "reset at
    // 7:24 PM" - have no separator there and so cannot reach this by accident.
    [GeneratedRegex(
        @"""?reset[a-z_]*""?\s*[:=]\s*""?(?<seconds>\d{1,12})""?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ResetSecondsRegex();

    [GeneratedRegex(
        @"\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?(?:[Zz]|[+\-]\d{2}:?\d{2})?",
        RegexOptions.IgnoreCase)]
    private static partial Regex IsoTimestampRegex();

    [GeneratedRegex(
        @"\b(?:in|after|for)\s+(?<amount>\d{1,5})\s*(?<unit>seconds|second|secs|sec|s|minutes|minute|mins|min|m|hours|hour|hrs|hr|h)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(
        @"\b(?:at|resets?|until)\s+(?<hour>\d{1,2}):(?<minute>\d{2})\s*(?<meridiem>[ap]\.?m\.?)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex WallClockRegex();
}
