using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class ContactSyncStateConfiguration : IEntityTypeConfiguration<ContactSyncState>
{
    public void Configure(EntityTypeBuilder<ContactSyncState> builder)
    {
        builder.ToTable("contact_sync_state");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.SourceUserId).HasColumnName("source_user_id").IsRequired();
        builder.Property(e => e.PhoneListId).HasColumnName("phone_list_id").IsRequired();
        builder.Property(e => e.TargetMailboxId).HasColumnName("target_mailbox_id").IsRequired();
        builder.Property(e => e.TunnelId).HasColumnName("tunnel_id");
        builder.Property(e => e.GraphContactId).HasColumnName("graph_contact_id").HasMaxLength(200);
        builder.Property(e => e.DataHash).HasColumnName("data_hash").HasMaxLength(64);
        builder.Property(e => e.PhotoHash).HasColumnName("photo_hash").HasMaxLength(64);
        builder.Property(e => e.PreviousDataHash).HasColumnName("previous_data_hash").HasMaxLength(64);
        builder.Property(e => e.PreviousPhotoHash).HasColumnName("previous_photo_hash").HasMaxLength(64);
        builder.Property(e => e.IsStale).HasColumnName("is_stale").HasDefaultValue(false);
        builder.Property(e => e.StaleDetectedAt).HasColumnName("stale_detected_at");
        builder.Property(e => e.LastSyncedAt).HasColumnName("last_synced_at");
        builder.Property(e => e.LastResult).HasColumnName("last_result").HasMaxLength(50);
        builder.Property(e => e.LastError).HasColumnName("last_error");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

        builder.HasIndex(e => new { e.SourceUserId, e.PhoneListId, e.TargetMailboxId }).IsUnique();

        builder.HasOne(e => e.SourceUser)
            .WithMany()
            .HasForeignKey(e => e.SourceUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.PhoneList)
            .WithMany()
            .HasForeignKey(e => e.PhoneListId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.TargetMailbox)
            .WithMany()
            .HasForeignKey(e => e.TargetMailboxId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Tunnel)
            .WithMany()
            .HasForeignKey(e => e.TunnelId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(e => e.SourceUserId).HasDatabaseName("idx_contact_sync_state_source");
        builder.HasIndex(e => e.TargetMailboxId).HasDatabaseName("idx_contact_sync_state_target");
        builder.HasIndex(e => e.PhoneListId).HasDatabaseName("idx_contact_sync_state_list");
        builder.HasIndex(e => e.IsStale).HasDatabaseName("idx_contact_sync_state_stale").HasFilter("is_stale = TRUE");
        builder.HasIndex(e => new { e.SourceUserId, e.PhoneListId, e.TargetMailboxId }).HasDatabaseName("idx_contact_sync_state_composite");

        // Hot-path index for ProcessMailboxAsync: loads all sync state for a tunnel+mailbox
        // filtered by phone list IDs. Covers the WHERE clause exactly.
        builder.HasIndex(e => new { e.TunnelId, e.TargetMailboxId, e.PhoneListId })
            .HasDatabaseName("idx_contact_sync_state_tunnel_mailbox_list");
    }
}
