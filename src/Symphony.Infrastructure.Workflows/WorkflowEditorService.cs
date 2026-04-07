using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Infrastructure.Workflows;

public sealed partial class WorkflowEditorService(
    WorkflowLoader loader,
    IOptions<WorkflowLoaderOptions> options)
{
    public const string TrackerApiKeyPlaceholder = "__SYMPHONY_KEEP_EXISTING_SECRET__";

    public async Task<WorkflowEditorDocument> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var workflowPath = WorkflowPathResolver.Resolve(options.Value.Path);
        var rawContent = await ReadWorkflowTextAsync(workflowPath, cancellationToken);
        var documentText = WorkflowEditorTextDocument.Parse(rawContent);

        string frontMatterText = documentText.FrontMatterText;
        var rawTrackerApiKey = TryGetTrackerApiKey(frontMatterText);
        var hasMaskedTrackerApiKey = !string.IsNullOrWhiteSpace(rawTrackerApiKey) && !IsEnvironmentReference(rawTrackerApiKey);
        if (hasMaskedTrackerApiKey)
        {
            frontMatterText = ReplaceTrackerApiKey(frontMatterText, TrackerApiKeyPlaceholder);
        }

        try
        {
            var definition = await loader.LoadAsync(workflowPath, cancellationToken);
            return new WorkflowEditorDocument(
                workflowPath,
                definition.LoadedAtUtc,
                frontMatterText,
                definition.PromptTemplate,
                hasMaskedTrackerApiKey,
                TrackerApiKeyPlaceholder,
                ValidationError: null);
        }
        catch (WorkflowLoadException ex)
        {
            return new WorkflowEditorDocument(
                workflowPath,
                LoadedAtUtc: null,
                frontMatterText,
                documentText.PromptTemplate,
                hasMaskedTrackerApiKey,
                TrackerApiKeyPlaceholder,
                new WorkflowEditorValidationError(ex.Code, ex.Message));
        }
    }

    public async Task<WorkflowEditorDocument> SaveAsync(
        WorkflowEditorDocument document,
        CancellationToken cancellationToken = default)
    {
        var workflowPath = WorkflowPathResolver.Resolve(options.Value.Path);
        var existingRawContent = await ReadWorkflowTextAsync(workflowPath, cancellationToken);
        var existingDocumentText = WorkflowEditorTextDocument.Parse(existingRawContent);
        var existingTrackerApiKey = TryGetTrackerApiKey(existingDocumentText.FrontMatterText);

        var frontMatterText = NormalizeLineEndings(document.FrontMatterText);
        if (!string.IsNullOrWhiteSpace(existingTrackerApiKey) &&
            !IsEnvironmentReference(existingTrackerApiKey) &&
            frontMatterText.Contains(TrackerApiKeyPlaceholder, StringComparison.Ordinal))
        {
            frontMatterText = ReplaceTrackerApiKey(frontMatterText, existingTrackerApiKey);
        }

        var promptTemplate = NormalizeLineEndings(document.PromptTemplate);
        var updatedContent = WorkflowEditorTextDocument.Compose(frontMatterText, promptTemplate);

        var workflowDirectory = Path.GetDirectoryName(workflowPath);
        if (string.IsNullOrWhiteSpace(workflowDirectory))
        {
            workflowDirectory = Directory.GetCurrentDirectory();
        }

        Directory.CreateDirectory(workflowDirectory);

        var tempPath = Path.Combine(
            workflowDirectory,
            $".{Path.GetFileName(workflowPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempPath, updatedContent, cancellationToken);
            await loader.LoadAsync(tempPath, cancellationToken);
            File.Move(tempPath, workflowPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        return await GetCurrentAsync(cancellationToken);
    }

    private static async Task<string> ReadWorkflowTextAsync(string workflowPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(workflowPath))
        {
            throw new WorkflowLoadException("missing_workflow_file", $"Workflow file was not found at '{workflowPath}'.");
        }

        try
        {
            return await File.ReadAllTextAsync(workflowPath, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new WorkflowLoadException("missing_workflow_file", $"Workflow file could not be read at '{workflowPath}'.", ex);
        }
    }

    private static string NormalizeLineEndings(string? value)
    {
        return (value ?? string.Empty).Replace("\r\n", "\n");
    }

    private static string? TryGetTrackerApiKey(string frontMatterText)
    {
        var match = TrackerApiKeyLineRegex().Match(frontMatterText);
        return match.Success
            ? match.Groups["value"].Value.Trim()
            : null;
    }

    private static string ReplaceTrackerApiKey(string frontMatterText, string replacementValue)
    {
        return TrackerApiKeyLineRegex().Replace(
            frontMatterText,
            match => $"{match.Groups["prefix"].Value}{replacementValue}",
            count: 1);
    }

    private static bool IsEnvironmentReference(string rawValue)
    {
        var trimmed = rawValue.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed.StartsWith("$", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^(?<prefix>\s*api_key\s*:\s*)(?<value>.*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex TrackerApiKeyLineRegex();

    internal sealed record WorkflowEditorTextDocument(
        string FrontMatterText,
        string PromptTemplate)
    {
        public static WorkflowEditorTextDocument Parse(string rawContent)
        {
            var normalized = NormalizeLineEndings(rawContent);
            if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            {
                return new WorkflowEditorTextDocument(string.Empty, normalized.Trim());
            }

            var lines = normalized.Split('\n');
            var closingIndex = -1;
            for (var index = 1; index < lines.Length; index++)
            {
                if (lines[index].Trim().Equals("---", StringComparison.Ordinal))
                {
                    closingIndex = index;
                    break;
                }
            }

            if (closingIndex < 0)
            {
                return new WorkflowEditorTextDocument(
                    string.Join('\n', lines[1..]).TrimEnd(),
                    string.Empty);
            }

            var frontMatterText = string.Join('\n', lines[1..closingIndex]).TrimEnd();
            var promptTemplate = closingIndex + 1 >= lines.Length
                ? string.Empty
                : string.Join('\n', lines[(closingIndex + 1)..]).Trim();

            return new WorkflowEditorTextDocument(frontMatterText, promptTemplate);
        }

        public static string Compose(string frontMatterText, string promptTemplate)
        {
            var normalizedFrontMatter = NormalizeLineEndings(frontMatterText).Trim();
            var normalizedPrompt = NormalizeLineEndings(promptTemplate).Trim();
            if (string.IsNullOrWhiteSpace(normalizedFrontMatter))
            {
                return string.IsNullOrWhiteSpace(normalizedPrompt)
                    ? string.Empty
                    : $"{normalizedPrompt}\n";
            }

            return string.IsNullOrWhiteSpace(normalizedPrompt)
                ? $"---\n{normalizedFrontMatter}\n---\n"
                : $"---\n{normalizedFrontMatter}\n---\n\n{normalizedPrompt}\n";
        }
    }
}
