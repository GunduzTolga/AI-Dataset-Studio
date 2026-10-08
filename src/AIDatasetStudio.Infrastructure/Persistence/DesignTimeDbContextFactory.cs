using Microsoft.EntityFrameworkCore.Design;

namespace AIDatasetStudio.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> to build the model when adding migrations.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WorkspaceDbContext>
{
    public WorkspaceDbContext CreateDbContext(string[] args) =>
        new(WorkspaceDatabase.BuildOptions(WorkspaceDatabase.BuildConnectionString("design-time.adstudio", allowCreate: true)));
}
