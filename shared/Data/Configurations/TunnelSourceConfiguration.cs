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
        builder.Property(e => e.SourceIdentifier).HasColumnName("source_identifier").HasMaxLength(500).IsRequired();
        builder.Property(e => e.SourceDisplayName).HasColumnName("source_display_name").HasMaxLength(200);
        builder.Property(e => e.SourceSmtpAddress).HasColumnName("source_smtp_address").HasMaxLength(320);
        builder.Property(e => e.SourceFilterPlain).HasColumnName("source_filter_plain").HasMaxLength(500);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");

        builder.HasOne(e => e.Tunnel)
            .WithMany(t => t.TunnelSources)
            .HasForeignKey(e => e.TunnelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.TunnelId).HasDatabaseName("idx_tunnel_sources_tunnel_id");

        builder.HasData(
            new TunnelSource { Id = 1, TunnelId = 1, SourceType = SourceType.Ddg, SourceIdentifier = "buckhead-ddg@atlantafinehomes.com", SourceDisplayName = "Buckhead Office DDG", SourceSmtpAddress = "buckhead-ddg@atlantafinehomes.com" },
            new TunnelSource { Id = 2, TunnelId = 2, SourceType = SourceType.Ddg, SourceIdentifier = "northatlanta-ddg@atlantafinehomes.com", SourceDisplayName = "North Atlanta Office DDG", SourceSmtpAddress = "northatlanta-ddg@atlantafinehomes.com" },
            new TunnelSource { Id = 3, TunnelId = 3, SourceType = SourceType.Ddg, SourceIdentifier = "intown-ddg@atlantafinehomes.com", SourceDisplayName = "Intown Office DDG", SourceSmtpAddress = "intown-ddg@atlantafinehomes.com" },
            new TunnelSource { Id = 4, TunnelId = 4, SourceType = SourceType.Ddg, SourceIdentifier = "blueridge-ddg@atlantafinehomes.com", SourceDisplayName = "Blue Ridge Office DDG", SourceSmtpAddress = "blueridge-ddg@atlantafinehomes.com" },
            new TunnelSource { Id = 5, TunnelId = 5, SourceType = SourceType.Ddg, SourceIdentifier = "cobb-ddg@atlantafinehomes.com", SourceDisplayName = "Cobb Office DDG", SourceSmtpAddress = "cobb-ddg@atlantafinehomes.com" },
            new TunnelSource { Id = 6, TunnelId = 6, SourceType = SourceType.Ddg, SourceIdentifier = "clayton-ddg@atlantafinehomes.com", SourceDisplayName = "Clayton Office DDG", SourceSmtpAddress = "clayton-ddg@atlantafinehomes.com" }
        );
    }
}
