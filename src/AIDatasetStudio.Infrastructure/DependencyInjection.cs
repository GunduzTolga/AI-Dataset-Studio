using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Judgments;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Settings;
using AIDatasetStudio.Application.Tags;
using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Infrastructure.AI;
using AIDatasetStudio.Infrastructure.Analysis;
using AIDatasetStudio.Infrastructure.Catalog;
using AIDatasetStudio.Infrastructure.Documents;
using AIDatasetStudio.Infrastructure.Export;
using AIDatasetStudio.Infrastructure.Import;
using AIDatasetStudio.Infrastructure.Judgments;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Records;
using AIDatasetStudio.Infrastructure.Settings;
using AIDatasetStudio.Infrastructure.Settings.Secrets;
using AIDatasetStudio.Infrastructure.Tags;
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
        services.TryAddSingleton(_ => SecretStores.ForThisSystem());
        services.AddSingleton(provider => new SqliteUserSettings(
            settingsDirectory ?? UserSettingsLocation.GetDirectory(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ISecretStore>()));
        services.AddSingleton<IUserSettings>(provider => provider.GetRequiredService<SqliteUserSettings>());
        services.AddSingleton<IAIConnectionStore>(provider => provider.GetRequiredService<SqliteUserSettings>());
        services.TryAddSingleton<IAIProviderFactory, AIProviderFactory>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<IWorkspaceService>(provider => provider.GetRequiredService<WorkspaceService>());
        services.AddSingleton<IWorkspaceDatabase>(provider => provider.GetRequiredService<WorkspaceService>());
        services.AddSingleton<ICatalogService, CatalogService>();
        services.AddSingleton<IDatasetMergeService, DatasetMergeService>();
        services.AddSingleton<IRecordCommandService, RecordCommandService>();
        services.AddSingleton<IRecordQueryService, RecordQueryService>();
        services.AddSingleton<DocumentReader>();
        services.AddSingleton<IDocumentReader>(provider => provider.GetRequiredService<DocumentReader>());
        services.AddSingleton<IImportService, ImportService>();
        services.AddSingleton<ITokenizerProvider, TiktokenProvider>();
        services.AddSingleton<IRecordAnalysisService, RecordAnalysisService>();
        services.AddSingleton<IJudgmentService, JudgmentService>();
        services.AddSingleton<ISemanticIndex, SemanticIndex>();
        services.AddSingleton<IExportService, ExportService>();
        services.AddSingleton<IVersionService, VersionService>();
        services.AddSingleton<ITagService, TagService>();
        return services;
    }
}
