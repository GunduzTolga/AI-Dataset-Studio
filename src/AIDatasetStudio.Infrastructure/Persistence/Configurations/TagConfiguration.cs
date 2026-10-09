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
        // Uniqueness uses the folded key: NOCASE would treat "Şifre" and "şifre" as different names.
        builder.Property(t => t.NameKey).HasMaxLength(Tag.MaxNameLength).IsRequired();
        builder.Property(t => t.ColorHex).HasMaxLength(7).IsRequired();
        builder.HasIndex(t => t.NameKey).IsUnique();
        // At most one tag per Mod+1..5 shortcut.
        builder.HasIndex(t => t.Shortcut).IsUnique().HasFilter("Shortcut IS NOT NULL");
    }
}
