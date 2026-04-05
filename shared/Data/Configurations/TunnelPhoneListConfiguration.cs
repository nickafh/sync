using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class TunnelPhoneListConfiguration : IEntityTypeConfiguration<TunnelPhoneList>
{
    public void Configure(EntityTypeBuilder<TunnelPhoneList> builder)
    {
        builder.ToTable("tunnel_phone_lists");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TunnelId).HasColumnName("tunnel_id").IsRequired();
        builder.Property(e => e.PhoneListId).HasColumnName("phone_list_id").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");

        builder.HasIndex(e => new { e.TunnelId, e.PhoneListId }).IsUnique();

        builder.HasOne(e => e.Tunnel)
            .WithMany(t => t.TunnelPhoneLists)
            .HasForeignKey(e => e.TunnelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.PhoneList)
            .WithMany(pl => pl.TunnelPhoneLists)
            .HasForeignKey(e => e.PhoneListId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasData(
            new TunnelPhoneList { Id = 1, TunnelId = 1, PhoneListId = 1 },
            new TunnelPhoneList { Id = 2, TunnelId = 1, PhoneListId = 2 },
            new TunnelPhoneList { Id = 3, TunnelId = 2, PhoneListId = 1 },
            new TunnelPhoneList { Id = 4, TunnelId = 2, PhoneListId = 2 },
            new TunnelPhoneList { Id = 5, TunnelId = 3, PhoneListId = 1 },
            new TunnelPhoneList { Id = 6, TunnelId = 3, PhoneListId = 2 },
            new TunnelPhoneList { Id = 7, TunnelId = 4, PhoneListId = 1 },
            new TunnelPhoneList { Id = 8, TunnelId = 4, PhoneListId = 2 },
            new TunnelPhoneList { Id = 9, TunnelId = 5, PhoneListId = 1 },
            new TunnelPhoneList { Id = 10, TunnelId = 5, PhoneListId = 2 },
            new TunnelPhoneList { Id = 11, TunnelId = 6, PhoneListId = 1 },
            new TunnelPhoneList { Id = 12, TunnelId = 6, PhoneListId = 2 }
        );
    }
}
