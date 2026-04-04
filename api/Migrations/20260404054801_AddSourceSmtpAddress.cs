using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceSmtpAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_smtp_address",
                table: "tunnels",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 1,
                column: "source_smtp_address",
                value: "buckhead-ddg@atlantafinehomes.com");

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 2,
                column: "source_smtp_address",
                value: "northatlanta-ddg@atlantafinehomes.com");

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 3,
                column: "source_smtp_address",
                value: "intown-ddg@atlantafinehomes.com");

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 4,
                column: "source_smtp_address",
                value: "blueridge-ddg@atlantafinehomes.com");

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 5,
                column: "source_smtp_address",
                value: "cobb-ddg@atlantafinehomes.com");

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 6,
                column: "source_smtp_address",
                value: "clayton-ddg@atlantafinehomes.com");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_smtp_address",
                table: "tunnels");
        }
    }
}
