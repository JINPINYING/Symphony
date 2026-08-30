using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Agent.Claude;

// M4: headless Claude Code as an agent runner. One RunIssueAsync invocation is
// one bounded agentic session: the prompt is piped to `claude -p` on stdin, the
// stream-json event lines on stdout are mapped to AgentRunUpdate (so the existing
// persistence pipeline records session ids, messages, and token usage), and the
// process's terminal `result` event decides success.
//
// Extra CLI arguments (output format, permission mode, model) are appended here;
// request.Command carries only the base command from workflow config. The
// request's ApprovalPolicy/sandbox fields are Codex app-server concepts and are
// ignored; Claude's equivalent is the configured permission mode.
public sealed class ClaudeAgentRunner(ILogger<ClaudeAgentRunner> logger) : IAgentRunner
{
    private const int MaxCapturedOutputChars = 256_000;

    // Configured via the workflow's claude: section and injected by the resolver
    // through these mutable-per-request extras (kept on the request's Command and
    // the two fields below to avoid widening the shared AgentRunRequest contract).
    public string PermissionMode { get; init; } = "bypassPermissions";
    public string? Model { get; init; }
    public int StallTimeoutMs { get; init; } = 600_000;

    public async Task<AgentRunResult> RunIssueAsync(
        AgentRunRequest request,
        Func<AgentRunUpdate, CancellationToken, Task>? onUpdate = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new ArgumentException("Command must be non-empty.", nameof(request.Command));
        }

        if (string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            throw new ArgumentException("WorkspacePath must be non-empty.", nameof(request.WorkspacePath));
        }

        var stopwatch = Stopwatch.StartNew();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        string? sessionId = null;
        string? resultText = null;
        var resultIsError = false;
        string? resultSubtype = null;
        int? finalInputTokens = null;
        int? finalOutputTokens = null;

        var command = BuildCommandLine(request.Command);
        var startInfo = BuildProcessStartInfo(command, request.WorkspacePath);

        using var process = new Process();
        process.StartInfo = startInfo;

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCts.CancelAfter(request.TimeoutMs);

        var lastEventUtc = DateTimeOffset.UtcNow;

        try
        {
            if (!process.Start())
            {
                return Failure("subprocess_start", "Failed to start the claude process.", stopwatch.Elapsed);
            }

            await ReportAsync(onUpdate, new AgentRunUpdate(
                EventType: "claude_started",
                Timestamp: DateTimeOffset.UtcNow,
                CodexAppServerPid: process.Id,
                Message: $"claude headless run started for {request.IssueIdentifier}."), cancellationToken);

            // Prompt goes in on stdin so arbitrary length and quoting are safe.
            await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), overallCts.Token);
            process.StandardInput.Close();

            var stderrTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None)) is not null)
                {
                    AppendCapped(stderr, line);
                }
            }, CancellationToken.None);

            while (true)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                readCts.CancelAfter(StallTimeoutMs);

                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(readCts.Token);
                }
                catch (OperationCanceledException) when (!overallCts.Token.IsCancellationRequested)
                {
                    TryKill(process);
                    return Failure(
                        "stall_timeout",
                        $"No stream event for {StallTimeoutMs}ms (last event {lastEventUtc:u}).",
                        stopwatch.Elapsed);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    return Failure(
                        "turn_timeout",
                        $"claude run exceeded the {request.TimeoutMs}ms turn timeout.",
                        stopwatch.Elapsed);
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                lastEventUtc = DateTimeOffset.UtcNow;
                AppendCapped(stdout, line);

                var update = TryMapStreamEvent(line, ref sessionId, ref resultText, ref resultIsError, ref resultSubtype, ref finalInputTokens, ref finalOutputTokens);
                if (update is not null)
                {
                    await ReportAsync(onUpdate, update, cancellationToken);
                }
            }

            await process.WaitForExitAsync(overallCts.Token);
            await stderrTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return Failure(
                "turn_timeout",
                $"claude run exceeded the {request.TimeoutMs}ms turn timeout.",
                stopwatch.Elapsed);
        }

        stopwatch.Stop();

        var success = process.ExitCode == 0 && !resultIsError &&
                      (resultSubtype is null || string.Equals(resultSubtype, "success", StringComparison.OrdinalIgnoreCase));

        if (success)
        {
            await ReportAsync(onUpdate, new AgentRunUpdate(
                EventType: "turn_completed",
                Timestamp: DateTimeOffset.UtcNow,
                ThreadId: sessionId,
                TurnId: "final",
                Message: Truncate(resultText, 2_000),
                InputTokens: finalInputTokens,
                OutputTokens: finalOutputTokens,
                TotalTokens: finalInputTokens.HasValue || finalOutputTokens.HasValue
                    ? (finalInputTokens ?? 0) + (finalOutputTokens ?? 0)
                    : null,
                TokenUsageIsDelta: false), cancellationToken);

            return new AgentRunResult(
                Success: true,
                ExitCode: process.ExitCode,
                Stdout: stdout.ToString(),
                Stderr: stderr.ToString(),
                Duration: stopwatch.Elapsed);
        }

        var errorCode = resultSubtype is not null && !string.Equals(resultSubtype, "success", StringComparison.OrdinalIgnoreCase)
            ? $"claude_{resultSubtype}"
            : resultIsError
                ? "claude_result_error"
                : "subprocess_exit";
        logger.LogWarning(
            "claude run for {IssueIdentifier} failed: exit={ExitCode} subtype={Subtype} isError={IsError}",
            request.IssueIdentifier, process.ExitCode, resultSubtype, resultIsError);

        return new AgentRunResult(
            Success: false,
            ExitCode: process.ExitCode,
            Stdout: stdout.ToString(),
            Stderr: string.IsNullOrWhiteSpace(stderr.ToString()) ? Truncate(resultText, 2_000) ?? "claude run failed." : stderr.ToString(),
            Duration: stopwatch.Elapsed,
            ErrorCode: errorCode);
    }

    private string BuildCommandLine(string baseCommand)
    {
        var builder = new StringBuilder(baseCommand.Trim());
        builder.Append(" -p --output-format stream-json --verbose");
        builder.Append(" --permission-mode ").Append(PermissionMode);
        if (!string.IsNullOrWhiteSpace(Model))
        {
            builder.Append(" --model ").Append(Model);
        }

        return builder.ToString();
    }

    private AgentRunUpdate? TryMapStreamEvent(
        string line,
        ref string? sessionId,
        ref string? resultText,
        ref bool resultIsError,
        ref string? resultSubtype,
        ref int? finalInputTokens,
        ref int? finalOutputTokens)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return new AgentRunUpdate("malformed", DateTimeOffset.UtcNow, Message: Truncate(line, 300));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var type = GetString(root, "type") ?? "other";
            var subtype = GetString(root, "subtype");
            sessionId ??= GetString(root, "session_id");

            switch (type)
            {
                case "system":
                    return new AgentRunUpdate(
                        EventType: $"claude_system_{subtype ?? "event"}",
                        Timestamp: DateTimeOffset.UtcNow,
                        ThreadId: sessionId);
                case "assistant":
                case "user":
                    string? excerpt = null;
                    if (root.TryGetProperty("message", out var message) &&
                        message.ValueKind == JsonValueKind.Object &&
                        message.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.ValueKind == JsonValueKind.Object &&
                                string.Equals(GetString(block, "type"), "text", StringComparison.Ordinal))
                            {
                                excerpt = Truncate(GetString(block, "text"), 500);
                                break;
                            }
                        }
                    }

                    return new AgentRunUpdate(
                        EventType: $"claude_{type}",
                        Timestamp: DateTimeOffset.UtcNow,
                        ThreadId: sessionId,
                        Message: excerpt);
                case "result":
                    resultSubtype = subtype;
                    resultIsError = root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
                    resultText = GetString(root, "result");
                    if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        finalInputTokens = GetInt(usage, "input_tokens");
                        finalOutputTokens = GetInt(usage, "output_tokens");
                    }

                    return new AgentRunUpdate(
                        EventType: "claude_result",
                        Timestamp: DateTimeOffset.UtcNow,
                        ThreadId: sessionId,
                        Message: Truncate(resultText, 500));
                default:
                    return new AgentRunUpdate(
                        EventType: $"claude_{type}",
                        Timestamp: DateTimeOffset.UtcNow,
                        ThreadId: sessionId);
            }
        }
    }

    private static async Task ReportAsync(
        Func<AgentRunUpdate, CancellationToken, Task>? onUpdate,
        AgentRunUpdate update,
        CancellationToken cancellationToken)
    {
        if (onUpdate is not null)
        {
            await onUpdate(update, cancellationToken);
        }
    }

    private static ProcessStartInfo BuildProcessStartInfo(string command, string workspacePath)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workspacePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/d /s /c {command}";
        }
        else
        {
            startInfo.FileName = "/bin/bash";
            startInfo.Arguments = $"-lc \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort; the run already failed.
        }
    }

    private static void AppendCapped(StringBuilder builder, string line)
    {
        if (builder.Length >= MaxCapturedOutputChars)
        {
            return;
        }

        builder.AppendLine(line.Length + builder.Length > MaxCapturedOutputChars
            ? line[..Math.Max(0, MaxCapturedOutputChars - builder.Length)]
            : line);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..maxLength]}…";
    }

    private static AgentRunResult Failure(string code, string message, TimeSpan duration) =>
        new(Success: false, ExitCode: -1, Stdout: string.Empty, Stderr: message, Duration: duration, ErrorCode: code);

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;
}

// build-stamp: m4b-1788130263
