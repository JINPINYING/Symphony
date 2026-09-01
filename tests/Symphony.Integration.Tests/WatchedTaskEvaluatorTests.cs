using Symphony.Host.Services;

namespace Symphony.Integration.Tests;

// These tests are weighted toward the failure this feature was built for: a task
// that quietly stops being started. The opposite error matters just as much
// though - a heartbeat monitor that fires on ordinary scheduler jitter is one
// the reader learns to scroll past, which would put us back where we started.
public sealed class WatchedTaskEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 2, 0, 0, TimeSpan.Zero);

    private static WatchedTaskReport Evaluate(
        TimeSpan? since = null,
        int? lastResult = 0,
        string state = "Enabled",
        int expectEveryMinutes = 15,
        int? lateAfterMinutes = null) =>
        WatchedTaskEvaluator.Evaluate(
            "ADCP Commander", "\\ADCP Commander", state, "Ready",
            since is null ? null : Now - since.Value,
            lastResult, Now.AddMinutes(expectEveryMinutes),
            expectEveryMinutes, lateAfterMinutes, Now);

    // ADCP#22. 267009 is 0x00041301, SCHED_S_TASK_RUNNING - the scheduler saying
    // "this task is working right now", not an exit code. Reading it as a failure
    // escalated the panel to "something is wrong and it will not clear itself"
    // while the plane was healthy and dispatching, and cleared only in the gaps
    // between runs. The healthier the Commander, the longer it held this value.
    [Fact]
    public void ARunningTaskIsNotAFailingTask()
    {
        var report = Evaluate(since: TimeSpan.FromMinutes(4), lastResult: 267009);

        Assert.Equal(WatchedTaskReport.HealthOk, report.Health);
        Assert.Contains("Currently running", report.Explanation);
    }

    // Still worth saying, though: a run that has outlasted three of its own
    // intervals is overlapping its next start.
    [Fact]
    public void ARunThatOutlastsItsOwnScheduleIsStillReported()
    {
        var report = Evaluate(since: TimeSpan.FromMinutes(90), lastResult: 267009);

        Assert.Equal(WatchedTaskReport.HealthLate, report.Health);
        Assert.Contains("overlapping its own schedule", report.Explanation);
    }

    [Theory]
    [InlineData(267010, WatchedTaskReport.HealthDisabled)]
    [InlineData(267011, WatchedTaskReport.HealthUnknown)]
    public void TheOtherSchedulerStatusCodesAreNotFailuresEither(int lastResult, string expectedHealth)
    {
        Assert.Equal(expectedHealth, Evaluate(since: TimeSpan.FromMinutes(4), lastResult: lastResult).Health);
    }

    // 267014 is SCHED_S_TASK_TERMINATED. The previous run did not finish, but the
    // task is not broken - the question that still matters is whether it keeps
    // being started - so it is judged on lateness and the termination is noted.
    [Fact]
    public void ATerminatedRunIsNotedRatherThanTreatedAsACrash()
    {
        var report = Evaluate(since: TimeSpan.FromMinutes(4), lastResult: 267014);

        Assert.Equal(WatchedTaskReport.HealthOk, report.Health);
        Assert.Contains("terminated rather than completing", report.Explanation);
    }

    [Fact]
    public void ARunWithinItsWindowIsHealthy()
    {
        Assert.Equal(WatchedTaskReport.HealthOk, Evaluate(since: TimeSpan.FromMinutes(4)).Health);
    }

    // Three intervals of slack, so a host that is briefly busy does not raise an
    // alarm. 40 minutes on a 15-minute schedule is late-ish but not yet a fault.
    [Fact]
    public void OrdinaryJitterIsNotAFault()
    {
        Assert.Equal(WatchedTaskReport.HealthOk, Evaluate(since: TimeSpan.FromMinutes(40)).Health);
    }

    // The real case, at the scale it actually happened: a 15-minute publisher that
    // had not run for 27 hours while the page reported everything as fine.
    [Fact]
    public void TwentySevenHoursOfSilenceIsCaught()
    {
        var report = Evaluate(since: TimeSpan.FromHours(27));

        Assert.Equal(WatchedTaskReport.HealthLate, report.Health);
        // Rendered as "1 day" rather than "27 hours" - the shared humaniser rolls
        // over at 24h, and matching the rest of the page matters more here than
        // the extra precision.
        Assert.Contains("1 day", report.Explanation);
    }

    [Fact]
    public void LatenessIsCaughtJustPastTheThreshold()
    {
        Assert.Equal(WatchedTaskReport.HealthLate, Evaluate(since: TimeSpan.FromMinutes(46)).Health);
    }

    [Fact]
    public void AnExplicitThresholdOverridesTheDefault()
    {
        Assert.Equal(
            WatchedTaskReport.HealthLate,
            Evaluate(since: TimeSpan.FromMinutes(10), lateAfterMinutes: 5).Health);
    }

    // Disabling something and forgetting is how a plane goes dark without anyone
    // deciding it should, so this is reported rather than skipped as "not scheduled".
    [Fact]
    public void DisabledIsReportedRatherThanIgnored()
    {
        var report = Evaluate(state: "Disabled", since: TimeSpan.FromDays(9));

        Assert.Equal(WatchedTaskReport.HealthDisabled, report.Health);
        Assert.Contains("re-enabled", report.Explanation);
    }

    // Originally written with 267011 as the example of "a non-zero exit", which is
    // the very confusion ADCP#22 is about - that value is SCHED_S_TASK_HAS_NOT_RUN,
    // a status. The invariant it was asserting is right and still holds, so it now
    // uses a code that really is an exit code.
    [Fact]
    public void ANonZeroExitOutranksBeingOnTime()
    {
        var report = Evaluate(since: TimeSpan.FromMinutes(1), lastResult: 2);

        Assert.Equal(WatchedTaskReport.HealthFailing, report.Health);
        Assert.Contains("exited with code 2", report.Explanation);
    }

    [Fact]
    public void NeverHavingRunIsUnknownNotHealthy()
    {
        Assert.Equal(WatchedTaskReport.HealthUnknown, Evaluate(since: null, lastResult: null).Health);
    }

    // schtasks quotes every field. Parsing must survive commas inside them, or a
    // task whose path contains one silently shifts every later column.
    [Fact]
    public void QuotedFieldsWithCommasSplitCorrectly()
    {
        var fields = WatchedTaskEvaluator.SplitCsvLine("\"\\Some, Task\",\"Ready\",\"N/A\"");

        Assert.Equal(["\\Some, Task", "Ready", "N/A"], fields);
    }

    [Fact]
    public void DoubledQuotesUnescape()
    {
        Assert.Equal(["a\"b"], WatchedTaskEvaluator.SplitCsvLine("\"a\"\"b\""));
    }

    [Fact]
    public void ARealSchtasksRecordIsRead()
    {
        var headers = WatchedTaskEvaluator.SplitCsvLine(
            "\"TaskName\",\"Next Run Time\",\"Status\",\"Last Run Time\",\"Last Result\",\"Scheduled Task State\"");
        var values = WatchedTaskEvaluator.SplitCsvLine(
            "\"\\ADCP Event Watcher\",\"9/1/2026 2:01:00 AM\",\"Ready\",\"9/1/2026 1:59:01 AM\",\"0\",\"Enabled\"");

        var report = WatchedTaskEvaluator.ParseCsvRecord(
            headers, values, "ADCP Event Watcher", "\\ADCP Event Watcher",
            expectEveryMinutes: 1, lateAfterMinutes: null,
            TimeZoneInfo.Utc, Now);

        Assert.NotNull(report);
        Assert.Equal("Enabled", report!.State);
        Assert.Equal(0, report.LastResult);
        Assert.Equal(WatchedTaskReport.HealthOk, report.Health);
    }

    // "N/A" is what schtasks prints for a task that has never run or has no next
    // run. Treating it as a value would produce a confident wrong timestamp.
    [Fact]
    public void NotApplicableIsAbsenceNotAValue()
    {
        var headers = WatchedTaskEvaluator.SplitCsvLine(
            "\"TaskName\",\"Next Run Time\",\"Status\",\"Last Run Time\",\"Last Result\",\"Scheduled Task State\"");
        var values = WatchedTaskEvaluator.SplitCsvLine(
            "\"\\New Task\",\"N/A\",\"Ready\",\"N/A\",\"N/A\",\"Enabled\"");

        var report = WatchedTaskEvaluator.ParseCsvRecord(
            headers, values, "New Task", "\\New Task", 15, null, TimeZoneInfo.Utc, Now);

        Assert.NotNull(report);
        Assert.Null(report!.LastRunUtc);
        Assert.Null(report.NextRunUtc);
        Assert.Equal(WatchedTaskReport.HealthUnknown, report.Health);
    }

    // A locale whose column headings this parser does not know must produce an
    // honest "unmonitored", never a default-derived "ok". Silence dressed as
    // health is the exact bug being fixed.
    [Fact]
    public void UnreadableFieldsReportUnmonitoredRatherThanHealthy()
    {
        var headers = WatchedTaskEvaluator.SplitCsvLine("\"Aufgabenname\",\"Status\"");
        var values = WatchedTaskEvaluator.SplitCsvLine("\"\\ADCP Commander\",\"Bereit\"");

        var report = WatchedTaskEvaluator.ParseCsvRecord(
            headers, values, "ADCP Commander", "\\ADCP Commander", 15, null, TimeZoneInfo.Utc, Now);

        Assert.NotNull(report);
        Assert.Equal(WatchedTaskReport.HealthUnknown, report!.Health);
        Assert.Contains("unmonitored", report.Explanation);
    }
}
