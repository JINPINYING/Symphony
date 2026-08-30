using Microsoft.Extensions.DependencyInjection;

namespace Symphony.Infrastructure.Agent.Claude;

public static class ServiceCollectionExtensions
{
    // ClaudeAgentRunner instances are created per dispatch by the agent-runner
    // resolver (their settings come from the live-reloadable workflow config), so
    // there is nothing to register here beyond a marker for future services.
    public static IServiceCollection AddSymphonyClaudeAgentRunner(this IServiceCollection services)
    {
        return services;
    }
}
