using Microsoft.Extensions.DependencyInjection;
using Symphony.Core.Abstractions;

namespace Symphony.Infrastructure.Tracker.GitHub;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSymphonyGitHubTrackerClient(this IServiceCollection services)
    {
        services
            .AddHttpClient<GitHubTrackerClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            // Constructed explicitly rather than through activation, so the budget
            // observer is visibly wired rather than resolved by convention. A host
            // that registers none gets null and the adapter simply records nothing.
            .AddTypedClient((httpClient, provider) =>
                new GitHubTrackerClient(httpClient, provider.GetService<IGitHubRateLimitObserver>()));
        services.AddScoped<ITrackerClient>(provider => provider.GetRequiredService<GitHubTrackerClient>());
        services.AddScoped<IGitHubTrackerClient>(provider => provider.GetRequiredService<GitHubTrackerClient>());

        return services;
    }
}
