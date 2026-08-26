using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class Phase2DataIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "mailbox_last_probed_at",
                table: "target_mailboxes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "mailbox_unavailable_at",
                table: "target_mailboxes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mailbox_unavailable_reason",
                table: "target_mailboxes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_tunnel_ids",
                table: "sync_runs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tunnel_mailbox_folders",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tunnel_id = table.Column<int>(type: "integer", nullable: false),
                    target_mailbox_id = table.Column<int>(type: "integer", nullable: false),
                    graph_folder_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    folder_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tunnel_mailbox_folders", x => x.id);
                    table.ForeignKey(
                        name: "FK_tunnel_mailbox_folders_target_mailboxes_target_mailbox_id",
                        column: x => x.target_mailbox_id,
                        principalTable: "target_mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tunnel_mailbox_folders_tunnels_tunnel_id",
                        column: x => x.tunnel_id,
                        principalTable: "tunnels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_tunnel_mailbox_folders_tunnel_mailbox",
                table: "tunnel_mailbox_folders",
                columns: new[] { "tunnel_id", "target_mailbox_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tunnel_mailbox_folders_target_mailbox_id",
                table: "tunnel_mailbox_folders",
                column: "target_mailbox_id");

            // Phase 2 (§2.0): dry-run artifacts and lost-id creates. A state row without a Graph
            // contact id can never be updated or deleted; the next real run recreates the contact.
            // Deploy step 1 counts these first (see the plan's Task 13).
            migrationBuilder.Sql("DELETE FROM contact_sync_state WHERE graph_contact_id IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tunnel_mailbox_folders");

            migrationBuilder.DropColumn(
                name: "mailbox_last_probed_at",
                table: "target_mailboxes");

            migrationBuilder.DropColumn(
                name: "mailbox_unavailable_at",
                table: "target_mailboxes");

            migrationBuilder.DropColumn(
                name: "mailbox_unavailable_reason",
                table: "target_mailboxes");

            migrationBuilder.DropColumn(
                name: "requested_tunnel_ids",
                table: "sync_runs");
        }
    }
}
