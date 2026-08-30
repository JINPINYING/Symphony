using Symphony.Core.Models;

namespace Symphony.Core.Tests;

public sealed class ReviewVerdictParserTests
{
    [Theory]
    [InlineData("VERDICT: APPROVED", "APPROVED")]
    [InlineData("findings above\nVERDICT: CHANGES_REQUIRED", "CHANGES_REQUIRED")]
    [InlineData("  VERDICT: NEEDS_COMMAND_CENTER  ", "NEEDS_COMMAND_CENTER")]
    public void Parse_ShouldReadSingleVerdictLine(string output, string expected)
    {
        Assert.Equal(expected, ReviewVerdictParser.Parse(output));
    }

    [Fact]
    public void Parse_ShouldReturnNullWhenNoVerdictLineExists()
    {
        Assert.Null(ReviewVerdictParser.Parse("looks good to me"));
        Assert.Null(ReviewVerdictParser.Parse(null));
        Assert.Null(ReviewVerdictParser.Parse(""));
    }

    [Fact]
    public void Parse_ShouldReturnNullOnUnknownToken()
    {
        Assert.Null(ReviewVerdictParser.Parse("VERDICT: LGTM"));
    }

    [Fact]
    public void Parse_ShouldReturnNullOnConflictingVerdictLines()
    {
        Assert.Null(ReviewVerdictParser.Parse("VERDICT: APPROVED\nVERDICT: CHANGES_REQUIRED"));
    }

    [Fact]
    public void Parse_ShouldAcceptRepeatedIdenticalVerdictLines()
    {
        Assert.Equal("APPROVED", ReviewVerdictParser.Parse("VERDICT: APPROVED\n...\nVERDICT: APPROVED"));
    }
}
