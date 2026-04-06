using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class TunnelSourceConfiguration : IEntityTypeConfiguration<TunnelSource>
{
    public void Configure(EntityTypeBuilder<TunnelSource> builder)
    {
        builder.ToTable("tunnel_sources");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TunnelId).HasColumnName("tunnel_id").IsRequired();
        builder.Property(e => e.SourceType).HasColumnName("source_type").IsRequired();
        builder.Property(e => e.SourceIdentifier).HasColumnName("source_identifier").HasMaxLength(2000).IsRequired();
        builder.Property(e => e.SourceDisplayName).HasColumnName("source_display_name").HasMaxLength(200);
        builder.Property(e => e.SourceSmtpAddress).HasColumnName("source_smtp_address").HasMaxLength(320);
        builder.Property(e => e.SourceFilterPlain).HasColumnName("source_filter_plain").HasMaxLength(2000);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");

        builder.HasOne(e => e.Tunnel)
            .WithMany(t => t.TunnelSources)
            .HasForeignKey(e => e.TunnelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.TunnelId).HasDatabaseName("idx_tunnel_sources_tunnel_id");
    }
}
