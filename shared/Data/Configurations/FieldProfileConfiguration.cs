using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class FieldProfileConfiguration : IEntityTypeConfiguration<FieldProfile>
{
    public void Configure(EntityTypeBuilder<FieldProfile> builder)
    {
        builder.ToTable("field_profiles");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(e => e.Description).HasColumnName("description");
        builder.Property(e => e.IsDefault).HasColumnName("is_default").HasDefaultValue(false);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

        builder.HasData(
            new FieldProfile { Id = 1, Name = "Default", Description = "Standard field sync profile for all office tunnels", IsDefault = true }
        );
    }
}
