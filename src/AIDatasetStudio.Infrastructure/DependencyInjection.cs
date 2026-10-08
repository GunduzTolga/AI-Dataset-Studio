using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Infrastructure.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIDatasetStudio.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<IWorkspaceService>(provider => provider.GetRequiredService<WorkspaceService>());
        return services;
    }
}
