using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public interface IWatchedTaskReader
{
    Task<IReadOnlyList<WatchedTaskReport>> ReadAsync(
        IReadOnlyList<WorkflowWatchedTaskSettings> watched,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reports nothing, for hosts with no Windows Task Scheduler.
///
/// It returns an empty list rather than a set of <c>unknown</c> reports on
/// purpose: on a platform where these tasks cannot exist, the honest statement is
/// that there is nothing to watch, not that watching failed.
/// </summary>
public sealed class UnsupportedWatchedTaskReader : IWatchedTaskReader
{
    public Task<IReadOnlyList<WatchedTaskReport>> ReadAsync(
        IReadOnlyList<WorkflowWatchedTaskSettings> watched,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WatchedTaskReport>>([]);
}

/// <summary>
/// Reads task state from <c>schtasks.exe</c>.
///
/// schtasks rather than a Task Scheduler binding because it is present on every
/// Windows host, needs no package reference, and this only ever reads. The
/// service runs under the owner's own account, so the tasks it needs to see are
/// the ones it can see.
///
/// Results are cached briefly. The dashboard polls state every 15 seconds and the
/// tick loop runs on the same cadence; without a cache that is a process launch
/// per task per poll, forever, to answer a question whose answer changes on the
/// order of minutes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWatchedTaskReader(
    TimeProvider timeProvider,
    WatchedTaskHistory history,
    ILogger<WindowsWatchedTaskReader> logger) : IWatchedTaskReader
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<WatchedTaskReport> _cached = [];
    private DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<WatchedTaskReport>> ReadAsync(
        IReadOnlyList<WorkflowWatchedTaskSettings> watched,
        CancellationToken cancellationToken)
    {
        if (watched.Count == 0)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        if (now - _cachedAtUtc < CacheFor && _cached.Count > 0)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            if (now - _cachedAtUtc < CacheFor && _cached.Count > 0)
            {
                return _cached;
            }

            var reports = new List<WatchedTaskReport>(watched.Count);
            foreach (var task in watched)
            {
                // The history is consulted here rather than inside the evaluator
                // because it is the only layer that sees successive polls. The
                // evaluator stays a pure function of one sample, which is what
                // makes its wording testable.
                reports.Add(history.Observe(await ReadOneAsync(task, now, cancellationToken)));
            }

            _cached = reports;
            _cachedAtUtc = now;
            return reports;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WatchedTaskReport> ReadOneAsync(
        WorkflowWatchedTaskSettings task,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, stdout) = await RunSchTasksAsync(task.Path, cancellationToken);

            // A non-zero exit is nearly always "the task does not exist". That is
            // a real finding, not a monitoring failure: something the plane
            // depends on is not registered at all.
            if (exitCode != 0)
            {
                return Unknown(task,
                    "The scheduler does not have a task registered at this path, so nothing is waking this part of the plane.");
            }

            var lines = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Trim().Length > 0)
                .ToList();

            if (lines.Count < 2)
            {
                return Unknown(task, "The scheduler returned no rows for this task.");
            }

            var headers = WatchedTaskEvaluator.SplitCsvLine(lines[0]);
            var values = WatchedTaskEvaluator.SplitCsvLine(lines[1]);

            return WatchedTaskEvaluator.ParseCsvRecord(
                       headers, values, task.Name, task.Path,
                       task.ExpectEveryMinutes, task.LateAfterMinutes,
                       TimeZoneInfo.Local, now)
                   ?? Unknown(task, "The scheduler's reply could not be read.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let a monitoring probe take down the thing it monitors. An
            // unreadable task is reported as unmonitored, which is the truth.
            logger.LogWarning(ex, "Could not read scheduled task {TaskPath}.", task.Path);
            return Unknown(task, $"Could not be read: {ex.Message}");
        }
    }

    private static WatchedTaskReport Unknown(WorkflowWatchedTaskSettings task, string explanation) =>
        new(task.Name, task.Path, "Unknown", "Unknown", null, null, null,
            task.ExpectEveryMinutes, WatchedTaskReport.HealthUnknown, explanation);

    private static async Task<(int ExitCode, string Stdout)> RunSchTasksAsync(
        string taskPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/query");
        startInfo.ArgumentList.Add("/fo");
        startInfo.ArgumentList.Add("CSV");
        startInfo.ArgumentList.Add("/v");
        startInfo.ArgumentList.Add("/tn");
        startInfo.ArgumentList.Add(taskPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("schtasks.exe could not be started.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QueryTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"schtasks did not answer within {QueryTimeout.TotalSeconds:0} seconds.");
        }

        return (process.ExitCode, await stdoutTask);

        static void TryKill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone, or not ours to kill. Nothing useful to do here.
            }
        }
    }
}
