using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class SourceUserConfiguration : IEntityTypeConfiguration<SourceUser>
{
    public void Configure(EntityTypeBuilder<SourceUser> builder)
    {
        builder.ToTable("source_users");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.EntraId).HasColumnName("entra_id").HasMaxLength(500).IsRequired();
        builder.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(200);
        builder.Property(e => e.FirstName).HasColumnName("first_name").HasMaxLength(100);
        builder.Property(e => e.LastName).HasColumnName("last_name").HasMaxLength(100);
        builder.Property(e => e.Email).HasColumnName("email").HasMaxLength(300);
        builder.Property(e => e.BusinessPhone).HasColumnName("business_phone").HasMaxLength(50);
        builder.Property(e => e.MobilePhone).HasColumnName("mobile_phone").HasMaxLength(50);
        builder.Property(e => e.JobTitle).HasColumnName("job_title").HasMaxLength(200);
        builder.Property(e => e.Department).HasColumnName("department").HasMaxLength(200);
        builder.Property(e => e.OfficeLocation).HasColumnName("office_location").HasMaxLength(100);
        builder.Property(e => e.CompanyName).HasColumnName("company_name").HasMaxLength(200);
        builder.Property(e => e.StreetAddress).HasColumnName("street_address").HasMaxLength(500);
        builder.Property(e => e.City).HasColumnName("city").HasMaxLength(100);
        builder.Property(e => e.State).HasColumnName("state").HasMaxLength(100);
        builder.Property(e => e.PostalCode).HasColumnName("postal_code").HasMaxLength(20);
        builder.Property(e => e.Country).HasColumnName("country").HasMaxLength(100);
        builder.Property(e => e.Notes).HasColumnName("notes");
        builder.Property(e => e.PhotoHash).HasColumnName("photo_hash").HasMaxLength(64);
        builder.Property(e => e.ExtensionAttr1).HasColumnName("extension_attr_1").HasMaxLength(200);
        builder.Property(e => e.ExtensionAttr2).HasColumnName("extension_attr_2").HasMaxLength(200);
        builder.Property(e => e.ExtensionAttr3).HasColumnName("extension_attr_3").HasMaxLength(200);
        builder.Property(e => e.ExtensionAttr4).HasColumnName("extension_attr_4").HasMaxLength(200);
        builder.Property(e => e.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(true);
        builder.Property(e => e.HiddenFromGal).HasColumnName("hidden_from_gal").HasDefaultValue(false);
        builder.Property(e => e.MailboxType).HasColumnName("mailbox_type").HasMaxLength(50);
        builder.Property(e => e.LastFetchedAt).HasColumnName("last_fetched_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

        builder.HasIndex(e => e.EntraId).IsUnique().HasDatabaseName("idx_source_users_entra");
        builder.HasIndex(e => e.IsEnabled).HasDatabaseName("idx_source_users_enabled");
    }
}
