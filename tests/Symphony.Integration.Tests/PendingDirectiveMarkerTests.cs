using System.Text.Json;
using Symphony.Host.Services;

namespace Symphony.Integration.Tests;

// A pending directive is the one input to the owner-attention panel whose job is
// to REMOVE an item: the owner has already answered, so the issue is the plane's
// to move. That makes it the riskiest input on the page - every other one can
// only add noise, and a wrong answer here hides an obligation instead.
public sealed class PendingDirectiveMarkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 21, 0, 0, TimeSpan.Zero);

    private static RuntimeStateService.PendingDirectiveMarker Marker(
        string issue,
        string commentId,
        TimeSpan? age = null) =>
        new(
            "issue-" + issue,
            JsonSerializer.Serialize(new DirectiveProcessor.PendingDirectiveState(commentId, "resume")),
            Now - (age ?? TimeSpan.FromMinutes(1)));

    private static HashSet<string> Consumed(params string[] commentIds) =>
        commentIds.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void AFreshUnconsumedMarkerMeansThePlaneOwesTheIssueAnAction()
    {
        var pending = RuntimeStateService.SelectPendingDirectiveIssueIds(
            [Marker("#146", "directive-1")], Consumed(), Now);

        Assert.Equal(["issue-#146"], pending);
    }

    // Once the directive has been consumed the plane has acted on it, and the
    // marker is spent. Believing it after that would leave the issue suppressed
    // for the whole freshness window on the strength of an answer already given.
    [Fact]
    public void AConsumedDirectiveIsNoLongerPending()
    {
        var pending = RuntimeStateService.SelectPendingDirectiveIssueIds(
            [Marker("#146", "directive-1")], Consumed("directive-1"), Now);

        Assert.Empty(pending);
    }

    // The marker is rewritten for as long as the plane keeps deferring it, so a
    // stale one means the plane stopped: the comment was deleted, or it is a row
    // from a world that no longer exists. A marker that never expired would go on
    // suppressing that issue's pull request for good.
    [Fact]
    public void AMarkerThePlaneHasStoppedRefreshingIsNotBelieved()
    {
        var stale = DirectiveProcessor.PendingDirectiveWindow + TimeSpan.FromMinutes(1);

        var pending = RuntimeStateService.SelectPendingDirectiveIssueIds(
            [Marker("#146", "directive-1", age: stale)], Consumed(), Now);

        Assert.Empty(pending);
    }

    // Unreadable data must fail towards reporting the item, not towards hiding it:
    // an item shown unnecessarily is noise, one hidden wrongly is the owner never
    // hearing about a decision at all.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"CommentId\":\"\"}")]
    public void AMarkerThatCannotBeReadSuppressesNothing(string? dataJson)
    {
        var marker = new RuntimeStateService.PendingDirectiveMarker(
            "issue-#146", dataJson, Now.AddMinutes(-1));

        Assert.Empty(RuntimeStateService.SelectPendingDirectiveIssueIds([marker], Consumed(), Now));
    }

    [Fact]
    public void MarkersAreScopedToTheirOwnIssue()
    {
        var pending = RuntimeStateService.SelectPendingDirectiveIssueIds(
            [Marker("#146", "directive-1"), Marker("#124", "directive-2")],
            Consumed("directive-2"),
            Now);

        Assert.Equal(["issue-#146"], pending);
    }
}
