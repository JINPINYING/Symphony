using Symphony.Host.Services;

namespace Symphony.Integration.Tests;

// The panel is allowed to say "this keeps failing" only once it has watched it
// keep failing. These tests are mostly about the wrong direction of that rule:
// a single bad run promoted to a standing fault is the defect this exists to
// prevent, and the cheapest way to reintroduce it is to advance the streak on
// something other than a new run.
public sealed class WatchedTaskHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 21, 0, 0, TimeSpan.Zero);

    private static WatchedTaskReport Report(TimeSpan ago, int lastResult) =>
        WatchedTaskEvaluator.Evaluate(
            "ADCP Commander", "\\ADCP Commander", "Enabled", "Ready",
            Now - ago, lastResult, Now.AddMinutes(15), 15, null, Now);

    [Fact]
    public void OneFailedRunStaysRecovering()
    {
        var history = new WatchedTaskHistory();

        var observed = history.Observe(Report(TimeSpan.FromMinutes(1), lastResult: 2));

        Assert.Equal(WatchedTaskReport.HealthRecovering, observed.Health);
    }

    // The exact live case. The Commander was killed by a deploy at 20:55 and ran
    // clean at 21:01; between those two the dashboard polled it repeatedly. If a
    // repeated poll counted as a repeated failure, having the page open would be
    // enough to manufacture the alarm it is meant to suppress.
    [Fact]
    public void SeeingTheSameFailedRunAgainDoesNotMakeItAStreak()
    {
        var history = new WatchedTaskHistory();
        var sample = Report(TimeSpan.FromMinutes(6), lastResult: -2147023829);

        history.Observe(sample);
        history.Observe(sample);
        var third = history.Observe(sample);

        Assert.Equal(WatchedTaskReport.HealthRecovering, third.Health);
    }

    [Fact]
    public void ASecondFailedRunIsAStreakAndReadsAsFailing()
    {
        var history = new WatchedTaskHistory();

        history.Observe(Report(TimeSpan.FromMinutes(20), lastResult: 2));
        var second = history.Observe(Report(TimeSpan.FromMinutes(5), lastResult: 2));

        Assert.Equal(WatchedTaskReport.HealthFailing, second.Health);
        Assert.Contains("last 2 runs in a row", second.Explanation);
    }

    // What actually happened on 2026-09-02: one bad run, then a good one. Nothing
    // was ever owed to anyone.
    [Fact]
    public void AGoodRunClearsTheStreak()
    {
        var history = new WatchedTaskHistory();

        history.Observe(Report(TimeSpan.FromMinutes(20), lastResult: 2));
        var recovered = history.Observe(Report(TimeSpan.FromMinutes(5), lastResult: 0));
        Assert.Equal(WatchedTaskReport.HealthOk, recovered.Health);

        // And the next failure starts counting from one again, rather than
        // inheriting the streak the successful run disproved.
        var afterRecovery = history.Observe(Report(TimeSpan.FromMinutes(1), lastResult: 2));
        Assert.Equal(WatchedTaskReport.HealthRecovering, afterRecovery.Health);
    }

    // Two tasks failing once each is two blips, not one streak.
    [Fact]
    public void StreaksAreCountedPerTask()
    {
        var history = new WatchedTaskHistory();

        var first = WatchedTaskEvaluator.Evaluate(
            "ADCP Commander", "\\ADCP Commander", "Enabled", "Ready",
            Now.AddMinutes(-20), 2, Now.AddMinutes(15), 15, null, Now);
        var second = WatchedTaskEvaluator.Evaluate(
            "ADCP Event Watcher", "\\ADCP Event Watcher", "Enabled", "Ready",
            Now.AddMinutes(-5), 2, Now.AddMinutes(15), 15, null, Now);

        history.Observe(first);

        Assert.Equal(WatchedTaskReport.HealthRecovering, history.Observe(second).Health);
    }
}
