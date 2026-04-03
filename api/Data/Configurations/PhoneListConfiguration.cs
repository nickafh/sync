using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Api.Data.Configurations;

public class PhoneListConfiguration : IEntityTypeConfiguration<PhoneList>
{
    public void Configure(EntityTypeBuilder<PhoneList> builder)
    {
        builder.ToTable("phone_lists");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.ExchangeFolderId).HasColumnName("exchange_folder_id").HasMaxLength(500);
        builder.Property(e => e.Description).HasColumnName("description");
        builder.Property(e => e.ContactCount).HasColumnName("contact_count").HasDefaultValue(0);
        builder.Property(e => e.UserCount).HasColumnName("user_count").HasDefaultValue(0);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

        builder.HasData(
            new PhoneList { Id = 1, Name = "All Atlanta Fine Homes", Description = "All AFH and MSIR contacts combined" },
            new PhoneList { Id = 2, Name = "All Mountain", Description = "All Mountain SIR contacts" },
            new PhoneList { Id = 3, Name = "AFHSIR", Description = "AFH Sotheby's agents only" },
            new PhoneList { Id = 4, Name = "MSIR", Description = "Mountain SIR contacts only" },
            new PhoneList { Id = 5, Name = "Avalon Gate Code", Description = "Gate access codes for Avalon community" }
        );
    }
}
