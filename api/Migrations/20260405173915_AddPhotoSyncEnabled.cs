using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoSyncEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "photo_sync_enabled",
                table: "tunnels",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.InsertData(
                table: "app_settings",
                columns: new[] { "id", "description", "key", "value" },
                values: new object[,]
                {
                    { 10, "Photo sync schedule for separate_pass mode (every 6 hours)", "photo_sync_cron", "0 */6 * * *" },
                    { 11, "Auto-trigger photo sync after contact sync completes (separate_pass mode)", "photo_sync_auto_trigger", "false" }
                });

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 1,
                column: "photo_sync_enabled",
                value: true);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 2,
                column: "photo_sync_enabled",
                value: true);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 3,
                column: "photo_sync_enabled",
                value: true);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 4,
                column: "photo_sync_enabled",
                value: true);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 5,
                column: "photo_sync_enabled",
                value: true);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 6,
                column: "photo_sync_enabled",
                value: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "app_settings",
                keyColumn: "id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "app_settings",
                keyColumn: "id",
                keyValue: 11);

            migrationBuilder.DropColumn(
                name: "photo_sync_enabled",
                table: "tunnels");
        }
    }
}
