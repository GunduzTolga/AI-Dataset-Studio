using AIDatasetStudio.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AIDatasetStudio.Infrastructure.Persistence.Configurations;

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("Tags");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(Tag.MaxNameLength).UseCollation("NOCASE").IsRequired();
        builder.Property(t => t.ColorHex).HasMaxLength(7).IsRequired();
        builder.HasIndex(t => t.Name).IsUnique();
    }
}
