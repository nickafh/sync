using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Resets all photo hashes to force a full photo re-write with correct
    /// Content-Type: image/jpeg header. Previous writes used application/octet-stream
    /// (Kiota SDK default) which Graph accepted silently but photos did not render
    /// on phone clients.
    /// </summary>
    public partial class ResetPhotoHashesForContentType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Clear photo hashes on ContactSyncState so delta logic re-processes all photos
            migrationBuilder.Sql(
                "UPDATE contact_sync_state SET photo_hash = NULL, previous_photo_hash = NULL WHERE photo_hash IS NOT NULL;");

            // Clear photo hashes on SourceUsers so they get re-fetched and re-compared
            migrationBuilder.Sql(
                "UPDATE source_users SET photo_hash = NULL WHERE photo_hash IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Cannot restore previous hashes — they were written with wrong content-type anyway.
            // The next sync run will recompute all hashes correctly.
        }
    }
}
