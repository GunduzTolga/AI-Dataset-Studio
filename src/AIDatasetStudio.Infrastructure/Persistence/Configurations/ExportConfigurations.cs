using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AIDatasetStudio.Infrastructure.Persistence.Configurations;

internal sealed class ExportProfileConfiguration : IEntityTypeConfiguration<ExportProfileRow>
{
    public void Configure(EntityTypeBuilder<ExportProfileRow> builder)
    {
        builder.ToTable("ExportProfiles");
        // One remembered profile per dataset for now; named profiles can add a key column later.
        builder.HasKey(p => p.DatasetId);
        builder.Property(p => p.Settings).IsRequired();
        builder.HasOne<Dataset>().WithOne().HasForeignKey<ExportProfileRow>(p => p.DatasetId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ExportHistoryConfiguration : IEntityTypeConfiguration<ExportHistoryRow>
{
    public void Configure(EntityTypeBuilder<ExportHistoryRow> builder)
    {
        builder.ToTable("ExportHistories");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Settings).IsRequired();
        builder.Property(h => h.Files).IsRequired();
        builder.HasOne<Dataset>().WithMany().HasForeignKey(h => h.DatasetId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(h => new { h.DatasetId, h.ExportedAt });
    }
}
