using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Api.Data.Configurations;

public class SyncRunItemConfiguration : IEntityTypeConfiguration<SyncRunItem>
{
    public void Configure(EntityTypeBuilder<SyncRunItem> builder)
    {
        builder.ToTable("sync_run_items");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.SyncRunId).HasColumnName("sync_run_id").IsRequired();
        builder.Property(e => e.TunnelId).HasColumnName("tunnel_id");
        builder.Property(e => e.PhoneListId).HasColumnName("phone_list_id");
        builder.Property(e => e.TargetMailboxId).HasColumnName("target_mailbox_id");
        builder.Property(e => e.SourceUserId).HasColumnName("source_user_id");
        builder.Property(e => e.Action).HasColumnName("action").HasMaxLength(50).IsRequired();
        builder.Property(e => e.FieldChanges).HasColumnName("field_changes").HasColumnType("jsonb");
        builder.Property(e => e.ErrorMessage).HasColumnName("error_message");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");

        builder.HasOne(e => e.SyncRun)
            .WithMany(sr => sr.SyncRunItems)
            .HasForeignKey(e => e.SyncRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Tunnel)
            .WithMany()
            .HasForeignKey(e => e.TunnelId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(e => e.PhoneList)
            .WithMany()
            .HasForeignKey(e => e.PhoneListId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(e => e.TargetMailbox)
            .WithMany()
            .HasForeignKey(e => e.TargetMailboxId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(e => e.SourceUser)
            .WithMany()
            .HasForeignKey(e => e.SourceUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(e => e.SyncRunId).HasDatabaseName("idx_sync_run_items_run");
        builder.HasIndex(e => e.TunnelId).HasDatabaseName("idx_sync_run_items_tunnel");
    }
}
