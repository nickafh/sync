using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> builder)
    {
        builder.ToTable("sync_runs");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.RunType).HasColumnName("run_type").IsRequired();
        builder.Property(e => e.Status).HasColumnName("status").IsRequired();
        builder.Property(e => e.IsDryRun).HasColumnName("is_dry_run").HasDefaultValue(false);
        builder.Property(e => e.StartedAt).HasColumnName("started_at");
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");
        builder.Property(e => e.DurationMs).HasColumnName("duration_ms");
        builder.Property(e => e.TunnelsProcessed).HasColumnName("tunnels_processed").HasDefaultValue(0);
        builder.Property(e => e.TunnelsWarned).HasColumnName("tunnels_warned").HasDefaultValue(0);
        builder.Property(e => e.TunnelsFailed).HasColumnName("tunnels_failed").HasDefaultValue(0);
        builder.Property(e => e.ContactsCreated).HasColumnName("contacts_created").HasDefaultValue(0);
        builder.Property(e => e.ContactsUpdated).HasColumnName("contacts_updated").HasDefaultValue(0);
        builder.Property(e => e.ContactsRemoved).HasColumnName("contacts_removed").HasDefaultValue(0);
        builder.Property(e => e.ContactsSkipped).HasColumnName("contacts_skipped").HasDefaultValue(0);
        builder.Property(e => e.ContactsFailed).HasColumnName("contacts_failed").HasDefaultValue(0);
        builder.Property(e => e.PhotosUpdated).HasColumnName("photos_updated").HasDefaultValue(0);
        builder.Property(e => e.PhotosFailed).HasColumnName("photos_failed").HasDefaultValue(0);
        builder.Property(e => e.ThrottleEvents).HasColumnName("throttle_events").HasDefaultValue(0);
        builder.Property(e => e.ErrorSummary).HasColumnName("error_summary");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");

        builder.HasIndex(e => e.Status).HasDatabaseName("idx_sync_runs_status");
        builder.HasIndex(e => e.StartedAt).HasDatabaseName("idx_sync_runs_started").IsDescending();
    }
}
