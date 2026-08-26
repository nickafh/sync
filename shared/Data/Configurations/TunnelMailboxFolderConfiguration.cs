using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class TunnelMailboxFolderConfiguration : IEntityTypeConfiguration<TunnelMailboxFolder>
{
    public void Configure(EntityTypeBuilder<TunnelMailboxFolder> builder)
    {
        builder.ToTable("tunnel_mailbox_folders");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TunnelId).HasColumnName("tunnel_id").IsRequired();
        builder.Property(e => e.TargetMailboxId).HasColumnName("target_mailbox_id").IsRequired();
        builder.Property(e => e.GraphFolderId).HasColumnName("graph_folder_id").HasMaxLength(300).IsRequired();
        builder.Property(e => e.FolderName).HasColumnName("folder_name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

        builder.HasIndex(e => new { e.TunnelId, e.TargetMailboxId })
            .IsUnique()
            .HasDatabaseName("idx_tunnel_mailbox_folders_tunnel_mailbox");

        builder.HasOne(e => e.Tunnel)
            .WithMany()
            .HasForeignKey(e => e.TunnelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.TargetMailbox)
            .WithMany()
            .HasForeignKey(e => e.TargetMailboxId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
