using Symphony.Core.Models;

namespace Symphony.Core.Tests;

public sealed class DirectiveParserTests
{
    [Fact]
    public void Parse_ShouldReturnNotADirectiveForOrdinaryComment()
    {
        var result = DirectiveParser.Parse("Thanks, looks good to me!");

        Assert.Equal(DirectiveParseOutcome.NotADirective, result.Outcome);
    }

    [Fact]
    public void Parse_ShouldReadActionPhaseAndMultilineInstructions()
    {
        var result = DirectiveParser.Parse(
            """
            Resolving this one:

            symphony:directive
            action: custom
            phase: verify
            instructions: rerun the verification suite
            and attach the output to the PR
            """);

        Assert.Equal(DirectiveParseOutcome.Valid, result.Outcome);
        Assert.Equal(DirectiveActions.Custom, result.Action);
        Assert.Equal(RunPhaseNames.Verify, result.Phase);
        Assert.Equal("rerun the verification suite\nand attach the output to the PR", result.Instructions);
    }

    [Fact]
    public void Parse_ShouldAcceptDirectiveInsideCodeFence()
    {
        var result = DirectiveParser.Parse(
            """
            ```
            symphony:directive
            action: resume
            instructions: pick up from the existing branch
            ```
            trailing chatter that is not part of the block
            """);

        Assert.Equal(DirectiveParseOutcome.Valid, result.Outcome);
        Assert.Equal(DirectiveActions.Resume, result.Action);
        Assert.Null(result.Phase);
        Assert.Equal("pick up from the existing branch", result.Instructions);
    }

    [Fact]
    public void Parse_ShouldRejectUnknownAction()
    {
        var result = DirectiveParser.Parse("symphony:directive\naction: banana");

        Assert.Equal(DirectiveParseOutcome.Invalid, result.Outcome);
        Assert.Contains("banana", result.Error);
    }

    [Fact]
    public void Parse_ShouldRejectMissingAction()
    {
        var result = DirectiveParser.Parse("symphony:directive\nphase: review");

        Assert.Equal(DirectiveParseOutcome.Invalid, result.Outcome);
        Assert.Contains("action", result.Error);
    }

    [Fact]
    public void Parse_ShouldRejectUnknownPhase()
    {
        var result = DirectiveParser.Parse("symphony:directive\naction: resume\nphase: shipping");

        Assert.Equal(DirectiveParseOutcome.Invalid, result.Outcome);
        Assert.Contains("shipping", result.Error);
    }

    [Fact]
    public void Parse_ShouldRejectCustomWithoutInstructions()
    {
        var result = DirectiveParser.Parse("symphony:directive\naction: custom");

        Assert.Equal(DirectiveParseOutcome.Invalid, result.Outcome);
        Assert.Contains("instructions", result.Error);
    }

    [Fact]
    public void Parse_ShouldRejectNonKeyValueLineInsideBlock()
    {
        var result = DirectiveParser.Parse("symphony:directive\njust do the thing");

        Assert.Equal(DirectiveParseOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void Parse_ShouldNotTreatAckMarkerAsDirective()
    {
        var result = DirectiveParser.Parse("<!-- symphony:directive-ack:abc123 -->\n**Directive executed** — resume.");

        Assert.Equal(DirectiveParseOutcome.NotADirective, result.Outcome);
    }
}
