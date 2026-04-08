using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Resets photo hashes to force re-fetch as 240x240 thumbnails. Full-size profile
    /// photos (50-200KB+) were too large for Exchange ActiveSync to include in the
    /// contact sync payload, causing photos to appear in OWA but not on phones.
    /// </summary>
    public partial class ResetPhotoHashesForThumbnail : Migration
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
