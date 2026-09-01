using System.Globalization;
using System.Text;

namespace Symphony.Host.Services;

/// <summary>
/// The state of one of the plane's own scheduled tasks.
///
/// WHY THIS EXISTS. Everything else on the Watchtower reports what the engine is
/// doing. Nothing reported whether the things that WAKE the engine were still
/// alive - so when the artifact publisher stopped running, it stopped silently
/// for 27 hours across a day in which the product shipped a stage. The page kept
/// saying "nothing needs you" because, from inside the engine, nothing did.
///
/// A missed heartbeat is therefore treated as a fault in its own right. The point
/// is not to report cron trivia; it is that a scheduler which quietly stops is
/// indistinguishable from a calm system unless something says otherwise.
/// </summary>
public sealed record WatchedTaskReport(
    string Name,
    string Path,
    string State,
    string Status,
    DateTimeOffset? LastRunUtc,
    int? LastResult,
    DateTimeOffset? NextRunUtc,
    int ExpectEveryMinutes,
    string Health,
    string Explanation)
{
    public const string HealthOk = "ok";
    public const string HealthLate = "late";
    public const string HealthFailing = "failing";
    public const string HealthDisabled = "disabled";
    public const string HealthUnknown = "unknown";
}

public static class WatchedTaskEvaluator
{
    /// <summary>
    /// How far past its expected interval a task may drift before it counts as
    /// late. Three intervals rather than one: schedulers legitimately slip when
    /// the host is busy, and a heartbeat monitor that cries wolf on ordinary
    /// jitter is one the reader learns to ignore - the exact failure this whole
    /// feature exists to prevent.
    ///
    /// It still catches the real case comfortably. The publisher that died was on
    /// a 15-minute schedule, so this would have called it late after 45 minutes
    /// instead of after 27 hours.
    /// </summary>
    public const int DefaultLatenessFactor = 3;

    public static WatchedTaskReport Evaluate(
        string name,
        string path,
        string state,
        string status,
        DateTimeOffset? lastRunUtc,
        int? lastResult,
        DateTimeOffset? nextRunUtc,
        int expectEveryMinutes,
        int? lateAfterMinutes,
        DateTimeOffset now)
    {
        var threshold = lateAfterMinutes is > 0
            ? lateAfterMinutes.Value
            : Math.Max(1, expectEveryMinutes) * DefaultLatenessFactor;

        // A disabled task is not a quiet task, it is an absent one. This is
        // reported rather than skipped because disabling something and forgetting
        // is precisely how a plane goes dark without anyone deciding it should.
        if (string.Equals(state, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return Build(WatchedTaskReport.HealthDisabled,
                "Disabled, so it will not run again until it is re-enabled.");
        }

        if (lastResult is not null && lastResult != 0)
        {
            return Build(WatchedTaskReport.HealthFailing,
                $"Its last run exited with code {lastResult}. It is still scheduled, so it will keep failing on the same schedule until the cause is fixed.");
        }

        if (lastRunUtc is null)
        {
            return Build(WatchedTaskReport.HealthUnknown,
                "It has never run, or the scheduler did not report a last run time.");
        }

        var silentFor = now - lastRunUtc.Value;
        if (silentFor.TotalMinutes > threshold)
        {
            return Build(WatchedTaskReport.HealthLate,
                $"Expected about every {Describe(expectEveryMinutes)}, but it has not run for {Humanise(silentFor)}. A task that stops being started looks exactly like a calm system from inside the engine.");
        }

        return Build(WatchedTaskReport.HealthOk,
            $"Last ran {Humanise(silentFor)} ago, on schedule.");

        WatchedTaskReport Build(string health, string explanation) => new(
            name, path, state, status, lastRunUtc, lastResult, nextRunUtc,
            expectEveryMinutes, health, explanation);
    }

    /// <summary>
    /// Parses one record of <c>schtasks /query /fo CSV /v</c>.
    ///
    /// Deliberately tolerant. schtasks localises both its column headings and its
    /// timestamps, so on a machine whose locale this does not anticipate the
    /// honest outcome is a report marked <c>unknown</c> that says so - never a
    /// crash, and never a confident <c>ok</c> derived from a field that failed to
    /// parse. Silence dressed as health is the bug this feature exists to kill.
    /// </summary>
    public static WatchedTaskReport? ParseCsvRecord(
        IReadOnlyList<string> headers,
        IReadOnlyList<string> values,
        string name,
        string path,
        int expectEveryMinutes,
        int? lateAfterMinutes,
        TimeZoneInfo localZone,
        DateTimeOffset now)
    {
        if (headers.Count == 0 || values.Count == 0)
        {
            return null;
        }

        var state = Field("Scheduled Task State") ?? "Unknown";
        var status = Field("Status") ?? "Unknown";
        var lastRun = ParseLocalTimestamp(Field("Last Run Time"), localZone);
        var nextRun = ParseLocalTimestamp(Field("Next Run Time"), localZone);
        var lastResult = ParseResult(Field("Last Result"));

        // If the state column could not be read and there is no last run time,
        // nothing useful is known and the honest answer says so. Status alone is
        // not enough to trust: it is spelled the same in several locales whose
        // other headings this parser cannot read, so a recognisable Status beside
        // an unreadable everything-else would otherwise be taken as a signal.
        if (state == "Unknown" && lastRun is null)
        {
            return new WatchedTaskReport(
                name, path, state, status, null, lastResult, nextRun,
                expectEveryMinutes, WatchedTaskReport.HealthUnknown,
                "The scheduler answered, but none of its fields could be read - most likely a locale this parser does not know. Treat this task as unmonitored.");
        }

        return Evaluate(name, path, state, status, lastRun, lastResult, nextRun,
            expectEveryMinutes, lateAfterMinutes, now);

        string? Field(string heading)
        {
            for (var i = 0; i < headers.Count && i < values.Count; i++)
            {
                if (string.Equals(headers[i], heading, StringComparison.OrdinalIgnoreCase))
                {
                    var value = values[i].Trim();
                    return value.Length == 0 || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : value;
                }
            }

            return null;
        }
    }

    internal static int? ParseResult(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// schtasks prints wall-clock time with no offset, so it is interpreted in the
    /// host's zone and converted. Storing it as an offset keeps every timestamp
    /// the engine hands out unambiguous, which matters because the page shows
    /// Eastern and the durable records are UTC.
    /// </summary>
    internal static DateTimeOffset? ParseLocalTimestamp(string? raw, TimeZoneInfo localZone)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed) &&
            !DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return null;
        }

        var unspecified = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        var offset = localZone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset);
    }

    /// <summary>
    /// Splits one CSV line as schtasks writes it: every field quoted, an embedded
    /// quote doubled. Hand-rolled because the framework has no CSV reader and the
    /// input shape is this narrow.
    /// </summary>
    public static IReadOnlyList<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static string Describe(int minutes) =>
        minutes == 1 ? "a minute" : minutes < 60 ? $"{minutes} minutes"
        : minutes % 60 == 0 ? $"{minutes / 60} hour{(minutes == 60 ? string.Empty : "s")}"
        : $"{minutes} minutes";

    private static string Humanise(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "less than a minute";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} minute{Plural((int)span.TotalMinutes)}";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hour{Plural((int)span.TotalHours)}";
        return $"{(int)span.TotalDays} day{Plural((int)span.TotalDays)}";
    }

    private static string Plural(int value) => value == 1 ? string.Empty : "s";
}
