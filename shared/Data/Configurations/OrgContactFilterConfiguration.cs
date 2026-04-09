using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class OrgContactFilterConfiguration : IEntityTypeConfiguration<OrgContactFilter>
{
    public void Configure(EntityTypeBuilder<OrgContactFilter> builder)
    {
        builder.ToTable("org_contact_filters");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TunnelId).HasColumnName("tunnel_id").IsRequired();
        builder.Property(e => e.OrgContactId).HasColumnName("org_contact_id").HasMaxLength(100).IsRequired();
        builder.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(200);
        builder.Property(e => e.Email).HasColumnName("email").HasMaxLength(300);
        builder.Property(e => e.CompanyName).HasColumnName("company_name").HasMaxLength(200);
        builder.Property(e => e.IsExcluded).HasColumnName("is_excluded").HasDefaultValue(false).IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

        builder.HasOne(e => e.Tunnel)
            .WithMany()
            .HasForeignKey(e => e.TunnelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.TunnelId, e.OrgContactId })
            .IsUnique()
            .HasDatabaseName("idx_org_contact_filters_tunnel_contact");
    }
}
