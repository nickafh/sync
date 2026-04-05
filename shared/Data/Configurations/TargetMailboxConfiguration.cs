using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class TargetMailboxConfiguration : IEntityTypeConfiguration<TargetMailbox>
{
    public void Configure(EntityTypeBuilder<TargetMailbox> builder)
    {
        builder.ToTable("target_mailboxes");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.EntraId).HasColumnName("entra_id").HasMaxLength(100).IsRequired();
        builder.Property(e => e.Email).HasColumnName("email").HasMaxLength(300).IsRequired();
        builder.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(200);
        builder.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.LastVerifiedAt).HasColumnName("last_verified_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

        builder.HasIndex(e => e.EntraId).IsUnique().HasDatabaseName("idx_target_mailboxes_entra");
    }
}
