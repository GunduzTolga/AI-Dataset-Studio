using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AIDatasetStudio.Infrastructure.Persistence.Configurations;

internal sealed class RecordAnalysisConfiguration : IEntityTypeConfiguration<RecordAnalysisRow>
{
    public void Configure(EntityTypeBuilder<RecordAnalysisRow> builder)
    {
        builder.ToTable("RecordAnalyses");
        builder.HasKey(a => a.RecordId);
        builder.Property(a => a.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Tokenizer).HasMaxLength(100).IsRequired();
        builder.Property(a => a.QuestionKey).HasMaxLength(32);
        builder.Property(a => a.Issues).IsRequired();

        // Analyses go with their record (and so with their dataset).
        builder.HasOne<DatasetRecord>()
            .WithOne()
            .HasForeignKey<RecordAnalysisRow>(a => a.RecordId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.DatasetId, a.QuestionKey });
    }
}

internal sealed class WorkspaceSettingConfiguration : IEntityTypeConfiguration<WorkspaceSetting>
{
    public void Configure(EntityTypeBuilder<WorkspaceSetting> builder)
    {
        builder.ToTable("WorkspaceSettings");
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(100);
        builder.Property(s => s.Value).IsRequired();
    }
}
