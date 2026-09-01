using Symphony.Host.Services;

namespace Symphony.Integration.Tests;

// Endpoint probes reached the owner's page and stayed there: a bare "a", the word
// "test", and two hundred consecutive x's, sitting above real work in the panel
// that answers "what is the team doing". Nothing rejected them, because the only
// rule was a length ceiling.
//
// The checks are deliberately blunt. Anything cleverer starts guessing at meaning
// and will eventually discard a real report that happened to be terse - the worse
// failure, because a dropped report looks exactly like an idle plane, which is
// the confusion this feed exists to remove.
public sealed class AgentActivityRejectionTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("test")]
    [InlineData("   ok   ")]
    public void TooShortToBeAReport(string summary)
    {
        var rejection = AgentActivity.DescribeRejection(summary.Trim());

        Assert.NotNull(rejection);
        Assert.Contains("too short", rejection);
    }

    [Fact]
    public void ASingleTokenIsNotASentence()
    {
        var rejection = AgentActivity.DescribeRejection("refactoring-the-thing-again");

        Assert.NotNull(rejection);
        Assert.Contains("single token", rejection);
    }

    // The row that disfigured the page: 200 x's, rendered as an unbroken bar
    // across the most prominent panel. It has no whitespace at all, so the
    // single-token rule catches it first - which is the right answer and a more
    // accurate description of what it is.
    [Fact]
    public void TheRowThatDisfiguredThePageIsRefused()
    {
        var rejection = AgentActivity.DescribeRejection(new string('x', 200));

        Assert.NotNull(rejection);
        Assert.Contains("single token", rejection);
    }

    // The run-length rule earns its keep on the case the token rule cannot see:
    // a genuine sentence carrying a pasted blob.
    [Fact]
    public void ASentenceCarryingAPastedBlobIsRefused()
    {
        var rejection = AgentActivity.DescribeRejection($"deploy log follows {new string('A', 120)} end");

        Assert.NotNull(rejection);
        Assert.Contains("no break", rejection);
    }

    [Fact]
    public void ALongButOtherwiseNormalSentenceSurvivesTheWordCheck()
    {
        var summary = string.Join(" ", Enumerable.Repeat("progress", 40));

        Assert.Null(AgentActivity.DescribeRejection(summary));
    }

    // The reports that matter must pass unchanged - a filter that blocks real work
    // is worse than the junk it removes.
    [Theory]
    [InlineData("Stage 2: five PRs merged tonight - synthetic caller and tool-calling.")]
    [InlineData("CyberMed dispatch is paused while the control plane is repaired.")]
    [InlineData("Writing the change for #115.")]
    public void RealReportsAreAccepted(string summary)
    {
        Assert.Null(AgentActivity.DescribeRejection(summary));
    }

    // A URL is long and unbroken, but a report that cites one is still a report -
    // the rule looks at the longest run, so the sentence around it carries it.
    [Fact]
    public void ASentenceCitingAUrlIsStillAReport()
    {
        var summary = "Merged the control-plane branches, see https://github.com/JINPINYING/Symphony/pull/22 for detail.";

        Assert.Null(AgentActivity.DescribeRejection(summary));
    }
}
