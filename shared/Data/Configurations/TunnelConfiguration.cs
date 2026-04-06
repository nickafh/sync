using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Api.Data.Configurations;

public class TunnelConfiguration : IEntityTypeConfiguration<Tunnel>
{
    public void Configure(EntityTypeBuilder<Tunnel> builder)
    {
        builder.ToTable("tunnels");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(e => e.FieldProfileId).HasColumnName("field_profile_id");
        builder.Property(e => e.StalePolicy).HasColumnName("stale_policy").IsRequired();
        builder.Property(e => e.StaleHoldDays).HasColumnName("stale_hold_days").IsRequired();
        builder.Property(e => e.PhotoSyncEnabled)
            .HasColumnName("photo_sync_enabled")
            .HasDefaultValue(true)
            .IsRequired();
        builder.Property(e => e.Status).HasColumnName("status").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

        builder.HasOne(e => e.FieldProfile)
            .WithMany(fp => fp.Tunnels)
            .HasForeignKey(e => e.FieldProfileId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(e => e.Status).HasDatabaseName("idx_tunnels_status");

        builder.HasData(
            new Tunnel { Id = 1, Name = "Buckhead", FieldProfileId = 1, StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, PhotoSyncEnabled = true, Status = TunnelStatus.Active },
            new Tunnel { Id = 2, Name = "North Atlanta", FieldProfileId = 1, StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, PhotoSyncEnabled = true, Status = TunnelStatus.Active },
            new Tunnel { Id = 3, Name = "Intown", FieldProfileId = 1, StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, PhotoSyncEnabled = true, Status = TunnelStatus.Active },
            new Tunnel { Id = 4, Name = "Blue Ridge", FieldProfileId = 1, StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, PhotoSyncEnabled = true, Status = TunnelStatus.Active },
            new Tunnel { Id = 5, Name = "Cobb", FieldProfileId = 1, StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, PhotoSyncEnabled = true, Status = TunnelStatus.Active },
            new Tunnel { Id = 6, Name = "Clayton", FieldProfileId = 1, StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, PhotoSyncEnabled = true, Status = TunnelStatus.Active }
        );
    }
}
