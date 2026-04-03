using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Api.Data.Configurations;

public class FieldProfileFieldConfiguration : IEntityTypeConfiguration<FieldProfileField>
{
    public void Configure(EntityTypeBuilder<FieldProfileField> builder)
    {
        builder.ToTable("field_profile_fields");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.FieldProfileId).HasColumnName("field_profile_id").IsRequired();
        builder.Property(e => e.FieldName).HasColumnName("field_name").HasMaxLength(100).IsRequired();
        builder.Property(e => e.FieldSection).HasColumnName("field_section").HasMaxLength(50).IsRequired();
        builder.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(100).IsRequired();
        builder.Property(e => e.Behavior).HasColumnName("behavior").IsRequired();
        builder.Property(e => e.DisplayOrder).HasColumnName("display_order").HasDefaultValue(0);

        builder.HasIndex(e => new { e.FieldProfileId, e.FieldName }).IsUnique();

        builder.HasOne(e => e.FieldProfile)
            .WithMany(fp => fp.FieldProfileFields)
            .HasForeignKey(e => e.FieldProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasData(
            // Identity
            new FieldProfileField { Id = 1, FieldProfileId = 1, FieldName = "DisplayName", FieldSection = "Identity", DisplayName = "Display Name", Behavior = SyncBehavior.Always, DisplayOrder = 1 },
            new FieldProfileField { Id = 2, FieldProfileId = 1, FieldName = "GivenName", FieldSection = "Identity", DisplayName = "First Name", Behavior = SyncBehavior.Always, DisplayOrder = 2 },
            new FieldProfileField { Id = 3, FieldProfileId = 1, FieldName = "Surname", FieldSection = "Identity", DisplayName = "Last Name", Behavior = SyncBehavior.Always, DisplayOrder = 3 },
            new FieldProfileField { Id = 4, FieldProfileId = 1, FieldName = "JobTitle", FieldSection = "Identity", DisplayName = "Job Title", Behavior = SyncBehavior.Always, DisplayOrder = 4 },
            new FieldProfileField { Id = 5, FieldProfileId = 1, FieldName = "CompanyName", FieldSection = "Identity", DisplayName = "Company", Behavior = SyncBehavior.Always, DisplayOrder = 5 },
            // Contact Info
            new FieldProfileField { Id = 6, FieldProfileId = 1, FieldName = "EmailAddresses", FieldSection = "Contact Info", DisplayName = "Email", Behavior = SyncBehavior.Always, DisplayOrder = 10 },
            new FieldProfileField { Id = 7, FieldProfileId = 1, FieldName = "BusinessPhones", FieldSection = "Contact Info", DisplayName = "Business Phone", Behavior = SyncBehavior.Always, DisplayOrder = 11 },
            new FieldProfileField { Id = 8, FieldProfileId = 1, FieldName = "MobilePhone", FieldSection = "Contact Info", DisplayName = "Mobile Phone", Behavior = SyncBehavior.Always, DisplayOrder = 12 },
            new FieldProfileField { Id = 9, FieldProfileId = 1, FieldName = "HomeFax", FieldSection = "Contact Info", DisplayName = "Fax", Behavior = SyncBehavior.Nosync, DisplayOrder = 13 },
            // Address
            new FieldProfileField { Id = 10, FieldProfileId = 1, FieldName = "BusinessStreet", FieldSection = "Address", DisplayName = "Business Street", Behavior = SyncBehavior.Always, DisplayOrder = 20 },
            new FieldProfileField { Id = 11, FieldProfileId = 1, FieldName = "BusinessCity", FieldSection = "Address", DisplayName = "Business City", Behavior = SyncBehavior.Always, DisplayOrder = 21 },
            new FieldProfileField { Id = 12, FieldProfileId = 1, FieldName = "BusinessState", FieldSection = "Address", DisplayName = "Business State", Behavior = SyncBehavior.Always, DisplayOrder = 22 },
            new FieldProfileField { Id = 13, FieldProfileId = 1, FieldName = "BusinessPostalCode", FieldSection = "Address", DisplayName = "Business Zip", Behavior = SyncBehavior.Always, DisplayOrder = 23 },
            new FieldProfileField { Id = 14, FieldProfileId = 1, FieldName = "HomeAddress", FieldSection = "Address", DisplayName = "Home Address", Behavior = SyncBehavior.Nosync, DisplayOrder = 24 },
            // Organization
            new FieldProfileField { Id = 15, FieldProfileId = 1, FieldName = "OfficeLocation", FieldSection = "Organization", DisplayName = "Office Location", Behavior = SyncBehavior.Always, DisplayOrder = 30 },
            new FieldProfileField { Id = 16, FieldProfileId = 1, FieldName = "Department", FieldSection = "Organization", DisplayName = "Department", Behavior = SyncBehavior.AddMissing, DisplayOrder = 31 },
            new FieldProfileField { Id = 17, FieldProfileId = 1, FieldName = "Manager", FieldSection = "Organization", DisplayName = "Manager", Behavior = SyncBehavior.Nosync, DisplayOrder = 32 },
            // Extras
            new FieldProfileField { Id = 18, FieldProfileId = 1, FieldName = "PersonalNotes", FieldSection = "Extras", DisplayName = "Notes", Behavior = SyncBehavior.AddMissing, DisplayOrder = 40 },
            new FieldProfileField { Id = 19, FieldProfileId = 1, FieldName = "Birthday", FieldSection = "Extras", DisplayName = "Birthday", Behavior = SyncBehavior.Nosync, DisplayOrder = 41 },
            new FieldProfileField { Id = 20, FieldProfileId = 1, FieldName = "NickName", FieldSection = "Extras", DisplayName = "Nickname", Behavior = SyncBehavior.Nosync, DisplayOrder = 42 },
            // Photo
            new FieldProfileField { Id = 21, FieldProfileId = 1, FieldName = "Photo", FieldSection = "Photo", DisplayName = "Contact Photo", Behavior = SyncBehavior.Always, DisplayOrder = 50 }
        );
    }
}
