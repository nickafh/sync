using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Resets all photo hashes to force a full photo re-write with EAS touch.
    /// Previous writes stored the photo correctly (visible in OWA) but did not
    /// PATCH the contact afterward, so Exchange ActiveSync never re-synced the
    /// contact with the photo to phone clients.
    /// </summary>
    public partial class ResetPhotoHashesForEASTouch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE contact_sync_state SET photo_hash = NULL, previous_photo_hash = NULL WHERE photo_hash IS NOT NULL;");

            migrationBuilder.Sql(
                "UPDATE source_users SET photo_hash = NULL WHERE photo_hash IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
