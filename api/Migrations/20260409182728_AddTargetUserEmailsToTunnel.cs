using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetUserEmailsToTunnel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:run_type", "dry_run,manual,scheduled")
                .Annotation("Npgsql:Enum:source_type", "ddg,mailbox_contacts,org_contacts")
                .Annotation("Npgsql:Enum:stale_policy", "auto_remove,flag_hold,leave")
                .Annotation("Npgsql:Enum:sync_behavior", "add_missing,always,nosync,remove_blank")
                .Annotation("Npgsql:Enum:sync_status", "cancelled,failed,pending,running,success,warning")
                .Annotation("Npgsql:Enum:target_scope", "all_users,specific_users")
                .Annotation("Npgsql:Enum:tunnel_status", "active,inactive")
                .OldAnnotation("Npgsql:Enum:run_type", "dry_run,manual,scheduled")
                .OldAnnotation("Npgsql:Enum:source_type", "ddg,mailbox_contacts")
                .OldAnnotation("Npgsql:Enum:stale_policy", "auto_remove,flag_hold,leave")
                .OldAnnotation("Npgsql:Enum:sync_behavior", "add_missing,always,nosync,remove_blank")
                .OldAnnotation("Npgsql:Enum:sync_status", "cancelled,failed,pending,running,success,warning")
                .OldAnnotation("Npgsql:Enum:target_scope", "all_users,specific_users")
                .OldAnnotation("Npgsql:Enum:tunnel_status", "active,inactive");

            migrationBuilder.AddColumn<string>(
                name: "target_user_emails",
                table: "tunnels",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "org_contact_filters",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tunnel_id = table.Column<int>(type: "integer", nullable: false),
                    org_contact_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    company_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_excluded = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_org_contact_filters", x => x.id);
                    table.ForeignKey(
                        name: "FK_org_contact_filters_tunnels_tunnel_id",
                        column: x => x.tunnel_id,
                        principalTable: "tunnels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_org_contact_filters_tunnel_contact",
                table: "org_contact_filters",
                columns: new[] { "tunnel_id", "org_contact_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "org_contact_filters");

            migrationBuilder.DropColumn(
                name: "target_user_emails",
                table: "tunnels");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:run_type", "dry_run,manual,scheduled")
                .Annotation("Npgsql:Enum:source_type", "ddg,mailbox_contacts")
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
    }
}
