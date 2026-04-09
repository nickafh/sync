using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class TunnelContactExclusionConfiguration : IEntityTypeConfiguration<TunnelContactExclusion>
{
    public void Configure(EntityTypeBuilder<TunnelContactExclusion> builder)
    {
        builder.ToTable("tunnel_contact_exclusions");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.EntraId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(e => e.DisplayName)
            .HasMaxLength(200);

        builder.Property(e => e.Email)
            .HasMaxLength(300);

        builder.Property(e => e.CreatedAt)
            .HasDefaultValueSql("NOW()");

        builder.HasIndex(e => new { e.TunnelId, e.EntraId })
            .IsUnique();

        builder.HasOne(e => e.Tunnel)
            .WithMany()
            .HasForeignKey(e => e.TunnelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
