using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Infrastructure.Persistence.Converters;
using AIDatasetStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AIDatasetStudio.Infrastructure.Persistence;

/// <summary>
/// EF Core context over a single workspace file.
/// </summary>
public sealed class WorkspaceDbContext(DbContextOptions<WorkspaceDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspace => Set<Workspace>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Dataset> Datasets => Set<Dataset>();

    public DbSet<DatasetRecord> Records => Set<DatasetRecord>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<DatasetVersion> Versions => Set<DatasetVersion>();

    internal DbSet<RecordAnalysisRow> Analyses => Set<RecordAnalysisRow>();

    internal DbSet<WorkspaceSetting> Settings => Set<WorkspaceSetting>();

    internal DbSet<ExportProfileRow> ExportProfiles => Set<ExportProfileRow>();

    internal DbSet<ExportHistoryRow> ExportHistories => Set<ExportHistoryRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkspaceDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();
}
