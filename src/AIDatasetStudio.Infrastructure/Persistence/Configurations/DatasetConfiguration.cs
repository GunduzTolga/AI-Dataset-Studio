using AIDatasetStudio.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AIDatasetStudio.Infrastructure.Persistence.Configurations;

internal sealed class DatasetConfiguration : IEntityTypeConfiguration<Dataset>
{
    public void Configure(EntityTypeBuilder<Dataset> builder)
    {
        builder.ToTable("Datasets");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Name).HasMaxLength(Dataset.MaxNameLength).IsRequired();
        builder.Property(d => d.DefaultTokenizer).HasMaxLength(100).IsRequired();

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(d => d.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
