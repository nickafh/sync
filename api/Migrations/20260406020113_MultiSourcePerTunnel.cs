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
                    source_identifier = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    source_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_smtp_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    source_filter_plain = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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

            migrationBuilder.InsertData(
                table: "tunnel_sources",
                columns: new[] { "id", "source_display_name", "source_filter_plain", "source_identifier", "source_smtp_address", "source_type", "tunnel_id" },
                values: new object[,]
                {
                    { 1, "Buckhead Office DDG", null, "buckhead-ddg@atlantafinehomes.com", "buckhead-ddg@atlantafinehomes.com", SourceType.Ddg, 1 },
                    { 2, "North Atlanta Office DDG", null, "northatlanta-ddg@atlantafinehomes.com", "northatlanta-ddg@atlantafinehomes.com", SourceType.Ddg, 2 },
                    { 3, "Intown Office DDG", null, "intown-ddg@atlantafinehomes.com", "intown-ddg@atlantafinehomes.com", SourceType.Ddg, 3 },
                    { 4, "Blue Ridge Office DDG", null, "blueridge-ddg@atlantafinehomes.com", "blueridge-ddg@atlantafinehomes.com", SourceType.Ddg, 4 },
                    { 5, "Cobb Office DDG", null, "cobb-ddg@atlantafinehomes.com", "cobb-ddg@atlantafinehomes.com", SourceType.Ddg, 5 },
                    { 6, "Clayton Office DDG", null, "clayton-ddg@atlantafinehomes.com", "clayton-ddg@atlantafinehomes.com", SourceType.Ddg, 6 }
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

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "source_display_name", "source_filter_plain", "source_identifier", "source_smtp_address", "source_type" },
                values: new object[] { "Buckhead Office DDG", null, "buckhead-ddg@atlantafinehomes.com", "buckhead-ddg@atlantafinehomes.com", SourceType.Ddg });

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 2,
                columns: new[] { "source_display_name", "source_filter_plain", "source_identifier", "source_smtp_address", "source_type" },
                values: new object[] { "North Atlanta Office DDG", null, "northatlanta-ddg@atlantafinehomes.com", "northatlanta-ddg@atlantafinehomes.com", SourceType.Ddg });

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "source_display_name", "source_filter_plain", "source_identifier", "source_smtp_address", "source_type" },
                values: new object[] { "Intown Office DDG", null, "intown-ddg@atlantafinehomes.com", "intown-ddg@atlantafinehomes.com", SourceType.Ddg });

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 4,
                columns: new[] { "source_display_name", "source_filter_plain", "source_identifier", "source_smtp_address", "source_type" },
                values: new object[] { "Blue Ridge Office DDG", null, "blueridge-ddg@atlantafinehomes.com", "blueridge-ddg@atlantafinehomes.com", SourceType.Ddg });

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 5,
                columns: new[] { "source_display_name", "source_filter_plain", "source_identifier", "source_smtp_address", "source_type" },
                values: new object[] { "Cobb Office DDG", null, "cobb-ddg@atlantafinehomes.com", "cobb-ddg@atlantafinehomes.com", SourceType.Ddg });

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 6,
                columns: new[] { "source_display_name", "source_filter_plain", "source_identifier", "source_smtp_address", "source_type" },
                values: new object[] { "Clayton Office DDG", null, "clayton-ddg@atlantafinehomes.com", "clayton-ddg@atlantafinehomes.com", SourceType.Ddg });
        }
    }
}
