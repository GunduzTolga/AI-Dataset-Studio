using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AIDatasetStudio.Infrastructure.Persistence.Configurations;

internal sealed class RecordJudgmentConfiguration : IEntityTypeConfiguration<RecordJudgmentRow>
{
    public const int MaxReasonLength = 2_000;

    public void Configure(EntityTypeBuilder<RecordJudgmentRow> builder)
    {
        builder.ToTable("RecordJudgments");
        builder.HasKey(j => j.RecordId);
        builder.Property(j => j.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(j => j.Reason).HasMaxLength(MaxReasonLength).IsRequired();
        builder.Property(j => j.Model).HasMaxLength(200).IsRequired();

        // Judgments go with their record (and so with their dataset).
        builder.HasOne<DatasetRecord>()
            .WithOne()
            .HasForeignKey<RecordJudgmentRow>(j => j.RecordId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(j => new { j.DatasetId, j.Score });
    }
}

internal sealed class RecordConflictConfiguration : IEntityTypeConfiguration<RecordConflictRow>
{
    public void Configure(EntityTypeBuilder<RecordConflictRow> builder)
    {
        builder.ToTable("RecordConflicts");
        builder.HasKey(c => new { c.RecordId, c.OtherId });
        builder.Property(c => c.RecordHash).HasMaxLength(64).IsRequired();
        builder.Property(c => c.OtherHash).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Reason).HasMaxLength(RecordJudgmentConfiguration.MaxReasonLength).IsRequired();
        builder.Property(c => c.Model).HasMaxLength(200).IsRequired();

        // A comparison goes when either record goes.
        builder.HasOne<DatasetRecord>()
            .WithMany()
            .HasForeignKey(c => c.RecordId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DatasetRecord>()
            .WithMany()
            .HasForeignKey(c => c.OtherId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => c.OtherId);
        builder.HasIndex(c => c.DatasetId);
    }
}
