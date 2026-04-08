using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Resets contact data hashes to force re-sync so OfficeLocation gets
    /// prepended to PersonalNotes (iOS has no dedicated office field).
    /// </summary>
    public partial class ResetDataHashesForOfficeNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE contact_sync_state SET data_hash = NULL, previous_data_hash = NULL WHERE data_hash IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
