namespace Symphony.Infrastructure.Workflows;

public sealed record WorkflowEditorDocument(
    string SourcePath,
    DateTimeOffset? LoadedAtUtc,
    string FrontMatterText,
    string PromptTemplate,
    bool HasMaskedTrackerApiKey,
    string TrackerApiKeyPlaceholder,
    WorkflowEditorValidationError? ValidationError);

public sealed record WorkflowEditorValidationError(
    string Code,
    string Message);
