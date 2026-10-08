using AIDatasetStudio.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AIDatasetStudio.Infrastructure.Persistence.Configurations;

internal sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        // A workspace file holds exactly one row here.
        builder.ToTable("Workspace");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Name).HasMaxLength(Workspace.MaxNameLength).IsRequired();
    }
}
