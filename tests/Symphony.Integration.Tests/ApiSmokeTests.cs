using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;
using Symphony.Host;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows;

namespace Symphony.Integration.Tests;

public sealed class ApiSmokeTests
{
    [Fact]
    public async Task HealthEndpoint_ShouldReturnSuccess()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        HttpStatusCode? statusCode = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    var response = await client.GetAsync("/api/v1/health", cancellationToken);
                    statusCode = response.StatusCode;
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.OK, statusCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task RuntimeEndpoint_ShouldReturnConfiguredDefaults()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        HttpStatusCode? statusCode = null;
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    var response = await client.GetAsync("/api/v1/runtime", cancellationToken);
                    statusCode = response.StatusCode;
                    content = await response.Content.ReadAsStringAsync(cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.OK, statusCode);
            Assert.NotNull(content);
            Assert.Contains("\"intervalMs\":600000", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"maxConcurrentAgents\":5", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"maxTurns\":20", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task StateEndpoint_ShouldReturnSnapshotShape()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/api/v1/state", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);
            Assert.Contains("\"counts\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"tracked\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"activity\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"coordination\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"codex_totals\"", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task StateEndpoint_ShouldSuppressFallbackOnlyOtherMessageActivityEntries()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            await SeedIssueStateAsync(
                dbPath,
                "MT-650",
                includeFallbackOtherMessageEvent: true,
                includeMeaningfulOtherMessageEvent: true);

            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/api/v1/state", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);

            var responseContent = content!;
            using var document = JsonDocument.Parse(responseContent);
            var activity = document.RootElement
                .GetProperty("activity")
                .EnumerateArray()
                .Select(entry => new
                {
                    Event = entry.GetProperty("event").GetString(),
                    Message = entry.TryGetProperty("message", out var messageProperty) && messageProperty.ValueKind != JsonValueKind.Null
                        ? messageProperty.GetString()
                        : null
                })
                .ToList();

            Assert.DoesNotContain(activity, entry =>
                string.Equals(entry.Event, "other_message", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(entry.Message));
            Assert.DoesNotContain(activity, entry =>
                string.Equals(entry.Event, "other_message", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Message, "other_message", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(activity, entry =>
                string.Equals(entry.Event, "other_message", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Message, "Planner emitted a plain-text note.", StringComparison.Ordinal));
            Assert.Contains(activity, entry =>
                string.Equals(entry.Event, "notification", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Message, "Working on tests", StringComparison.Ordinal));

            // The poll heartbeat filled 9 of 24 rows - more than a third of the feed
            // spent saying the poller ran - and what it carries is already on the
            // page as attention items. Agent reports have their own strip at the
            // top, so listing them here is the same fact twice. Both stay in the log
            // and in the raw view; only the operational feed is quieter.
            Assert.DoesNotContain(activity, entry =>
                string.Equals(entry.Event, "open_pull_requests_updated", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(activity, entry =>
                string.Equals(entry.Event, "agent_activity_reported", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task StateEndpoint_ShouldReturnTrackedIssueDistributionIncludingClosedIssues()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            await SeedTrackedIssueCacheAsync(
                dbPath,
                ("issue-1", "MT-651", "Close stale worktree", "Closed"),
                ("issue-2", "MT-652", "Keep polling active items", "Open"),
                ("issue-3", "MT-653", "Archive completed lease rows", "Closed"));

            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/api/v1/state", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);

            using var document = JsonDocument.Parse(content!);
            var groupsByState = document.RootElement
                .GetProperty("tracked")
                .GetProperty("by_state")
                .EnumerateArray()
                .ToDictionary(
                    group => group.GetProperty("state").GetString() ?? string.Empty,
                    group => group.GetProperty("count").GetInt32(),
                    StringComparer.OrdinalIgnoreCase);

            Assert.Equal(3, document.RootElement.GetProperty("counts").GetProperty("tracked").GetInt32());
            Assert.Equal(2, groupsByState["Closed"]);
            Assert.Equal(1, groupsByState["Open"]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task RefreshEndpoint_ShouldQueueBestEffortPoll()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        HttpStatusCode? statusCode = null;
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    var response = await client.PostAsync("/api/v1/refresh", content: null, cancellationToken);
                    statusCode = response.StatusCode;
                    content = await response.Content.ReadAsStringAsync(cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.Accepted, statusCode);
            Assert.NotNull(content);
            Assert.Contains("\"queued\":true", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"operations\"", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task RootEndpoint_ShouldServeDashboardHtml()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        HttpStatusCode? statusCode = null;
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    var response = await client.GetAsync("/", cancellationToken);
                    statusCode = response.StatusCode;
                    content = await response.Content.ReadAsStringAsync(cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.OK, statusCode);
            Assert.NotNull(content);
            var htmlContent = content!;
            Assert.Contains("Symphony Watchtower", htmlContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("wt-shell", htmlContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("wt-grid", htmlContent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("max-w-7xl", htmlContent, StringComparison.OrdinalIgnoreCase);

            // Every panel the reader needs is declared in the markup. The roadmap
            // once had no container of its own - it built one at runtime, anchored
            // to a panel that was later removed, and vanished silently. Declaring
            // them all here means a panel cannot leave without a test saying so.
            foreach (var panel in new[]
                     {
                         "panel-attention",
                         "panel-health",
                         "panel-team",
                         "panel-activity",
                         "panel-queue",
                         "panel-roadmap",
                         "panel-utility",
                         "panel-advanced",
                         "staleness-banner",
                         "live-badge"
                     })
            {
                Assert.Contains($"id=\"{panel}\"", htmlContent, StringComparison.OrdinalIgnoreCase);
            }

            // The issue-detail, tracked-issues and issue-distribution panels were
            // removed: a per-poll /api/v1/{issue} call feeding a panel nobody
            // opened, a list of mostly-closed history, and a count of closed issues
            // that only grows. Asserted absent rather than deleted quietly, so
            // bringing one back is a deliberate act.
            foreach (var removed in new[] { "issue-detail", "tracked-issues", "issue-distribution" })
            {
                Assert.DoesNotContain($"id=\"{removed}\"", htmlContent, StringComparison.OrdinalIgnoreCase);
            }

            var issueDetailElementMatch = System.Text.RegularExpressions.Regex.Match(
                htmlContent,
                @"<section[^>]*id=""issue-detail""[^>]*>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            Assert.False(issueDetailElementMatch.Success, "The issue-detail panel was removed.");

        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task DashboardCssAsset_ShouldIncludeFullWidthShellStyles()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/assets/dashboard.css", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);
            var cssContent = content!;
            Assert.Contains(".dashboard-shell", cssContent, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"(?i)\.dashboard-shell\s*\{[^}]*width\s*:\s*100%", cssContent);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task DashboardCssAsset_ShouldIncludeSidebarScrollStyles()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/assets/dashboard.css", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);
            var cssContent = content!;
            Assert.Contains(".dashboard-rail", cssContent, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"(?i)overflow-y\s*:\s*auto", cssContent);
            Assert.Matches(@"(?i)max-height\s*:\s*calc\(100vh\s*-\s*3rem\)", cssContent);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task DashboardJavaScriptAsset_ShouldClampRetryCountdownsToNow()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/assets/dashboard.js", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);

            var javascriptContent = content!;
            Assert.Matches(@"function\s+formatRetryCountdown\b", javascriptContent);
            Assert.Matches(@"Math\.max\(\s*diffSeconds\s*,\s*0\s*\)", javascriptContent);
            // The card that once carried this text is gone; retrying work now shows
            // in the queue panel, which is where "why has this not started" belongs.
            // What mattered was never the card - it was that the countdown goes
            // through formatRetryCountdown, whose clamp keeps an overdue retry from
            // reading as a future one. That call site is what is asserted here.
            Assert.Matches(@"formatRetryCountdown\(\s*retry\.due_at\s*\)", javascriptContent);
            Assert.DoesNotMatch(@"formatRelativeTime\(\s*snapshot\.retrying\[0\]\.due_at\s*\)", javascriptContent);
            Assert.DoesNotMatch(@"formatRelativeTime\(\s*retry\.due_at\s*\)", javascriptContent);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    // DashboardJavaScriptAsset_ShouldRenderTrackedIssueStateLabelsFromCache was
    // deleted here. It asserted the state label inside renderTrackedIssue, and the
    // tracked-issues panel it belonged to was removed - a list of mostly-closed
    // history nobody read. ApiSmokeTests still asserts that panel stays absent, so
    // bringing it back is a deliberate act rather than an accident.

    [Fact]
    public async Task DashboardJavaScriptAsset_ShouldNotReportHealthBeforeASnapshotLoads()
    {
        // The page once rendered "All systems operational" and "Nothing is
        // waiting on you" while it held no snapshot at all - every field was
        // empty, and the absence of bad news read as a clean bill of health.
        // That is the failure this dashboard exists to prevent, so both panels
        // must derive their calm from a snapshot being present, and the engine
        // must be positively healthy rather than merely not-known-to-be-broken.
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/assets/dashboard.js", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);
            var javascriptContent = content!;

            Assert.Matches(@"const\s+blind\s*=\s*!state\.snapshot;", javascriptContent);
            Assert.Contains("Cannot reach the engine", javascriptContent, StringComparison.Ordinal);
            Assert.Contains("Cannot read the plane", javascriptContent, StringComparison.Ordinal);

            // "not false" would let an unknown engine pass as healthy.
            Assert.Matches(@"const\s+engineOk\s*=\s*state\.health\?\.ok\s*===\s*true;", javascriptContent);
            Assert.Matches(@"const\s+allOk\s*=\s*!blind\s*&&", javascriptContent);
            Assert.DoesNotMatch(@"const\s+engineOk\s*=\s*state\.health\?\.ok\s*!==\s*false;", javascriptContent);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task DashboardJavaScriptAsset_ShouldIncludeWorkflowEditorActions()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/assets/dashboard.js", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);

            var javascriptContent = content!;
            Assert.Contains("/api/v1/workflow", javascriptContent, StringComparison.Ordinal);
            Assert.Contains("data-action='save-workflow'", javascriptContent, StringComparison.Ordinal);
            Assert.Contains("data-action='reload-workflow'", javascriptContent, StringComparison.Ordinal);
            Assert.Contains("data-action='toggle-workflow-editor'", javascriptContent, StringComparison.Ordinal);
            Assert.Contains("data-workflow-field=\"frontMatterText\"", javascriptContent, StringComparison.Ordinal);
            Assert.Contains("data-workflow-field=\"promptTemplate\"", javascriptContent, StringComparison.Ordinal);
            Assert.Matches(@"workflowEditorExpanded\s*:\s*false", javascriptContent);
            Assert.Contains("function syncWorkflowEditorChrome()", javascriptContent, StringComparison.Ordinal);
            Assert.Contains("function renderWorkflowEditorSection", javascriptContent, StringComparison.Ordinal);
            Assert.Contains("data-workflow-status", javascriptContent, StringComparison.Ordinal);
            Assert.Contains("Expand editor", javascriptContent, StringComparison.Ordinal);
            Assert.Contains("Minimize editor", javascriptContent, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task DashboardCssAsset_ShouldIncludeWorkflowEditorSummaryCardStyles()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/assets/dashboard.css", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);
            var cssContent = content!;
            Assert.Contains(".workflow-summary-card", cssContent, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"(?i)\.workflow-summary-card\s*\{[^}]*border", cssContent);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task WorkflowEndpoint_ShouldReturnStructuredErrorWhenWorkflowFileDisappears()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        HttpStatusCode? statusCode = null;
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    TryDeleteFile(workflowPath);
                    using var client = app.GetTestClient();
                    var response = await client.GetAsync("/api/v1/workflow", cancellationToken);
                    statusCode = response.StatusCode;
                    content = await response.Content.ReadAsStringAsync(cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.BadRequest, statusCode);
            Assert.NotNull(content);
            Assert.Contains("\"code\":\"missing_workflow_file\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"message\"", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task WorkflowEndpoint_ShouldMaskInlineTrackerApiKeysAndPersistEdits()
    {
        var workflowPath = CreateWorkflowPath("""
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: inline-secret-token
              owner: released
              repo: symphony
            polling:
              interval_ms: 600000
            agent:
              max_concurrent_agents: 5
            ---
            Prompt body.
            """);
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        WorkflowEditorDocument? workflowDocument = null;
        HttpStatusCode? saveStatusCode = null;
        string? runtimeContent = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();

                    workflowDocument = await client.GetFromJsonAsync<WorkflowEditorDocument>("/api/v1/workflow", cancellationToken);
                    Assert.NotNull(workflowDocument);
                    Assert.True(workflowDocument!.HasMaskedTrackerApiKey);
                    Assert.Contains(WorkflowEditorService.TrackerApiKeyPlaceholder, workflowDocument.FrontMatterText, StringComparison.Ordinal);

                    var updatedDocument = workflowDocument with
                    {
                        FrontMatterText = workflowDocument.FrontMatterText.Replace("owner: released", "owner: updated-owner", StringComparison.Ordinal),
                        PromptTemplate = "Updated prompt body."
                    };

                    var saveResponse = await client.PutAsJsonAsync("/api/v1/workflow", updatedDocument, cancellationToken);
                    saveStatusCode = saveResponse.StatusCode;
                    runtimeContent = await client.GetStringAsync("/api/v1/runtime", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.OK, saveStatusCode);
            Assert.NotNull(runtimeContent);
            Assert.Contains("\"owner\":\"updated-owner\"", runtimeContent, StringComparison.OrdinalIgnoreCase);

            var persistedContent = await File.ReadAllTextAsync(workflowPath);
            Assert.Contains("api_key: inline-secret-token", persistedContent, StringComparison.Ordinal);
            Assert.DoesNotContain(WorkflowEditorService.TrackerApiKeyPlaceholder, persistedContent, StringComparison.Ordinal);
            Assert.Contains("owner: updated-owner", persistedContent, StringComparison.Ordinal);
            Assert.Contains("Updated prompt body.", persistedContent, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task WorkflowEndpoint_ShouldRejectTrackerPlaceholderWhenNoInlineSecretCanBeRestored()
    {
        var apiKeyEnvVar = $"SYMPHONY_TEST_GITHUB_TOKEN_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(apiKeyEnvVar, "test-token");
        var workflowPath = CreateWorkflowPath($$"""
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: ${{apiKeyEnvVar}}
              owner: released
              repo: symphony
            polling:
              interval_ms: 600000
            agent:
              max_concurrent_agents: 5
            codex:
              command: codex app-server
              turn_timeout_ms: 3600000
              approval_policy: never
              thread_sandbox: danger-full-access
              turn_sandbox_policy: danger-full-access
              read_timeout_ms: 5000
              stall_timeout_ms: 300000
            workspace:
              root: ./workspaces
              shared_clone_path: ./workspaces/repo
              worktrees_root: ./workspaces/worktrees
              base_branch: main
            hooks:
              timeout_ms: 60000
            ---
            Prompt body.
            """);
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        HttpStatusCode? statusCode = null;
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();

                    var workflowDocument = await client.GetFromJsonAsync<WorkflowEditorDocument>("/api/v1/workflow", cancellationToken);
                    Assert.NotNull(workflowDocument);

                    var updatedDocument = workflowDocument! with
                    {
                        FrontMatterText = workflowDocument.FrontMatterText.Replace(
                            $"api_key: ${apiKeyEnvVar}",
                            $"api_key: {WorkflowEditorService.TrackerApiKeyPlaceholder}",
                            StringComparison.Ordinal)
                    };

                    var response = await client.PutAsJsonAsync("/api/v1/workflow", updatedDocument, cancellationToken);
                    statusCode = response.StatusCode;
                    content = await response.Content.ReadAsStringAsync(cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.BadRequest, statusCode);
            Assert.NotNull(content);
            Assert.Contains(WorkflowEditorService.InvalidTrackerApiKeyPlaceholderCode, content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(apiKeyEnvVar, null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task IssueEndpoint_ShouldReturnTrackedIssueDetails()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            await SeedIssueStateAsync(dbPath, "MT-649");

            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/api/v1/MT-649", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);
            Assert.Contains("\"issue_identifier\":\"MT-649\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"workspace\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"recent_events\"", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task IssueEndpoint_ShouldSuppressFallbackOnlyOtherMessageRecentEvents()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            await SeedIssueStateAsync(
                dbPath,
                "MT-650",
                includeFallbackOtherMessageEvent: true,
                includeMeaningfulOtherMessageEvent: true);

            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/api/v1/MT-650", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);

            var responseContent = content!;
            using var document = JsonDocument.Parse(responseContent);
            var recentEvents = document.RootElement
                .GetProperty("recent_events")
                .EnumerateArray()
                .Select(entry => new
                {
                    Event = entry.GetProperty("event").GetString(),
                    Message = entry.TryGetProperty("message", out var messageProperty) && messageProperty.ValueKind != JsonValueKind.Null
                        ? messageProperty.GetString()
                        : null
                })
                .ToList();

            Assert.DoesNotContain(recentEvents, entry =>
                string.Equals(entry.Event, "other_message", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(entry.Message));
            Assert.DoesNotContain(recentEvents, entry =>
                string.Equals(entry.Event, "other_message", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Message, "other_message", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(recentEvents, entry =>
                string.Equals(entry.Event, "other_message", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Message, "Planner emitted a plain-text note.", StringComparison.Ordinal));
            Assert.Contains(recentEvents, entry =>
                string.Equals(entry.Event, "notification", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Message, "Working on tests", StringComparison.Ordinal));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task HostStartup_ShouldFailFastWhenWorkflowApiKeyCannotBeResolved()
    {
        var missingApiKeyEnvVar = $"SYMPHONY_MISSING_API_KEY_{Guid.NewGuid():N}";
        var workflowPath = CreateWorkflowPath($$"""
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: ${{missingApiKeyEnvVar}}
              owner: released
              repo: symphony
            polling:
              interval_ms: 600000
            agent:
              max_concurrent_agents: 5
            codex:
              command: codex app-server
              turn_timeout_ms: 3600000
              approval_policy: never
              thread_sandbox: danger-full-access
              turn_sandbox_policy: danger-full-access
              read_timeout_ms: 5000
              stall_timeout_ms: 300000
            workspace:
              root: ./workspaces
              shared_clone_path: ./workspaces/repo
              worktrees_root: ./workspaces/worktrees
              base_branch: main
            hooks:
              timeout_ms: 60000
            ---
            Prompt body.
            """);
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();

        try
        {
            Environment.SetEnvironmentVariable(missingApiKeyEnvVar, null);
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder =>
                {
                    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Persistence:ConnectionString"] = $"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate"
                    });
                },
                runApplicationAsync: static async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.Equal(1, exitCode);
            Assert.Contains("missing_tracker_api_key", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("tracker.api_key", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    private static string CreateValidWorkflowPath()
    {
        return CreateWorkflowPath("""
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: test-token
              owner: released
              repo: symphony
            polling:
              interval_ms: 600000
            agent:
              max_concurrent_agents: 5
            codex:
              command: codex app-server
              turn_timeout_ms: 3600000
              approval_policy: never
              thread_sandbox: danger-full-access
              turn_sandbox_policy: danger-full-access
              read_timeout_ms: 5000
              stall_timeout_ms: 300000
            workspace:
              root: ./workspaces
              shared_clone_path: ./workspaces/repo
              worktrees_root: ./workspaces/worktrees
              base_branch: main
            hooks:
              timeout_ms: 60000
            ---
            Prompt body.
            """);
    }

    private static string CreateWorkflowPath(string content)
    {
        var workflowPath = Path.Combine(Path.GetTempPath(), $"symphony-int-workflow-{Guid.NewGuid():N}.md");
        File.WriteAllText(workflowPath, content);
        return workflowPath;
    }

    private static void ConfigureTestServer(WebApplicationBuilder builder, string dbPath)
    {
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:ConnectionString"] = $"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate"
        });
    }

    // The panel's one write. Every "action" on the status page used to be a link
    // or a button that copied text - and one of those copied a command-center flag
    // that did not exist. A control has to do the thing it is labelled with.
    [Fact]
    public async Task DirectiveAction_ShouldPostTheDirectiveTheOwnerPressed()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        var tracker = new FakeGitHubTrackerClient();
        HttpStatusCode? statusCode = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services, tracker),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    var response = await client.PostAsync(
                        "/api/v1/actions/directive",
                        new StringContent(
                            """{"issueId":"issue-1","issueIdentifier":"#142","action":"resume","phase":"review"}""",
                            System.Text.Encoding.UTF8,
                            "application/json"),
                        cancellationToken);
                    statusCode = response.StatusCode;
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.Accepted, statusCode);

            var posted = Assert.Single(tracker.PostedComments);
            Assert.Equal("issue-1", posted.IssueId);
            Assert.Contains("symphony:directive", posted.Body, StringComparison.Ordinal);
            Assert.Contains("action: resume", posted.Body, StringComparison.Ordinal);
            Assert.Contains("phase: review", posted.Body, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    // And a directive the engine would refuse is refused here, rather than posted
    // and left for the owner to delete by hand.
    [Fact]
    public async Task DirectiveAction_ShouldRefuseADirectiveTheEngineWouldReject()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        var tracker = new FakeGitHubTrackerClient();
        HttpStatusCode? statusCode = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services, tracker),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    var response = await client.PostAsync(
                        "/api/v1/actions/directive",
                        new StringContent(
                            """{"issueId":"issue-1","action":"resume","phase":"not_a_phase"}""",
                            System.Text.Encoding.UTF8,
                            "application/json"),
                        cancellationToken);
                    statusCode = response.StatusCode;
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.BadRequest, statusCode);
            Assert.Empty(tracker.PostedComments);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    private static void RegisterFakeTracker(IServiceCollection services)
    {
        var trackerClient = new FakeGitHubTrackerClient();
        services.AddSingleton<ITrackerClient>(trackerClient);
        services.AddSingleton<IGitHubTrackerClient>(trackerClient);
    }

    private static void RegisterFakeTracker(IServiceCollection services, FakeGitHubTrackerClient trackerClient)
    {
        services.AddSingleton<ITrackerClient>(trackerClient);
        services.AddSingleton<IGitHubTrackerClient>(trackerClient);
    }

    private static async Task SeedIssueStateAsync(
        string dbPath,
        string issueIdentifier,
        bool includeFallbackOtherMessageEvent = false,
        bool includeMeaningfulOtherMessageEvent = false)
    {
        var options = new DbContextOptionsBuilder<SymphonyDbContext>()
            .UseSqlite($"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate")
            .Options;

        await using var dbContext = new SymphonyDbContext(options);
        await dbContext.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;

        dbContext.Runs.Add(new RunEntity
        {
            Id = "run-1",
            IssueId = "issue-1",
            IssueIdentifier = issueIdentifier,
            OwnerInstanceId = "instance-1",
            Status = RunStatusNames.Running,
            State = "Open",
            SessionId = "thread-1-turn-1",
            LastEvent = "notification",
            LastMessage = "Working on tests",
            StartedAtUtc = now.AddMinutes(-1),
            LastEventAtUtc = now,
            TurnCount = 2,
            InputTokens = 10,
            OutputTokens = 5,
            TotalTokens = 15
        });
        dbContext.RunAttempts.Add(new RunAttemptEntity
        {
            Id = "attempt-1",
            RunId = "run-1",
            IssueId = "issue-1",
            Status = RunStatusNames.Running,
            StartedAtUtc = now.AddMinutes(-1)
        });
        dbContext.WorkspaceRecords.Add(new WorkspaceRecordEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = issueIdentifier,
            WorkspacePath = @"C:\tmp\MT-649",
            BranchName = "feature/mt-649",
            LastPreparedAtUtc = now.AddMinutes(-2)
        });
        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = issueIdentifier,
            RunId = "run-1",
            RunAttemptId = "attempt-1",
            SessionId = "thread-1-turn-1",
            EventName = "notification",
            Level = "Information",
            Message = "Working on tests",
            OccurredAtUtc = now
        });

        if (includeFallbackOtherMessageEvent)
        {
            dbContext.EventLog.Add(new EventLogEntity
            {
                IssueId = "issue-1",
                IssueIdentifier = issueIdentifier,
                RunId = "run-1",
                RunAttemptId = "attempt-1",
                SessionId = "thread-1-turn-1",
                EventName = "other_message",
                Level = "Information",
                Message = "other_message",
                OccurredAtUtc = now.AddSeconds(1)
            });
        }

        if (includeMeaningfulOtherMessageEvent)
        {
            dbContext.EventLog.Add(new EventLogEntity
            {
                IssueId = "issue-1",
                IssueIdentifier = issueIdentifier,
                RunId = "run-1",
                RunAttemptId = "attempt-1",
                SessionId = "thread-1-turn-1",
                EventName = "other_message",
                Level = "Information",
                Message = "Planner emitted a plain-text note.",
                OccurredAtUtc = now.AddSeconds(2)
            });
        }

        dbContext.IssueCache.Add(new IssueCacheEntity
        {
            IssueId = "issue-1",
            Identifier = issueIdentifier,
            Title = "Add runtime dashboard",
            State = "Open",
            Url = "https://github.com/released/symphony/issues/649",
            Milestone = "Sprint 12",
            LabelsJson = "[\"dashboard\",\"ui\"]",
            PullRequestsJson = "[]",
            BlockedByJson = "[]",
            CachedAtUtc = now,
            UpdatedAtUtc = now
        });
        dbContext.InstanceLeases.Add(new InstanceLeaseEntity
        {
            LeaseName = "poll-dispatch",
            OwnerInstanceId = "instance-1",
            AcquiredAtUtc = now.AddMinutes(-1),
            UpdatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(10)
        });

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedTrackedIssueCacheAsync(
        string dbPath,
        params (string IssueId, string IssueIdentifier, string Title, string State)[] issues)
    {
        var options = new DbContextOptionsBuilder<SymphonyDbContext>()
            .UseSqlite($"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate")
            .Options;

        await using var dbContext = new SymphonyDbContext(options);
        await dbContext.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;

        foreach (var issue in issues)
        {
            dbContext.IssueCache.Add(new IssueCacheEntity
            {
                IssueId = issue.IssueId,
                Identifier = issue.IssueIdentifier,
                Title = issue.Title,
                State = issue.State,
                LabelsJson = "[]",
                PullRequestsJson = "[]",
                BlockedByJson = "[]",
                CachedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private sealed class FakeGitHubTrackerClient : IGitHubTrackerClient
    {
        public Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(
            TrackerQuery query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);
        }

        public Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(
            TrackerQuery query,
            IReadOnlyList<string> states,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);
        }

        public Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(
            TrackerQuery query,
            IReadOnlyList<string> issueIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<IssueStateSnapshot>>([]);
        }

        public Task<GitHubGraphQlExecutionResult> ExecuteGitHubGraphQlAsync(
            TrackerQuery query,
            string graphQlDocument,
            string? variablesJson,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitHubGraphQlExecutionResult(true, "{\"data\":{}}"));
        }

        public Task<IssueCommentMarkerSnapshot?> FetchIssueCommentMarkerAsync(
            TrackerQuery query,
            string issueId,
            string marker,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IssueCommentMarkerSnapshot?>(
                new IssueCommentMarkerSnapshot(issueId, "Open", null, MarkerFound: false));
        }

        public List<(string Repository, string IssueId, string Body)> PostedComments { get; } = [];

        public Task<string?> PostIssueCommentAsync(
            TrackerQuery query,
            string issueId,
            string body,
            CancellationToken cancellationToken = default)
        {
            PostedComments.Add(($"{query.Owner}/{query.Repo}", issueId, body));
            return Task.FromResult<string?>("comment-1");
        }

        public Task<IReadOnlyList<NormalizedIssueComment>> FetchIssueCommentsAsync(
            TrackerQuery query,
            string issueId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NormalizedIssueComment>>([]);
        }

        public Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByIdsAsync(
            TrackerQuery query,
            IReadOnlyList<string> issueIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);
        }

        public Task CloseIssueAsync(
            TrackerQuery query,
            string issueId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<PullRequestStatus?> FetchPullRequestStatusAsync(
            TrackerQuery query,
            int pullRequestNumber,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<PullRequestStatus?>(null);
        }

        public Task<PullRequestStatus?> FetchOpenPullRequestByHeadBranchAsync(
            TrackerQuery query,
            string headRefName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<PullRequestStatus?>(null);
        }

        public Task<IReadOnlyList<string>> FetchPullRequestFilesAsync(
            TrackerQuery query,
            int pullRequestNumber,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<IReadOnlyList<OpenPullRequest>> FetchOpenPullRequestsAsync(TrackerQuery query, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpenPullRequest>>(OpenPullRequests);

        public IReadOnlyList<OpenPullRequest> OpenPullRequests { get; set; } = [];


        public Task<string?> MergePullRequestAsync(
            TrackerQuery query,
            int pullRequestNumber,
            string expectedHeadSha,
            string method,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("merging is not supported by this fake");
        }

        public Task RemoveIssueLabelsAsync(
            TrackerQuery query,
            string issueId,
            IReadOnlyList<string> labelNames,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private static void TryDeleteFile(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
    }
}
