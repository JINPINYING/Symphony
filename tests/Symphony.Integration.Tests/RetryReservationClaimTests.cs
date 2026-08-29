using Microsoft.EntityFrameworkCore;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Integration.Tests;

public sealed class RetryReservationClaimTests
{
    [Fact]
    public async Task TryClaimIssueAsync_ShouldNotBypassFutureRetryReservationForSameOwner()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-coord.db");
        try
        {
            var options = BuildOptions(dbPath);
            await EnsureCreatedAsync(options);
            var now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
            var clock = new FixedTimeProvider(now);

            await using var context = new SymphonyDbContext(options);
            var store = new OrchestrationCoordinationStore(context, clock);
            Assert.True(await store.AcquireOrRenewLeaseAsync("dispatch", "instance-a", TimeSpan.FromMinutes(5)));
            Assert.True(await store.TryClaimIssueAsync("issue-84", "#84", "dispatch", "instance-a"));

            AddRetryState(context, now, attemptCount: 1, dueAtUtc: now.AddMinutes(5));
            await context.SaveChangesAsync();

            Assert.False(await store.TryClaimIssueAsync("issue-84", "#84", "dispatch", "instance-a"));

            var reservation = await context.RetryQueue.SingleAsync();
            reservation.DueAtUtc = now;
            await context.SaveChangesAsync();

            Assert.True(await store.TryClaimIssueAsync("issue-84", "#84", "dispatch", "instance-a"));
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public async Task TryClaimIssueAsync_ShouldFenceThirdPreSessionStartupAttempt()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-coord.db");
        try
        {
            var options = BuildOptions(dbPath);
            await EnsureCreatedAsync(options);
            var now = DateTimeOffset.Parse("2026-08-29T12:00:00Z");
            var clock = new FixedTimeProvider(now);

            await using var context = new SymphonyDbContext(options);
            var store = new OrchestrationCoordinationStore(context, clock);
            Assert.True(await store.AcquireOrRenewLeaseAsync("dispatch", "instance-a", TimeSpan.FromMinutes(5)));
            Assert.True(await store.TryClaimIssueAsync("issue-84", "#84", "dispatch", "instance-a"));

            AddRetryState(context, now, attemptCount: 2, dueAtUtc: now);
            await context.SaveChangesAsync();

            Assert.False(await store.TryClaimIssueAsync("issue-84", "#84", "dispatch", "instance-a"));
        }
        finally
        {
            DeleteDatabaseFiles(dbPath);
        }
    }

    private static DbContextOptions<SymphonyDbContext> BuildOptions(string dbPath)
    {
        var connectionString = $"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate";
        return new DbContextOptionsBuilder<SymphonyDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    private static async Task EnsureCreatedAsync(DbContextOptions<SymphonyDbContext> options)
    {
        await using var setupContext = new SymphonyDbContext(options);
        await setupContext.Database.EnsureCreatedAsync();
    }

    private static void AddRetryState(
        SymphonyDbContext context,
        DateTimeOffset now,
        int attemptCount,
        DateTimeOffset dueAtUtc)
    {
        const string runId = "run-84";
        context.Runs.Add(new RunEntity
        {
            Id = runId,
            IssueId = "issue-84",
            IssueIdentifier = "#84",
            OwnerInstanceId = "instance-a",
            Status = "retrying",
            State = "Open",
            StartedAtUtc = now.AddMinutes(-10)
        });

        for (var i = 0; i < attemptCount; i++)
        {
            context.RunAttempts.Add(new RunAttemptEntity
            {
                Id = $"attempt-{i + 1}",
                RunId = runId,
                IssueId = "issue-84",
                AttemptNumber = i + 1,
                Status = "stalled",
                StartedAtUtc = now.AddMinutes(-(attemptCount - i + 1)),
                CompletedAtUtc = now.AddMinutes(-(attemptCount - i))
            });
        }

        context.RetryQueue.Add(new RetryQueueEntity
        {
            IssueId = "issue-84",
            IssueIdentifier = "#84",
            RunId = runId,
            OwnerInstanceId = "instance-a",
            Attempt = attemptCount + 1,
            DueAtUtc = dueAtUtc,
            DelayType = "backoff",
            Error = "startup stalled",
            MaxBackoffMs = 300_000,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
    }

    private static void DeleteDatabaseFiles(string dbPath)
    {
        foreach (var path in new[] { dbPath, $"{dbPath}-wal", $"{dbPath}-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
