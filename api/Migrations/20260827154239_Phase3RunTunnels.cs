using System;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class Phase3RunTunnels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "reconcile_pending_at",
                table: "tunnel_mailbox_folders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sync_run_tunnels",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    sync_run_id = table.Column<int>(type: "integer", nullable: false),
                    tunnel_id = table.Column<int>(type: "integer", nullable: true),
                    tunnel_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<SyncStatus>(type: "sync_status", nullable: false),
                    targets_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    contacts_created = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    contacts_updated = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    contacts_removed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    contacts_skipped = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    contacts_failed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    error_summary = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_run_tunnels", x => x.id);
                    table.ForeignKey(
                        name: "FK_sync_run_tunnels_sync_runs_sync_run_id",
                        column: x => x.sync_run_id,
                        principalTable: "sync_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sync_run_tunnels_tunnels_tunnel_id",
                        column: x => x.tunnel_id,
                        principalTable: "tunnels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "idx_sync_run_tunnels_run",
                table: "sync_run_tunnels",
                column: "sync_run_id");

            migrationBuilder.CreateIndex(
                name: "idx_sync_run_tunnels_tunnel_completed",
                table: "sync_run_tunnels",
                columns: new[] { "tunnel_id", "completed_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_run_tunnels");

            migrationBuilder.DropColumn(
                name: "reconcile_pending_at",
                table: "tunnel_mailbox_folders");
        }
    }
}
