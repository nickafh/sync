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
        builder.Property(e => e.SourceType).HasColumnName("source_type").IsRequired();
        builder.Property(e => e.SourceIdentifier).HasColumnName("source_identifier").HasMaxLength(500).IsRequired();
        builder.Property(e => e.SourceDisplayName).HasColumnName("source_display_name").HasMaxLength(200);
        builder.Property(e => e.SourceSmtpAddress).HasColumnName("source_smtp_address").HasMaxLength(320);
        builder.Property(e => e.SourceFilterPlain).HasColumnName("source_filter_plain").HasMaxLength(500);
        builder.Property(e => e.TargetScope).HasColumnName("target_scope").IsRequired();
        builder.Property(e => e.TargetUserFilter).HasColumnName("target_user_filter").HasColumnType("jsonb");
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
            new Tunnel { Id = 1, Name = "Buckhead", SourceType = SourceType.Ddg, SourceIdentifier = "buckhead-ddg@atlantafinehomes.com", SourceDisplayName = "Buckhead Office DDG", SourceSmtpAddress = "buckhead-ddg@atlantafinehomes.com", TargetScope = TargetScope.AllUsers, FieldProfileId = 1, StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, PhotoSyncEnabled = true, Status = TunnelStatus.Active },
            new Tunnel { Id = 2, Name = "North Atlanta", SourceType = SourceType.Ddg, SourceIdentifier = "northatlanta-ddg@atlantafinehomes.com", SourceDisplayName = "North Atlanta Office DDG", SourceSmtpAddress = "northatlanta-ddg@atlantafinehomes.com", TargetScope = TargetScope.AllUsers, FieldProfileId = 1, StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, PhotoSyncEnabled = true, Status = TunnelStatus.Active },
            new Tunnel { Id = 3, Name = "Intown", SourceType = SourceType.Ddg, SourceIdentifier = "intown-ddg@atlantafinehomes.com", SourceDisplayName = "Intown Office DDG", SourceSmtpAddress = "intown-ddg@atlantafinehomes.com", TargetScope = TargetScope.AllUsers, FieldProfileId = 1, StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, PhotoSyncEnabled = true, Status = TunnelStatus.Active },
            new Tunnel { Id = 4, Name = "Blue Ridge", SourceType = SourceType.Ddg, SourceIdentifier = "blueridge-ddg@atlantafinehomes.com", SourceDisplayName = "Blue Ridge Office DDG", SourceSmtpAddress = "blueridge-ddg@atlantafinehomes.com", TargetScope = TargetScope.AllUsers, FieldProfileId = 1, StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, PhotoSyncEnabled = true, Status = TunnelStatus.Active },
            new Tunnel { Id = 5, Name = "Cobb", SourceType = SourceType.Ddg, SourceIdentifier = "cobb-ddg@atlantafinehomes.com", SourceDisplayName = "Cobb Office DDG", SourceSmtpAddress = "cobb-ddg@atlantafinehomes.com", TargetScope = TargetScope.AllUsers, FieldProfileId = 1, StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, PhotoSyncEnabled = true, Status = TunnelStatus.Active },
            new Tunnel { Id = 6, Name = "Clayton", SourceType = SourceType.Ddg, SourceIdentifier = "clayton-ddg@atlantafinehomes.com", SourceDisplayName = "Clayton Office DDG", SourceSmtpAddress = "clayton-ddg@atlantafinehomes.com", TargetScope = TargetScope.AllUsers, FieldProfileId = 1, StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, PhotoSyncEnabled = true, Status = TunnelStatus.Active }
        );
    }
}
