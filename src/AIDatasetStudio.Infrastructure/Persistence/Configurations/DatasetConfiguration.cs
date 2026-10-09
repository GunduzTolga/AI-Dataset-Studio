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
        // Datasets created before context lengths existed get the default.
        builder.Property(d => d.ContextLength).HasDefaultValue(Dataset.DefaultContextLength);
        builder.Property(d => d.AIGuidance).HasMaxLength(Dataset.MaxAIGuidanceLength);

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(d => d.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
