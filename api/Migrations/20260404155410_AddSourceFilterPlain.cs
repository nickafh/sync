using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceFilterPlain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_filter_plain",
                table: "tunnels",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 1,
                column: "source_filter_plain",
                value: null);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 2,
                column: "source_filter_plain",
                value: null);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 3,
                column: "source_filter_plain",
                value: null);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 4,
                column: "source_filter_plain",
                value: null);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 5,
                column: "source_filter_plain",
                value: null);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 6,
                column: "source_filter_plain",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_filter_plain",
                table: "tunnels");
        }
    }
}
