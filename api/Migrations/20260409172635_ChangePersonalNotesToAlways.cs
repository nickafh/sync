using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class ChangePersonalNotesToAlways : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "field_profile_fields",
                keyColumn: "id",
                keyValue: 18,
                column: "behavior",
                value: SyncBehavior.Always);

            // Reset all data hashes to force re-sync so PersonalNotes (now Always)
            // gets written to all existing contacts on the next run
            migrationBuilder.Sql(
                "UPDATE contact_sync_state SET data_hash = NULL, previous_data_hash = NULL WHERE data_hash IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "field_profile_fields",
                keyColumn: "id",
                keyValue: 18,
                column: "behavior",
                value: SyncBehavior.AddMissing);
        }
    }
}
