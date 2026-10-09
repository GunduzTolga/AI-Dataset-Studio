using AIDatasetStudio.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AIDatasetStudio.Infrastructure.Persistence.Configurations;

/// <remarks>
/// The version's records live in <c>VersionRecordMappings</c> and their contents in
/// <c>RecordRevisions</c>; both are created by the AddVersions migration and used through SQL
/// only (see Versions/VersionService.cs).
/// </remarks>
internal sealed class DatasetVersionConfiguration : IEntityTypeConfiguration<DatasetVersion>
{
    public void Configure(EntityTypeBuilder<DatasetVersion> builder)
    {
        builder.ToTable("DatasetVersions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Number).HasMaxLength(32).IsRequired();
        builder.Property(v => v.Description).HasMaxLength(DatasetVersion.MaxDescriptionLength);
        builder.Property(v => v.Tokenizer).HasMaxLength(100).IsRequired();

        builder.HasOne<Dataset>()
            .WithMany()
            .HasForeignKey(v => v.DatasetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(v => new { v.DatasetId, v.Number }).IsUnique();
    }
}
