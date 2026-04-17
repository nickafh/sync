using System;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCleanupJobsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:cleanup_job_status", "cancelled,completed,failed,queued,running")
                .Annotation("Npgsql:Enum:run_type", "dry_run,manual,photo_sync,scheduled")
                .Annotation("Npgsql:Enum:source_type", "ddg,mailbox_contacts,org_contacts")
                .Annotation("Npgsql:Enum:stale_policy", "auto_remove,flag_hold,leave")
                .Annotation("Npgsql:Enum:sync_behavior", "add_missing,always,nosync,remove_blank")
                .Annotation("Npgsql:Enum:sync_status", "cancelled,failed,pending,running,success,warning")
                .Annotation("Npgsql:Enum:target_scope", "all_users,specific_users")
                .Annotation("Npgsql:Enum:tunnel_status", "active,inactive")
                .OldAnnotation("Npgsql:Enum:run_type", "dry_run,manual,photo_sync,scheduled")
                .OldAnnotation("Npgsql:Enum:source_type", "ddg,mailbox_contacts,org_contacts")
                .OldAnnotation("Npgsql:Enum:stale_policy", "auto_remove,flag_hold,leave")
                .OldAnnotation("Npgsql:Enum:sync_behavior", "add_missing,always,nosync,remove_blank")
                .OldAnnotation("Npgsql:Enum:sync_status", "cancelled,failed,pending,running,success,warning")
                .OldAnnotation("Npgsql:Enum:target_scope", "all_users,specific_users")
                .OldAnnotation("Npgsql:Enum:tunnel_status", "active,inactive");

            migrationBuilder.CreateTable(
                name: "cleanup_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<CleanupJobStatus>(type: "cleanup_job_status", nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    deleted = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    failed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    error_summary = table.Column<string>(type: "jsonb", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cleanup_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_cleanup_jobs_status",
                table: "cleanup_jobs",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cleanup_jobs");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:run_type", "dry_run,manual,photo_sync,scheduled")
                .Annotation("Npgsql:Enum:source_type", "ddg,mailbox_contacts,org_contacts")
                .Annotation("Npgsql:Enum:stale_policy", "auto_remove,flag_hold,leave")
                .Annotation("Npgsql:Enum:sync_behavior", "add_missing,always,nosync,remove_blank")
                .Annotation("Npgsql:Enum:sync_status", "cancelled,failed,pending,running,success,warning")
                .Annotation("Npgsql:Enum:target_scope", "all_users,specific_users")
                .Annotation("Npgsql:Enum:tunnel_status", "active,inactive")
                .OldAnnotation("Npgsql:Enum:cleanup_job_status", "cancelled,completed,failed,queued,running")
                .OldAnnotation("Npgsql:Enum:run_type", "dry_run,manual,photo_sync,scheduled")
                .OldAnnotation("Npgsql:Enum:source_type", "ddg,mailbox_contacts,org_contacts")
                .OldAnnotation("Npgsql:Enum:stale_policy", "auto_remove,flag_hold,leave")
                .OldAnnotation("Npgsql:Enum:sync_behavior", "add_missing,always,nosync,remove_blank")
                .OldAnnotation("Npgsql:Enum:sync_status", "cancelled,failed,pending,running,success,warning")
                .OldAnnotation("Npgsql:Enum:target_scope", "all_users,specific_users")
                .OldAnnotation("Npgsql:Enum:tunnel_status", "active,inactive");
        }
    }
}
