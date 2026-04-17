using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class CleanupJobConfiguration : IEntityTypeConfiguration<CleanupJob>
{
    public void Configure(EntityTypeBuilder<CleanupJob> builder)
    {
        builder.ToTable("cleanup_jobs");

        builder.HasKey(e => e.Id);
        // No DB-side default — the API supplies Guid.NewGuid() so it can return the id in the 202 response.
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Status).HasColumnName("status").HasColumnType("cleanup_job_status");
        builder.Property(e => e.Total).HasColumnName("total").HasDefaultValue(0);
        builder.Property(e => e.Deleted).HasColumnName("deleted").HasDefaultValue(0);
        builder.Property(e => e.Failed).HasColumnName("failed").HasDefaultValue(0);
        builder.Property(e => e.LastError).HasColumnName("last_error");
        builder.Property(e => e.ErrorSummary).HasColumnName("error_summary").HasColumnType("jsonb");
        builder.Property(e => e.StartedAt).HasColumnName("started_at");
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

        builder.HasIndex(e => e.Status).HasDatabaseName("idx_cleanup_jobs_status");
    }
}
