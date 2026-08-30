using Symphony.Core.Models;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public interface IIssueExecutionCoordinator
{
    Task<bool> TryStartAsync(IssueExecutionRequest request, CancellationToken cancellationToken = default);

    Task<bool> TryStopAsync(string issueId, CancellationToken cancellationToken = default);
}

public sealed record IssueExecutionRequest(
    string RunId,
    string AttemptId,
    string InstanceId,
    int? Attempt,
    NormalizedIssue Issue,
    WorkflowDefinition WorkflowDefinition,
    // M3: a command-center directive that authorized this dispatch. Appended to
    // the rendered prompt so the worker executes with the owner's instructions.
    string? DirectiveInstructions = null,
    string? DirectiveAction = null,
    string? DirectivePhase = null);
