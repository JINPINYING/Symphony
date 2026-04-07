using Symphony.Host.Setup;

namespace Symphony.Integration.Tests;

public sealed class CodexCliPreflightEvaluatorTests
{
    [Fact]
    public async Task CheckAsync_ShouldAcceptReadyCodexCli()
    {
        var codexHome = CreateTempDirectory("codex-home");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(codexHome, "auth.json"), CreateLoginAuthJson());

            var result = await CodexCliPreflightEvaluator.CheckAsync(
                CreateRunner(new Dictionary<string, CodexCliCommandResult>(StringComparer.Ordinal)
                {
                    ["codex --version"] = new(0, "codex-cli 0.114.0", string.Empty),
                    ["npm view @openai/codex version"] = new(0, "0.114.0", string.Empty)
                }),
                codexHome,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.True(result.IsReadyToStart);
            Assert.Equal("0.114.0", result.InstalledVersion);
            Assert.Equal("0.114.0", result.LatestVersion);
            Assert.True(result.LatestVersionVerified);
            Assert.True(result.HasAuthJson);
            Assert.True(result.AuthenticationConfigured);
            Assert.Equal("chatgpt", result.AuthenticationMode);
        }
        finally
        {
            TryDeleteDirectory(codexHome);
        }
    }

    [Fact]
    public async Task CheckAsync_ShouldAcceptApiKeyAuthFile()
    {
        var codexHome = CreateTempDirectory("codex-home");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(codexHome, "auth.json"), CreateApiKeyAuthJson());

            var result = await CodexCliPreflightEvaluator.CheckAsync(
                CreateRunner(new Dictionary<string, CodexCliCommandResult>(StringComparer.Ordinal)
                {
                    ["codex --version"] = new(0, "codex-cli 0.114.0", string.Empty),
                    ["npm view @openai/codex version"] = new(0, "0.114.0", string.Empty)
                }),
                codexHome,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.True(result.IsReadyToStart);
            Assert.True(result.AuthenticationConfigured);
            Assert.Equal("api_key", result.AuthenticationMode);
        }
        finally
        {
            TryDeleteDirectory(codexHome);
        }
    }

    [Fact]
    public async Task CheckAsync_ShouldBlockWhenInstalledVersionIsBehindValidatedVersion()
    {
        var codexHome = CreateTempDirectory("codex-home");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(codexHome, "auth.json"), CreateLoginAuthJson());

            var result = await CodexCliPreflightEvaluator.CheckAsync(
                CreateRunner(new Dictionary<string, CodexCliCommandResult>(StringComparer.Ordinal)
                {
                    ["codex --version"] = new(0, "codex-cli 0.113.0", string.Empty),
                    ["npm view @openai/codex version"] = new(0, "0.114.0", string.Empty)
                }),
                codexHome,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.False(result.IsReadyToStart);
            Assert.Contains(
                result.BlockingIssues,
                issue => issue.Contains("Symphony-validated version 0.114.0", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(codexHome);
        }
    }

    [Fact]
    public async Task CheckAsync_ShouldBlockWhenAuthJsonIsMissing()
    {
        var codexHome = CreateTempDirectory("codex-home");

        try
        {
            var result = await CodexCliPreflightEvaluator.CheckAsync(
                CreateRunner(new Dictionary<string, CodexCliCommandResult>(StringComparer.Ordinal)
                {
                    ["codex --version"] = new(0, "codex-cli 0.114.0", string.Empty),
                    ["npm view @openai/codex version"] = new(0, "0.114.0", string.Empty)
                }),
                codexHome,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.False(result.IsReadyToStart);
            Assert.Contains(
                result.BlockingIssues,
                issue => issue.Contains("auth file is missing", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(codexHome);
        }
    }

    [Fact]
    public async Task CheckAsync_ShouldFallbackToLocalVersionCacheWhenNpmLookupFails()
    {
        var codexHome = CreateTempDirectory("codex-home");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(codexHome, "auth.json"), CreateLoginAuthJson());
            await File.WriteAllTextAsync(
                Path.Combine(codexHome, "version.json"),
                """
                {
                  "latest_version": "0.114.0"
                }
                """);

            var result = await CodexCliPreflightEvaluator.CheckAsync(
                CreateRunner(new Dictionary<string, CodexCliCommandResult>(StringComparer.Ordinal)
                {
                    ["codex --version"] = new(0, "codex-cli 0.114.0", string.Empty),
                    ["npm view @openai/codex version"] = new(1, string.Empty, "npm unavailable")
                }),
                codexHome,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.True(result.IsReadyToStart);
            Assert.Equal("cache", result.LatestVersionSource);
            Assert.False(result.LatestVersionVerified);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("local Codex version cache", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(codexHome);
        }
    }

    [Fact]
    public async Task CheckAsync_ShouldBlockWhenAuthJsonHasNoUsableCredentials()
    {
        var codexHome = CreateTempDirectory("codex-home");

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(codexHome, "auth.json"),
                """
                {
                  "auth_mode": "chatgpt",
                  "tokens": {}
                }
                """);

            var result = await CodexCliPreflightEvaluator.CheckAsync(
                CreateRunner(new Dictionary<string, CodexCliCommandResult>(StringComparer.Ordinal)
                {
                    ["codex --version"] = new(0, "codex-cli 0.114.0", string.Empty),
                    ["npm view @openai/codex version"] = new(0, "0.114.0", string.Empty)
                }),
                codexHome,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.False(result.IsReadyToStart);
            Assert.Contains(
                result.BlockingIssues,
                issue => issue.Contains("usable authentication record", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("reusable login token set or API key", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(codexHome);
        }
    }

    [Fact]
    public async Task CheckAsync_ShouldPropagateCallerCancellation()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CodexCliPreflightEvaluator.CheckAsync(
                (_, token) => Task.FromCanceled<CodexCliCommandResult>(token),
                Path.Combine(Path.GetTempPath(), $"symphony-codex-home-{Guid.NewGuid():N}"),
                TimeSpan.FromSeconds(1),
                cancellationTokenSource.Token));
    }

    [Fact]
    public async Task CheckAsync_ShouldTreatTimedOutNpmProbeAsUnverifiedLatestVersion()
    {
        var codexHome = CreateTempDirectory("codex-home");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(codexHome, "auth.json"), CreateLoginAuthJson());
            await File.WriteAllTextAsync(
                Path.Combine(codexHome, "version.json"),
                """
                {
                  "latest_version": "0.114.0"
                }
                """);

            var result = await CodexCliPreflightEvaluator.CheckAsync(
                (command, token) => command.Equals("npm view @openai/codex version", StringComparison.Ordinal)
                    ? WaitForCancellationAsync(token)
                    : Task.FromResult(command switch
                    {
                        "codex --version" => new CodexCliCommandResult(0, "codex-cli 0.114.0", string.Empty),
                        _ => throw new InvalidOperationException($"Unexpected command '{command}'.")
                    }),
                codexHome,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None);

            Assert.True(result.IsReadyToStart);
            Assert.Equal("cache", result.LatestVersionSource);
            Assert.False(result.LatestVersionVerified);
        }
        finally
        {
            TryDeleteDirectory(codexHome);
        }
    }

    private static Func<string, CancellationToken, Task<CodexCliCommandResult>> CreateRunner(
        IReadOnlyDictionary<string, CodexCliCommandResult> responses)
    {
        return (command, _) =>
        {
            if (!responses.TryGetValue(command, out var response))
            {
                throw new InvalidOperationException($"No fake response configured for '{command}'.");
            }

            return Task.FromResult(response);
        };
    }

    private static string CreateLoginAuthJson()
    {
        return """
            {
              "auth_mode": "chatgpt",
              "tokens": {
                "id_token": "id-token",
                "access_token": "access-token",
                "refresh_token": "refresh-token",
                "account_id": "account-id"
              },
              "last_refresh": "2026-04-08T00:00:00Z"
            }
            """;
    }

    private static string CreateApiKeyAuthJson()
    {
        return """
            {
              "OPENAI_API_KEY": "sk-test",
              "last_refresh": "2026-04-08T00:00:00Z"
            }
            """;
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"symphony-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<CodexCliCommandResult> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Expected cancellation before completion.");
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
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
