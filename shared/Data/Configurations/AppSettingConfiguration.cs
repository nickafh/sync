using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> builder)
    {
        builder.ToTable("app_settings");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Key).HasColumnName("key").HasMaxLength(100).IsRequired();
        builder.Property(e => e.Value).HasColumnName("value").IsRequired();
        builder.Property(e => e.Description).HasColumnName("description");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

        builder.HasIndex(e => e.Key).IsUnique();

        builder.HasData(
            new AppSetting { Id = 1, Key = "sync_schedule_cron", Value = "0 */4 * * *", Description = "Sync runs every 4 hours" },
            new AppSetting { Id = 2, Key = "photo_sync_mode", Value = "included", Description = "included | separate_pass | disabled" },
            new AppSetting { Id = 3, Key = "batch_size", Value = "50", Description = "Contacts per batch for Graph writes" },
            new AppSetting { Id = 4, Key = "parallelism", Value = "4", Description = "Concurrent target mailbox processing" },
            new AppSetting { Id = 5, Key = "stale_policy_default", Value = "flag_hold", Description = "Default stale policy for new tunnels" },
            new AppSetting { Id = 6, Key = "stale_hold_days_default", Value = "14", Description = "Default hold period before auto-remove" },
            new AppSetting { Id = 7, Key = "graph_tenant_id", Value = "", Description = "Azure AD Tenant ID" },
            new AppSetting { Id = 8, Key = "graph_client_id", Value = "", Description = "Entra App Registration Client ID" },
            new AppSetting { Id = 9, Key = "graph_client_secret", Value = "", Description = "Entra App Registration Client Secret (use Key Vault in production)" },
            new AppSetting { Id = 10, Key = "photo_sync_cron", Value = "0 */6 * * *", Description = "Photo sync schedule for separate_pass mode (every 6 hours)" },
            new AppSetting { Id = 11, Key = "photo_sync_auto_trigger", Value = "false", Description = "Auto-trigger photo sync after contact sync completes (separate_pass mode)" }
        );
    }
}
