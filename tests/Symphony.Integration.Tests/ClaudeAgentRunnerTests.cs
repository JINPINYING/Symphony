using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Models;
using Symphony.Infrastructure.Agent.Claude;

namespace Symphony.Integration.Tests;

public sealed class ClaudeAgentRunnerTests
{
    [Fact]
    public async Task RunIssueAsync_ShouldSucceedOnResultSuccessAndMapSessionAndTokens()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var harness = CreateClaudeHarness("""
            $prompt = [Console]::In.ReadToEnd()
            @{ type = 'system'; subtype = 'init'; session_id = 'sess-1' } | ConvertTo-Json -Compress
            @{ type = 'assistant'; session_id = 'sess-1'; message = @{ role = 'assistant'; content = @(@{ type = 'text'; text = 'working on it' }) } } | ConvertTo-Json -Compress -Depth 6
            @{ type = 'result'; subtype = 'success'; is_error = $false; session_id = 'sess-1'; result = "done: $($prompt.Substring(0, 10))"; usage = @{ input_tokens = 100; output_tokens = 50 } } | ConvertTo-Json -Compress -Depth 4
            """);

        var updates = new List<AgentRunUpdate>();
        var runner = new ClaudeAgentRunner(NullLogger<ClaudeAgentRunner>.Instance);

        var result = await runner.RunIssueAsync(
            CreateRequest(harness, timeoutMs: 120_000, prompt: "marker-123 do the task"),
            (update, _) =>
            {
                updates.Add(update);
                return Task.CompletedTask;
            });

        Assert.True(result.Success, result.Stderr);
        Assert.Contains("\"result\"", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(updates, update => update.EventType == "claude_system_init");
        Assert.Contains(updates, update => update.EventType == "claude_assistant" && update.Message == "working on it");

        var final = Assert.Single(updates, update => update.EventType == "turn_completed");
        Assert.Equal("sess-1", final.ThreadId);
        Assert.Equal(100, final.InputTokens);
        Assert.Equal(50, final.OutputTokens);
        Assert.Equal(150, final.TotalTokens);
        Assert.False(final.TokenUsageIsDelta);
        Assert.Contains("marker-123", final.Message);
    }

    // ADCP#26. AgentRunUpdate.SessionId composes ThreadId with TurnId and is null
    // unless both are set. Claude reports a session id and no turn id, so before this
    // fix every update during a live run carried SessionId == null and the run's
    // session was recorded only by the final turn_completed. The startup guard reads
    // exactly that field, concluded the run was still "pre-session" three minutes in,
    // and killed a working agent - every time, for the whole life of the feature.
    [Fact]
    public async Task RunIssueAsync_ShouldReportItsSessionFromTheFirstEventNotOnlyAtTheEnd()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var harness = CreateClaudeHarness("""
            $null = [Console]::In.ReadToEnd()
            @{ type = 'system'; subtype = 'init'; session_id = 'sess-1' } | ConvertTo-Json -Compress
            @{ type = 'assistant'; session_id = 'sess-1'; message = @{ role = 'assistant'; content = @(@{ type = 'text'; text = 'working on it' }) } } | ConvertTo-Json -Compress -Depth 6
            @{ type = 'result'; subtype = 'success'; is_error = $false; session_id = 'sess-1'; result = 'done'; usage = @{ input_tokens = 1; output_tokens = 1 } } | ConvertTo-Json -Compress -Depth 4
            """);

        var updates = new List<AgentRunUpdate>();
        var runner = new ClaudeAgentRunner(NullLogger<ClaudeAgentRunner>.Instance);

        var result = await runner.RunIssueAsync(
            CreateRequest(harness, timeoutMs: 120_000),
            (update, _) =>
            {
                updates.Add(update);
                return Task.CompletedTask;
            });

        Assert.True(result.Success, result.Stderr);

        var init = Assert.Single(updates, update => update.EventType == "claude_system_init");
        Assert.False(string.IsNullOrWhiteSpace(init.SessionId));

        // And it is the SAME session throughout, so one run produces one session
        // record rather than a live one and a separate final one.
        var sessionIds = updates
            .Where(update => update.SessionId is not null)
            .Select(update => update.SessionId)
            .Distinct()
            .ToList();
        Assert.Single(sessionIds);
        Assert.Equal(init.SessionId, Assert.Single(updates, update => update.EventType == "turn_completed").SessionId);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldFailOnErrorResult()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var harness = CreateClaudeHarness("""
            $null = [Console]::In.ReadToEnd()
            @{ type = 'system'; subtype = 'init'; session_id = 'sess-1' } | ConvertTo-Json -Compress
            @{ type = 'result'; subtype = 'error_during_execution'; is_error = $true; session_id = 'sess-1'; result = 'the run failed' } | ConvertTo-Json -Compress
            """);

        var runner = new ClaudeAgentRunner(NullLogger<ClaudeAgentRunner>.Instance);

        var result = await runner.RunIssueAsync(CreateRequest(harness, timeoutMs: 120_000));

        Assert.False(result.Success);
        Assert.Equal("claude_error_during_execution", result.ErrorCode);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldKillOnStallTimeout()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var harness = CreateClaudeHarness("""
            $null = [Console]::In.ReadToEnd()
            @{ type = 'system'; subtype = 'init'; session_id = 'sess-1' } | ConvertTo-Json -Compress
            Start-Sleep -Seconds 120
            """);

        var runner = new ClaudeAgentRunner(NullLogger<ClaudeAgentRunner>.Instance)
        {
            StallTimeoutMs = 2_000
        };

        var result = await runner.RunIssueAsync(CreateRequest(harness, timeoutMs: 120_000));

        Assert.False(result.Success);
        Assert.Equal("stall_timeout", result.ErrorCode);
        Assert.True(result.Duration < TimeSpan.FromSeconds(60), $"took {result.Duration}");
    }

    [Fact]
    public async Task RunIssueAsync_ShouldKillOnTurnTimeoutWhileEventsKeepFlowing()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var harness = CreateClaudeHarness("""
            $null = [Console]::In.ReadToEnd()
            @{ type = 'system'; subtype = 'init'; session_id = 'sess-1' } | ConvertTo-Json -Compress
            while ($true) {
                @{ type = 'system'; subtype = 'tick' } | ConvertTo-Json -Compress
                Start-Sleep -Milliseconds 300
            }
            """);

        var runner = new ClaudeAgentRunner(NullLogger<ClaudeAgentRunner>.Instance)
        {
            StallTimeoutMs = 30_000
        };

        var result = await runner.RunIssueAsync(CreateRequest(harness, timeoutMs: 3_000));

        Assert.False(result.Success);
        Assert.Equal("turn_timeout", result.ErrorCode);
        Assert.True(result.Duration < TimeSpan.FromSeconds(60), $"took {result.Duration}");
    }

    [Fact]
    public async Task RunIssueAsync_ShouldFailOnNonZeroExitWithoutResultEvent()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var harness = CreateClaudeHarness("""
            $null = [Console]::In.ReadToEnd()
            @{ type = 'system'; subtype = 'init'; session_id = 'sess-1' } | ConvertTo-Json -Compress
            [Console]::Error.WriteLine('claude exploded')
            exit 3
            """);

        var runner = new ClaudeAgentRunner(NullLogger<ClaudeAgentRunner>.Instance);

        var result = await runner.RunIssueAsync(CreateRequest(harness, timeoutMs: 120_000));

        Assert.False(result.Success);
        Assert.Equal("subprocess_exit", result.ErrorCode);
        Assert.Contains("claude exploded", result.Stderr, StringComparison.Ordinal);
    }

    private static AgentRunRequest CreateRequest(ClaudeHarness harness, int timeoutMs, string prompt = "test prompt")
    {
        return new AgentRunRequest(
            IssueId: "issue-1",
            IssueIdentifier: "#1",
            IssueTitle: "Issue #1",
            WorkspacePath: harness.WorkspacePath,
            Prompt: prompt,
            Command: harness.Command,
            TimeoutMs: timeoutMs,
            MaxTurns: 1,
            ApprovalPolicy: "never",
            ThreadSandbox: "danger-full-access",
            TurnSandboxPolicy: "danger-full-access",
            ReadTimeoutMs: 5_000);
    }

    private static ClaudeHarness CreateClaudeHarness(string script)
    {
        var workspacePath = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-claude-runner")).FullName;
        var scriptPath = Path.Combine(workspacePath, "fake-claude.ps1");
        var wrapperPath = Path.Combine(workspacePath, "fake-claude-wrapper.cmd");
        File.WriteAllText(scriptPath, script);
        // The wrapper deliberately ignores the runner-appended CLI flags
        // (-p --output-format ... --permission-mode ...); only the script matters.
        File.WriteAllText(wrapperPath, "@echo off\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"%~1\"\r\n");
        return new ClaudeHarness(workspacePath, $"call \"{wrapperPath}\" \"{scriptPath}\"");
    }

    private sealed class ClaudeHarness(string workspacePath, string command) : IDisposable
    {
        public string WorkspacePath { get; } = workspacePath;

        public string Command { get; } = command;

        public void Dispose()
        {
            try
            {
                Directory.Delete(WorkspacePath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
