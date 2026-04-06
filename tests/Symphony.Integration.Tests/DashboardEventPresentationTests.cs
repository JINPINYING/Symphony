using Symphony.Host.Services;

namespace Symphony.Integration.Tests;

public sealed class DashboardEventPresentationTests
{
    [Theory]
    [InlineData("other_message", null)]
    [InlineData("other_message", "other_message")]
    [InlineData("Other_Message", "other_message")]
    public void ShouldInclude_ReturnsFalse_ForFallbackOnlyOtherMessageEntries(string eventName, string? message)
    {
        Assert.False(DashboardEventPresentation.ShouldInclude(eventName, message));
        Assert.Null(DashboardEventPresentation.GetVisibleMessage(eventName, message));
    }

    [Theory]
    [InlineData("notification", "other_message")]
    [InlineData("other_message", "Planner emitted a plain-text note.")]
    public void ShouldInclude_ReturnsTrue_ForMeaningfulEntries(string eventName, string? message)
    {
        Assert.True(DashboardEventPresentation.ShouldInclude(eventName, message));
        Assert.Equal(message, DashboardEventPresentation.GetVisibleMessage(eventName, message));
    }
}
