using System.Net;
using System.Net.Sockets;
using Symphony.Host.Services;

namespace Symphony.Integration.Tests;

// The bug behind these tests: twelve candidate scans failed and every one was
// logged as "GitHubTrackerException." - the type name alone. The real cause,
// intermittent DNS, was only recoverable by grepping 64 MB of rotated service
// log. What is recorded here has to be enough to act on without doing that.
public sealed class TrackerReachabilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 3, 0, 0, TimeSpan.Zero);

    // The suite's existing FixedTimeProvider cannot move, and a blind window is
    // only meaningful across time, so this one advances.
    private sealed class MovableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private static (TrackerReachability Reachability, MovableTimeProvider Clock) Create()
    {
        var clock = new MovableTimeProvider(Start);
        return (new TrackerReachability(clock), clock);
    }

    [Fact]
    public void AHealthyTrackerHasNoStreakAndNoBlindWindow()
    {
        var (reachability, _) = Create();
        reachability.RecordSuccess();

        var snapshot = reachability.Current;
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Null(snapshot.UnreachableSinceUtc);
        Assert.Equal(Start, snapshot.LastSuccessUtc);
    }

    // The age reported must be how long the engine has been blind, not how long
    // since it last retried - otherwise a failure every 15 seconds looks forever
    // fresh and never crosses any threshold.
    [Fact]
    public void TheBlindWindowIsMeasuredFromTheFirstFailure()
    {
        var (reachability, clock) = Create();
        reachability.RecordFailure("dns", transient: true);
        clock.Advance(TimeSpan.FromMinutes(9));
        reachability.RecordFailure("dns", transient: true);

        var snapshot = reachability.Current;
        Assert.Equal(2, snapshot.ConsecutiveFailures);
        Assert.Equal(Start, snapshot.UnreachableSinceUtc);
    }

    [Fact]
    public void OneSuccessClearsTheStreak()
    {
        var (reachability, clock) = Create();
        reachability.RecordFailure("dns", transient: true);
        reachability.RecordFailure("dns", transient: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        reachability.RecordSuccess();

        var snapshot = reachability.Current;
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Null(snapshot.UnreachableSinceUtc);
        Assert.Null(snapshot.LastFailureReason);
    }

    // The exact shape of the real failure: a wrapper that says nothing useful
    // around an inner exception that says everything.
    [Fact]
    public void TheCauseKeptIsTheInnermostOne()
    {
        var inner = new SocketException((int)SocketError.HostNotFound);
        var http = new HttpRequestException("No such host is known. (api.github.com:443)", inner);
        var wrapper = new InvalidOperationException("GitHub GraphQL request failed.", http);

        var described = TrackerReachability.DescribeCause(wrapper);

        Assert.Contains("GitHub GraphQL request failed.", described);
        Assert.Contains(inner.Message, described);
    }

    [Fact]
    public void AnExceptionWithNoInnerCauseIsReportedAsItself()
    {
        Assert.Equal("plain", TrackerReachability.DescribeCause(new InvalidOperationException("plain")));
    }

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.ConnectionRefused)]
    public void SocketFailuresAreTransient(SocketError error)
    {
        var ex = new InvalidOperationException("wrapped", new SocketException((int)error));

        Assert.True(TrackerReachability.IsTransientConnectivity(ex));
    }

    [Fact]
    public void ARequestThatNeverReachedAServerIsTransient()
    {
        Assert.True(TrackerReachability.IsTransientConnectivity(
            new HttpRequestException("connection reset")));
    }

    // A refusal fails identically forever, so patience is the wrong response and
    // it must not be filed alongside the blips.
    [Fact]
    public void ARejectedRequestIsNotTransient()
    {
        var ex = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

        Assert.False(TrackerReachability.IsTransientConnectivity(ex));
    }

    [Fact]
    public void AMalformedQueryIsNotTransient()
    {
        Assert.False(TrackerReachability.IsTransientConnectivity(
            new InvalidOperationException("GraphQL: Field 'nope' doesn't exist")));
    }
}
