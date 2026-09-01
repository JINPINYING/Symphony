using Symphony.Core.Models;

namespace Symphony.Core.Tests;

public sealed class TrackerQuerySetTests
{
    private static TrackerQuery Query(string owner, string repo) =>
        new("https://api.github.com/graphql", "token", owner, repo, ["Open"], [], null);

    private static TrackerQuerySet Set() => new([
        Query("JINPINYING", "CyberMed-AI-Receptionist"),
        Query("JINPINYING", "Symphony")
    ]);

    [Fact]
    public void For_ShouldResolveTheRepositoryTheWorkCameFrom()
    {
        Assert.Equal("Symphony", Set().For("JINPINYING/Symphony").Repo);
        Assert.Equal("CyberMed-AI-Receptionist", Set().For("JINPINYING/CyberMed-AI-Receptionist").Repo);
    }

    [Fact]
    public void For_ShouldBeCaseInsensitive()
    {
        Assert.Equal("Symphony", Set().For("jinpinying/symphony").Repo);
    }

    // Every run, ledger and cache row written before multi-repository tracking has
    // an empty repository, and they all belong to the repository that was the only
    // one at the time. Falling back is what makes those rows keep working rather
    // than becoming unresolvable.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("JINPINYING/SomethingUntracked")]
    public void For_ShouldFallBackToThePrimaryRepository(string? repositoryKey)
    {
        Assert.Equal("CyberMed-AI-Receptionist", Set().For(repositoryKey).Repo);
    }

    [Fact]
    public void ASingleRepositorySetAnswersEveryKeyWithIt()
    {
        var single = new TrackerQuerySet([Query("JINPINYING", "CyberMed-AI-Receptionist")]);

        Assert.False(single.IsMultiRepository);
        Assert.Equal("CyberMed-AI-Receptionist", single.For("anything/at-all").Repo);
    }

    [Fact]
    public void AnEmptySetIsRefusedRatherThanFailingLater()
    {
        Assert.Throws<ArgumentException>(() => new TrackerQuerySet([]));
    }
}
