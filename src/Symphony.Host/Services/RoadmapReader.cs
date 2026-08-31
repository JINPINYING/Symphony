namespace Symphony.Host.Services;

public sealed record RoadmapEntry(string Status, string Milestone, string Title);

/// <summary>
/// Reads the roadmap the page shows.
///
/// This is the one part of the status page the engine cannot compute: it is
/// project narrative, not runtime state. So it comes from a file next to the
/// workflow contract, editable without a rebuild and versioned with everything
/// else - rather than being hard-coded into the page, where it would quietly go
/// stale and start lying.
///
/// Format is a markdown task list, so the file reads correctly on GitHub too:
///
///   - [x] **M4** Claude runner and real phases
///   - [>] **M8** Whatever is in flight
///   - [ ] **M9** Not started
/// </summary>
public static class RoadmapReader
{
    public const string Done = "done";
    public const string Active = "active";
    public const string Planned = "planned";

    public static IReadOnlyList<RoadmapEntry> Read(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "config", "ROADMAP.md");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return Parse(File.ReadAllLines(path));
        }
        catch (IOException)
        {
            // A missing or unreadable roadmap must never take the page down.
            return [];
        }
    }

    public static IReadOnlyList<RoadmapEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<RoadmapEntry>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("- [", StringComparison.Ordinal) || line.Length < 6)
            {
                continue;
            }

            var status = line[3] switch
            {
                'x' or 'X' => Done,
                '>' => Active,
                _ => Planned
            };

            var rest = line[5..].Trim();

            // "**M4** Title" -> milestone "M4", title "Title". The bold marker is
            // optional; without it the whole remainder is the title.
            var milestone = string.Empty;
            if (rest.StartsWith("**", StringComparison.Ordinal))
            {
                var close = rest.IndexOf("**", 2, StringComparison.Ordinal);
                if (close > 2)
                {
                    milestone = rest[2..close].Trim();
                    rest = rest[(close + 2)..].Trim();
                }
            }

            rest = rest.TrimStart('-', '—', ' ').Trim();
            if (rest.Length > 0 || milestone.Length > 0)
            {
                entries.Add(new RoadmapEntry(status, milestone, rest));
            }
        }

        return entries;
    }
}
