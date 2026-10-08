using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Settings;
using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Infrastructure.Analysis;
using AIDatasetStudio.Infrastructure.Catalog;
using AIDatasetStudio.Infrastructure.Export;
using AIDatasetStudio.Infrastructure.Import;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Records;
using AIDatasetStudio.Infrastructure.Settings;
using AIDatasetStudio.Infrastructure.Versions;
using AIDatasetStudio.Infrastructure.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIDatasetStudio.Infrastructure;

public static class DependencyInjection
{
    /// <param name="services">The service collection.</param>
    /// <param name="settingsDirectory">Folder for per-user settings; defaults to <see cref="UserSettingsLocation.GetDirectory"/>.</param>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string? settingsDirectory = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IUserSettings>(provider =>
            new SqliteUserSettings(settingsDirectory ?? UserSettingsLocation.GetDirectory(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<IWorkspaceService>(provider => provider.GetRequiredService<WorkspaceService>());
        services.AddSingleton<IWorkspaceDatabase>(provider => provider.GetRequiredService<WorkspaceService>());
        services.AddSingleton<ICatalogService, CatalogService>();
        services.AddSingleton<IRecordCommandService, RecordCommandService>();
        services.AddSingleton<IRecordQueryService, RecordQueryService>();
        services.AddSingleton<IImportService, ImportService>();
        services.AddSingleton<ITokenizerProvider, TiktokenProvider>();
        services.AddSingleton<IRecordAnalysisService, RecordAnalysisService>();
        services.AddSingleton<IExportService, ExportService>();
        services.AddSingleton<IVersionService, VersionService>();
        return services;
    }
}
