using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoSyncRunType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:run_type", "dry_run,manual,photo_sync,scheduled")
                .Annotation("Npgsql:Enum:source_type", "ddg,mailbox_contacts,org_contacts")
                .Annotation("Npgsql:Enum:stale_policy", "auto_remove,flag_hold,leave")
                .Annotation("Npgsql:Enum:sync_behavior", "add_missing,always,nosync,remove_blank")
                .Annotation("Npgsql:Enum:sync_status", "cancelled,failed,pending,running,success,warning")
                .Annotation("Npgsql:Enum:target_scope", "all_users,specific_users")
                .Annotation("Npgsql:Enum:tunnel_status", "active,inactive")
                .OldAnnotation("Npgsql:Enum:run_type", "dry_run,manual,scheduled")
                .OldAnnotation("Npgsql:Enum:source_type", "ddg,mailbox_contacts,org_contacts")
                .OldAnnotation("Npgsql:Enum:stale_policy", "auto_remove,flag_hold,leave")
                .OldAnnotation("Npgsql:Enum:sync_behavior", "add_missing,always,nosync,remove_blank")
                .OldAnnotation("Npgsql:Enum:sync_status", "cancelled,failed,pending,running,success,warning")
                .OldAnnotation("Npgsql:Enum:target_scope", "all_users,specific_users")
                .OldAnnotation("Npgsql:Enum:tunnel_status", "active,inactive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:run_type", "dry_run,manual,scheduled")
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
        }
    }
}
