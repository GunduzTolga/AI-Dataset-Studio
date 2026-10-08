using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AIDatasetStudio.Infrastructure.Persistence.Configurations;

internal sealed class DatasetRecordConfiguration : IEntityTypeConfiguration<DatasetRecord>
{
    public void Configure(EntityTypeBuilder<DatasetRecord> builder)
    {
        builder.ToTable("DatasetRecords");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Payload)
            .HasColumnName("DataPayload")
            .HasConversion(new PayloadConverter(), new PayloadComparer())
            .IsRequired();

        builder.Property(r => r.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Preview).HasMaxLength(DatasetRecord.PreviewMaxLength).IsRequired();
        builder.Property(r => r.MetadataJson).IsRequired();

        builder.ComplexProperty(r => r.Lineage, lineage =>
        {
            lineage.Property(l => l.SourceKind).HasColumnName("SourceKind");
            lineage.Property(l => l.SourceReference).HasColumnName("SourceReference");
            lineage.Property(l => l.CreatedBy).HasColumnName("CreatedBy");
            lineage.Property(l => l.GeneratedByModel).HasColumnName("GeneratedByModel");
        });

        builder.HasOne<Dataset>()
            .WithMany()
            .HasForeignKey(r => r.DatasetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(r => r.Tags)
            .WithMany()
            .UsingEntity(
                "RecordTags",
                right => right.HasOne(typeof(Tag)).WithMany().HasForeignKey("TagId").OnDelete(DeleteBehavior.Cascade),
                left => left.HasOne(typeof(DatasetRecord)).WithMany().HasForeignKey("RecordId").OnDelete(DeleteBehavior.Cascade),
                join => join.HasKey("RecordId", "TagId"));
        builder.Navigation(r => r.Tags).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(r => new { r.DatasetId, r.Ordinal });
        builder.HasIndex(r => new { r.DatasetId, r.Status });
        builder.HasIndex(r => new { r.DatasetId, r.ContentHash });
    }
}
