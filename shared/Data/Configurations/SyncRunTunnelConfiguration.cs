using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class SyncRunTunnelConfiguration : IEntityTypeConfiguration<SyncRunTunnel>
{
    public void Configure(EntityTypeBuilder<SyncRunTunnel> builder)
    {
        builder.ToTable("sync_run_tunnels");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.SyncRunId).HasColumnName("sync_run_id").IsRequired();
        builder.Property(e => e.TunnelId).HasColumnName("tunnel_id");
        builder.Property(e => e.TunnelName).HasColumnName("tunnel_name").HasMaxLength(100).IsRequired();
        builder.Property(e => e.Status).HasColumnName("status").IsRequired();
        builder.Property(e => e.TargetsCount).HasColumnName("targets_count").HasDefaultValue(0);
        builder.Property(e => e.ContactsCreated).HasColumnName("contacts_created").HasDefaultValue(0);
        builder.Property(e => e.ContactsUpdated).HasColumnName("contacts_updated").HasDefaultValue(0);
        builder.Property(e => e.ContactsRemoved).HasColumnName("contacts_removed").HasDefaultValue(0);
        builder.Property(e => e.ContactsSkipped).HasColumnName("contacts_skipped").HasDefaultValue(0);
        builder.Property(e => e.ContactsFailed).HasColumnName("contacts_failed").HasDefaultValue(0);
        builder.Property(e => e.ErrorSummary).HasColumnName("error_summary");
        builder.Property(e => e.StartedAt).HasColumnName("started_at").IsRequired();
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at").IsRequired();

        builder.HasOne(e => e.SyncRun)
            .WithMany(r => r.TunnelRecords)
            .HasForeignKey(e => e.SyncRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Tunnel)
            .WithMany()
            .HasForeignKey(e => e.TunnelId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(e => e.SyncRunId).HasDatabaseName("idx_sync_run_tunnels_run");
        builder.HasIndex(e => new { e.TunnelId, e.CompletedAt })
            .HasDatabaseName("idx_sync_run_tunnels_tunnel_completed")
            .IsDescending(false, true);
    }
}
