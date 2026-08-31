using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

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

    // ---- ADCP #4: the feed was unreadable -----------------------------------
    // Streaming deltas and events whose "message" was just their own name
    // rendered as screens of identical cards that looked like duplicate execution.

    [Theory]
    [InlineData("item/agentMessage/delta", "some streamed text")]
    [InlineData("item/commandExecution/outputDelta", "build output")]
    [InlineData("thread/tokenUsage/updated", "tokens")]
    [InlineData("account/rateLimits/updated", "limits")]
    [InlineData("item/started", "starting")]
    public void Classify_ShouldTreatStreamingAndTransportEventsAsProtocol(string eventName, string message) =>
        Assert.Equal(
            DashboardEventPresentation.EventClass.Protocol,
            DashboardEventPresentation.Classify(eventName, message));

    [Theory]
    // The echo rule: a message identical to the event name carries no information.
    // This is what actually flooded the feed, and it catches future event types
    // without needing this file edited.
    [InlineData("claude_assistant")]
    [InlineData("claude_system_thinking_tokens")]
    [InlineData("some_future_event_type")]
    public void Classify_ShouldTreatEchoedMessagesAsProtocol(string eventName) =>
        Assert.Equal(
            DashboardEventPresentation.EventClass.Protocol,
            DashboardEventPresentation.Classify(eventName, eventName));

    [Theory]
    [InlineData("phase_merged", "PR #98 merged autonomously at head cd0d0f2c.")]
    [InlineData("run_completed", "Run completed with status succeeded.")]
    [InlineData("needs_command_center", "Reviewer returned NEEDS_COMMAND_CENTER.")]
    [InlineData("error", "Something failed.")]
    // item/completed is on no allow-list, but carries the agent's real output, so
    // the echo rule is what decides it - and it passes.
    [InlineData("item/completed", "Posted the required review-verdict comment.")]
    public void Classify_ShouldTreatRealMessagesAsOperational(string eventName, string message) =>
        Assert.Equal(
            DashboardEventPresentation.EventClass.Operational,
            DashboardEventPresentation.Classify(eventName, message));

    [Theory]
    [InlineData("phase_merged", "Merged")]
    [InlineData("needs_command_center", "Needs command center")]
    [InlineData("phase_review_dispatched", "Review dispatched")]
    // Unmapped names still read as English rather than as a raw identifier.
    [InlineData("some_new_thing_happened", "Some new thing happened")]
    [InlineData("item/commandExecution/outputDelta", "Output delta")]
    public void GetLabel_ShouldProduceReadableText(string eventName, string expected) =>
        Assert.Equal(expected, DashboardEventPresentation.GetLabel(eventName));

    [Fact]
    public void Build_ShouldHideProtocolEventsByDefaultAndRestoreThemForRawView()
    {
        var entries = new[]
        {
            Event(5, "phase_merged", "PR #98 merged."),
            Event(4, "item/agentMessage/delta", "streamed"),
            Event(3, "claude_assistant", "claude_assistant"),
            Event(2, "item/agentMessage/delta", "streamed more"),
            Event(1, "issue_dispatched", "Dispatched #97 to claude."),
        };

        var operational = DashboardActivityAggregator.Build(entries, includeProtocol: false, limit: 20);
        Assert.Equal(2, operational.Count);
        Assert.All(operational, entry => Assert.False(entry.IsProtocol));
        Assert.Equal(["phase_merged", "issue_dispatched"], operational.Select(entry => entry.EventName));

        // Nothing was deleted - the raw feed still has every row.
        Assert.Equal(5, DashboardActivityAggregator.Build(entries, includeProtocol: true, limit: 20).Count);
    }

    [Fact]
    public void Build_ShouldCollapseAdjacentIdenticalEventsIntoOneRowWithACount()
    {
        // The "looks like duplicate execution" symptom, reproduced.
        var entries = Enumerable.Range(0, 9)
            .Select(index => Event(20 - index, "claude_result", "Working on it."))
            .ToArray();

        var single = Assert.Single(DashboardActivityAggregator.Build(entries, includeProtocol: false, limit: 20));

        Assert.Equal(9, single.RepeatCount);
        // The newest timestamp is kept, so the row does not appear to go stale.
        Assert.Equal(Event(20, "claude_result", "Working on it.").OccurredAtUtc, single.At);
    }

    [Fact]
    public void Build_ShouldNotCollapseAcrossDifferentIssuesOrMessages()
    {
        var entries = new[]
        {
            Event(4, "claude_result", "Working on it.", issue: "#1"),
            Event(3, "claude_result", "Working on it.", issue: "#2"),   // different issue
            Event(2, "claude_result", "Something else.", issue: "#2"),  // different message
            Event(1, "claude_result", "Something else.", issue: "#2"),
        };

        var activity = DashboardActivityAggregator.Build(entries, includeProtocol: false, limit: 20);

        Assert.Equal(3, activity.Count);
        Assert.Equal([1, 1, 2], activity.Select(entry => entry.RepeatCount));
    }

    [Fact]
    public void Build_ShouldPreserveChronologicalOrderAndRespectTheLimit()
    {
        var entries = new[]
        {
            Event(3, "phase_merged", "third"),
            Event(2, "phase_ready", "second"),
            Event(1, "issue_dispatched", "first"),
        };

        var activity = DashboardActivityAggregator.Build(entries, includeProtocol: false, limit: 2);

        Assert.Equal(2, activity.Count);
        Assert.Equal(["phase_merged", "phase_ready"], activity.Select(entry => entry.EventName));
    }

    private static EventLogEntity Event(long id, string name, string message, string issue = "#97") =>
        new()
        {
            Id = id,
            EventName = name,
            Message = message,
            Level = "Information",
            IssueIdentifier = issue,
            IssueId = "issue-1",
            OccurredAtUtc = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero).AddSeconds(id),
        };
}
