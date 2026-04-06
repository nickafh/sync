using System;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class MultiSourcePerTunnel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 4);

            migrationBuilder.DeleteData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 5);

            migrationBuilder.DeleteData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 6);

            migrationBuilder.DropColumn(
                name: "source_display_name",
                table: "tunnels");

            migrationBuilder.DropColumn(
                name: "source_filter_plain",
                table: "tunnels");

            migrationBuilder.DropColumn(
                name: "source_identifier",
                table: "tunnels");

            migrationBuilder.DropColumn(
                name: "source_smtp_address",
                table: "tunnels");

            migrationBuilder.DropColumn(
                name: "source_type",
                table: "tunnels");

            migrationBuilder.CreateTable(
                name: "tunnel_sources",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tunnel_id = table.Column<int>(type: "integer", nullable: false),
                    source_type = table.Column<SourceType>(type: "source_type", nullable: false),
                    source_identifier = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    source_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_smtp_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    source_filter_plain = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tunnel_sources", x => x.id);
                    table.ForeignKey(
                        name: "FK_tunnel_sources_tunnels_tunnel_id",
                        column: x => x.tunnel_id,
                        principalTable: "tunnels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_tunnel_sources_tunnel_id",
                table: "tunnel_sources",
                column: "tunnel_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tunnel_sources");

            migrationBuilder.AddColumn<string>(
                name: "source_display_name",
                table: "tunnels",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_filter_plain",
                table: "tunnels",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_identifier",
                table: "tunnels",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "source_smtp_address",
                table: "tunnels",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<SourceType>(
                name: "source_type",
                table: "tunnels",
                type: "source_type",
                nullable: false,
                defaultValue: SourceType.Ddg);

            migrationBuilder.InsertData(
                table: "tunnels",
                columns: new[] { "id", "field_profile_id", "name", "photo_sync_enabled", "source_display_name", "source_filter_plain", "source_identifier", "source_smtp_address", "source_type", "stale_hold_days", "stale_policy", "status" },
                values: new object[,]
                {
                    { 1, 1, "Buckhead", true, "Buckhead Office DDG", null, "buckhead-ddg@atlantafinehomes.com", "buckhead-ddg@atlantafinehomes.com", SourceType.Ddg, 14, StalePolicy.FlagHold, TunnelStatus.Active },
                    { 2, 1, "North Atlanta", true, "North Atlanta Office DDG", null, "northatlanta-ddg@atlantafinehomes.com", "northatlanta-ddg@atlantafinehomes.com", SourceType.Ddg, 14, StalePolicy.FlagHold, TunnelStatus.Active },
                    { 3, 1, "Intown", true, "Intown Office DDG", null, "intown-ddg@atlantafinehomes.com", "intown-ddg@atlantafinehomes.com", SourceType.Ddg, 14, StalePolicy.FlagHold, TunnelStatus.Active },
                    { 4, 1, "Blue Ridge", true, "Blue Ridge Office DDG", null, "blueridge-ddg@atlantafinehomes.com", "blueridge-ddg@atlantafinehomes.com", SourceType.Ddg, 14, StalePolicy.FlagHold, TunnelStatus.Active },
                    { 5, 1, "Cobb", true, "Cobb Office DDG", null, "cobb-ddg@atlantafinehomes.com", "cobb-ddg@atlantafinehomes.com", SourceType.Ddg, 14, StalePolicy.FlagHold, TunnelStatus.Active },
                    { 6, 1, "Clayton", true, "Clayton Office DDG", null, "clayton-ddg@atlantafinehomes.com", "clayton-ddg@atlantafinehomes.com", SourceType.Ddg, 14, StalePolicy.FlagHold, TunnelStatus.Active }
                });
        }
    }
}
