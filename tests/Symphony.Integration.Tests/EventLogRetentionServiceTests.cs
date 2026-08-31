using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Integration.Tests;

// The event log grows without bound: ~96% of it is agent streaming chatter, and
// the database reached 104 MB in three days. This prunes it. Because it DELETES,
// the tests below are mostly about what must survive.
public sealed class EventLogRetentionServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private string dbPath = string.Empty;
    private SymphonyDbContext dbContext = null!;
    private FixedTimeProvider clock = null!;

    public async Task InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"symphony-retention-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<SymphonyDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        dbContext = new SymphonyDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        clock = new FixedTimeProvider(Now);
    }

    public async Task DisposeAsync()
    {
        await dbContext.DisposeAsync();
        try { File.Delete(dbPath); } catch { /* best effort */ }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private EventLogRetentionService CreateService() =>
        new(dbContext, clock, NullLogger<EventLogRetentionService>.Instance);

    private static WorkflowEventLogRetentionSettings Settings(
        bool enabled = true,
        int protocolDays = 3,
        int operationalDays = 180,
        int maxRows = 250_000) =>
        new(enabled, protocolDays, operationalDays, maxRows);

    private async Task AddAsync(string eventName, string message, int daysAgo)
    {
        dbContext.EventLog.Add(new EventLogEntity
        {
            EventName = eventName,
            Message = message,
            Level = "Information",
            IssueId = "issue-1",
            IssueIdentifier = "#97",
            OccurredAtUtc = Now.AddDays(-daysAgo),
        });
        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task PruneAsync_ShouldDoNothingWhenDisabled()
    {
        await AddAsync("item/agentMessage/delta", "streamed", daysAgo: 400);

        var result = await CreateService().PruneAsync(Settings(enabled: false), CancellationToken.None);

        Assert.False(result.Ran);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, await dbContext.EventLog.CountAsync());
    }

    [Fact]
    public async Task PruneAsync_ShouldDeleteOldStreamingNoise()
    {
        await AddAsync("item/agentMessage/delta", "streamed text", daysAgo: 10);
        await AddAsync("item/commandExecution/outputDelta", "build output", daysAgo: 10);
        await AddAsync("claude_assistant", "claude_assistant", daysAgo: 10);

        var result = await CreateService().PruneAsync(Settings(), CancellationToken.None);

        Assert.Equal(3, result.Deleted);
        Assert.Equal(0, await dbContext.EventLog.CountAsync());
    }

    [Fact]
    public async Task PruneAsync_ShouldKeepTheOperationalRecordEvenWhenItIsOld()
    {
        // The whole point: noise goes early, the audit trail stays.
        await AddAsync("phase_merged", "PR #98 merged at head cd0d0f2c.", daysAgo: 90);
        await AddAsync("needs_command_center", "Reviewer returned NEEDS_COMMAND_CENTER.", daysAgo: 90);
        await AddAsync("item/agentMessage/delta", "streamed", daysAgo: 90);

        var result = await CreateService().PruneAsync(Settings(), CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        var survivors = await dbContext.EventLog.Select(entry => entry.EventName).ToListAsync();
        Assert.Equal(["phase_merged", "needs_command_center"], survivors);
    }

    [Fact]
    public async Task PruneAsync_ShouldKeepRecentNoiseSoALiveRunCanStillBeDebugged()
    {
        // Streaming events are how you diagnose a run that is happening now.
        await AddAsync("item/agentMessage/delta", "streamed", daysAgo: 1);

        var result = await CreateService().PruneAsync(Settings(protocolDays: 3), CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, await dbContext.EventLog.CountAsync());
    }

    [Fact]
    public async Task PruneAsync_ShouldEventuallyDeleteOperationalRowsPastTheLongWindow()
    {
        await AddAsync("phase_merged", "ancient merge", daysAgo: 400);
        await AddAsync("phase_merged", "recent merge", daysAgo: 10);

        var result = await CreateService().PruneAsync(Settings(operationalDays: 180), CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        var survivor = Assert.Single(await dbContext.EventLog.ToListAsync());
        Assert.Equal("recent merge", survivor.Message);
    }

    [Fact]
    public async Task PruneAsync_ShouldEnforceTheRowCapAsABackstop()
    {
        // All recent, so no age rule applies - only the cap can act.
        for (var i = 0; i < 10; i++)
        {
            await AddAsync("phase_merged", $"merge {i}", daysAgo: 0);
        }

        var result = await CreateService().PruneAsync(Settings(maxRows: 4), CancellationToken.None);

        Assert.Equal(6, result.Deleted);
        Assert.Equal(4, await dbContext.EventLog.CountAsync());

        // The cap removes the OLDEST rows, so the newest survive.
        var survivors = await dbContext.EventLog.OrderBy(e => e.Id).Select(e => e.Message).ToListAsync();
        Assert.Equal(["merge 6", "merge 7", "merge 8", "merge 9"], survivors);
    }

    [Fact]
    public async Task PruneAsync_ShouldWalkPastKeepersToReachOlderNoise()
    {
        // Regression guard: an operational row sitting at the oldest end must not
        // stop the scan, or everything behind it is never examined.
        await AddAsync("phase_merged", "keeper at the very back", daysAgo: 30);
        for (var i = 0; i < 20; i++)
        {
            await AddAsync("item/agentMessage/delta", $"noise {i}", daysAgo: 29);
        }

        var result = await CreateService().PruneAsync(Settings(), CancellationToken.None);

        Assert.Equal(20, result.Deleted);
        var survivor = Assert.Single(await dbContext.EventLog.ToListAsync());
        Assert.Equal("keeper at the very back", survivor.Message);
    }
}
