using Symphony.Host.Services;

namespace Symphony.Integration.Tests;

// The roadmap is the one part of the status page the engine cannot compute, so it
// is read from a file. That file is edited by hand, which means the parser has to
// survive imperfect input rather than assume it.
public sealed class RoadmapReaderTests
{
    [Fact]
    public void ParsesTheThreeStates()
    {
        var entries = RoadmapReader.Parse([
            "- [x] **M4** Claude runner and real phases",
            "- [>] **M8** In flight right now",
            "- [ ] **M9** Not started yet"
        ]);

        Assert.Equal(3, entries.Count);
        Assert.Equal(RoadmapReader.Done, entries[0].Status);
        Assert.Equal(RoadmapReader.Active, entries[1].Status);
        Assert.Equal(RoadmapReader.Planned, entries[2].Status);
        Assert.Equal("M4", entries[0].Milestone);
        Assert.Equal("Claude runner and real phases", entries[0].Title);
    }

    [Fact]
    public void IgnoresProseAroundTheList()
    {
        // The file is also read by humans on GitHub, so it has headings and
        // instructions above the list. None of that is a roadmap entry.
        var entries = RoadmapReader.Parse([
            "# Roadmap",
            "",
            "Format is a markdown task list.",
            "",
            "- [x] **M1** Escalations reach GitHub",
            "",
            "---"
        ]);

        var only = Assert.Single(entries);
        Assert.Equal("M1", only.Milestone);
    }

    [Fact]
    public void AcceptsAnEntryWithNoMilestoneMarker()
    {
        var entries = RoadmapReader.Parse(["- [x] Event log retention"]);

        var only = Assert.Single(entries);
        Assert.Equal(string.Empty, only.Milestone);
        Assert.Equal("Event log retention", only.Title);
    }

    [Fact]
    public void StripsTheDashSeparatorAuthorsNaturallyWrite()
    {
        var entries = RoadmapReader.Parse(["- [x] **M0** - Hardened state machine"]);

        Assert.Equal("Hardened state machine", Assert.Single(entries).Title);
    }

    [Fact]
    public void UppercaseXCountsAsDone()
    {
        Assert.Equal(RoadmapReader.Done, Assert.Single(RoadmapReader.Parse(["- [X] **M1** Done"])).Status);
    }

    [Fact]
    public void ReturnsEmptyRatherThanThrowingWhenTheFileIsAbsent()
    {
        // A missing roadmap must not take the status page down with it.
        Assert.Empty(RoadmapReader.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }
}
