
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;
using Symphony.Host.Services;
using Symphony.Infrastructure.Tracker.GitHub;

namespace Symphony.Integration.Tests;

/// <summary>
/// The measurement half of the budget work. The model in
/// <see cref="GitHubTrackerGraphQlCostTests"/> catches an unaffordable query
/// before it ships; this catches an assumption that was wrong in production,
/// which is what actually happened on 2026-09-05.
/// </summary>
public sealed class GitHubRateLimitBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 5, 11, 30, 0, TimeSpan.Zero);

    // The reset GitHub actually named on 2026-09-05: 1788610784.
    private static readonly DateTimeOffset Reset = new(2026, 9, 5, 12, 19, 44, TimeSpan.Zero);

    // A burn rate is only meaningful across time, so the clock has to move.
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private static GitHubRateLimitReading Reading(
        int used,
        DateTimeOffset observedAt,
        string resource = GitHubRateLimitReading.GraphQlResource,
        DateTimeOffset? reset = null) =>
        new(resource, 5000, used, Math.Max(0, 5000 - used), reset ?? Reset, observedAt);

    [Fact]
    public void NothingObservedReportsNothing()
    {
        var budget = new GitHubRateLimitBudget(new MovableClock(Start));

        // An unobserved budget is unknown. Reporting unknown as healthy is the
        // failure this whole type exists to end.
        Assert.Empty(budget.Current);
        Assert.Empty(budget.NeedingAttention);
    }

    [Fact]
    public void OneReadingReportsTheAllowanceButNoRate()
    {
        var clock = new MovableClock(Start);
        var budget = new GitHubRateLimitBudget(clock);

        budget.Record(Reading(used: 4200, observedAt: Start));

        var snapshot = Assert.Single(budget.Current);
        Assert.Equal(GitHubRateLimitReading.GraphQlResource, snapshot.Resource);
        Assert.Equal(4200, snapshot.Used);
        Assert.Equal(800, snapshot.Remaining);
        Assert.Equal(84.0, snapshot.UsedPercent);

        // Null, not zero. A rate of zero would say "this will never run out" about
        // a budget that is nearly gone.
        Assert.Null(snapshot.BurnPointsPerHour);
        Assert.Null(snapshot.ProjectedExhaustionUtc);
    }

    [Fact]
    public void BurnRateIsMeasuredAcrossReadingsInTheSameWindow()
    {
        var clock = new MovableClock(Start);
        var budget = new GitHubRateLimitBudget(clock);

        budget.Record(Reading(used: 1000, observedAt: Start));
        clock.Advance(TimeSpan.FromMinutes(30));
        budget.Record(Reading(used: 2500, observedAt: Start.AddMinutes(30)));

        var snapshot = Assert.Single(budget.Current);

        // 1,500 points in half an hour.
        Assert.Equal(3000d, snapshot.BurnPointsPerHour!.Value, 3);
    }

    [Fact]
    public void TwoReadingsTooCloseTogetherReportNoRate()
    {
        var clock = new MovableClock(Start);
        var budget = new GitHubRateLimitBudget(clock);

        budget.Record(Reading(used: 1000, observedAt: Start));
        budget.Record(Reading(used: 1001, observedAt: Start.AddSeconds(2)));

        // One point over two seconds is 1,800 an hour, which is arithmetic rather
        // than evidence. Saying nothing beats raising an alarm about a rounding.
        Assert.Null(Assert.Single(budget.Current).BurnPointsPerHour);
    }

    [Fact]
    public void AWindowThatResetsStartsTheMeasurementAgain()
    {
        var clock = new MovableClock(Start);
        var budget = new GitHubRateLimitBudget(clock);

        budget.Record(Reading(used: 4900, observedAt: Start));
        clock.Advance(TimeSpan.FromMinutes(50));

        var nextWindow = Reset.AddHours(1);
        budget.Record(Reading(used: 40, observedAt: Start.AddMinutes(50), reset: nextWindow));

        var snapshot = Assert.Single(budget.Current);
        Assert.Equal(40, snapshot.Used);
        Assert.Equal(nextWindow, snapshot.ResetAtUtc);

        // Measuring 40 against the previous window's 4,900 would report the budget
        // refilling at minus five thousand points an hour.
        Assert.Null(snapshot.BurnPointsPerHour);
    }

    [Fact]
    public void AStaleReadingArrivingLateDoesNotWalkUsageBackwards()
    {
        var clock = new MovableClock(Start);
        var budget = new GitHubRateLimitBudget(clock);

        budget.Record(Reading(used: 1000, observedAt: Start));
        clock.Advance(TimeSpan.FromMinutes(10));
        budget.Record(Reading(used: 1800, observedAt: Start.AddMinutes(10)));

        // Concurrent requests finish in whatever order the network gives them.
        budget.Record(Reading(used: 1200, observedAt: Start.AddMinutes(5)));

        Assert.Equal(1800, Assert.Single(budget.Current).Used);
    }

    [Fact]
    public void AtEightyPercentTheBudgetNeedsAttention()
    {
        var clock = new MovableClock(Start);
        var budget = new GitHubRateLimitBudget(clock);

        budget.Record(Reading(used: 3900, observedAt: Start));
        Assert.Empty(budget.NeedingAttention);

        clock.Advance(TimeSpan.FromMinutes(5));
        budget.Record(Reading(used: 4000, observedAt: Start.AddMinutes(5)));

        var flagged = Assert.Single(budget.NeedingAttention);
        Assert.Equal(80.0, flagged.UsedPercent);
    }

    [Fact]
    public void ExhaustionIsProjectedOnlyWhenItArrivesBeforeTheWindowResets()
    {
        var clock = new MovableClock(Start);
        var budget = new GitHubRateLimitBudget(clock);

        // 3,000 points an hour with 500 left: ten minutes, and the window does not
        // reset for another twenty. It really does run out first.
        budget.Record(Reading(used: 3000, observedAt: Start));
        clock.Advance(TimeSpan.FromMinutes(30));
        budget.Record(Reading(used: 4500, observedAt: Start.AddMinutes(30)));

        var burning = Assert.Single(budget.Current);
        Assert.NotNull(burning.ProjectedExhaustionUtc);
        Assert.True(burning.ProjectedExhaustionUtc < Reset);

        // A trickle that outlasts the window is not an exhaustion, and saying it
        // is trains the reader to ignore the next one.
        var slow = new GitHubRateLimitBudget(clock);
        slow.Record(Reading(used: 100, observedAt: Start));
        slow.Record(Reading(used: 110, observedAt: Start.AddMinutes(30)));

        Assert.Null(Assert.Single(slow.Current).ProjectedExhaustionUtc);
    }

    /// <summary>
    /// The claim "recorded from every response" rests entirely on the tracker
    /// adapter being handed the singleton the status page reads. Asserted end to
    /// end through the real registrations, because a wiring nobody checks is how a
    /// measurement quietly becomes a no-op.
    /// </summary>
    [Fact]
    public async Task TheRegisteredTrackerClientReportsIntoTheRegisteredBudget()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new MovableClock(Start));
        services.AddSingleton<GitHubRateLimitBudget>();
        services.AddSingleton<IGitHubRateLimitObserver>(
            provider => provider.GetRequiredService<GitHubRateLimitBudget>());
        services.AddSymphonyGitHubTrackerClient();
        services.AddHttpClient<GitHubTrackerClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new BudgetHeaderHandler());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ITrackerClient>().FetchCandidateIssuesAsync(new TrackerQuery(
            Endpoint: "https://api.github.com/graphql",
            ApiKey: "token",
            Owner: "released",
            Repo: "symphony",
            ActiveStates: ["Open"],
            Labels: [],
            Milestone: null));

        var snapshot = Assert.Single(provider.GetRequiredService<GitHubRateLimitBudget>().Current);
        Assert.Equal("core", snapshot.Resource);
        Assert.Equal(4321, snapshot.Used);
    }

    private sealed class BudgetHeaderHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("x-ratelimit-resource", "core");
            response.Headers.TryAddWithoutValidation("x-ratelimit-limit", "5000");
            response.Headers.TryAddWithoutValidation("x-ratelimit-used", "4321");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "679");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void TheTwoBudgetsAreKeptApart()
    {
        var clock = new MovableClock(Start);
        var budget = new GitHubRateLimitBudget(clock);

        budget.Record(Reading(used: 4500, observedAt: Start));
        budget.Record(Reading(used: 12, observedAt: Start, resource: GitHubRateLimitReading.RestResource));

        // Exhausting either one blinds different things, so a reading that does not
        // say which it describes is not a reading.
        Assert.Equal(2, budget.Current.Count);
        Assert.Equal(GitHubRateLimitReading.GraphQlResource, Assert.Single(budget.NeedingAttention).Resource);
    }
}
