using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Resets all photo hashes on ContactSyncState and SourceUser records.
    /// This forces the next photo sync run to re-write all photos using the
    /// corrected ContactFolders Graph API path (fix for photos-not-syncing-to-contacts).
    /// Previous photo writes used the flat /contacts/{id}/photo path which does not
    /// reliably write photos for contacts in custom subfolders.
    /// </summary>
    public partial class ResetPhotoHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Clear photo hashes on ContactSyncState so delta logic re-processes all photos
            migrationBuilder.Sql(
                "UPDATE contact_sync_states SET photo_hash = NULL, previous_photo_hash = NULL WHERE photo_hash IS NOT NULL;");

            // Clear photo hashes on SourceUsers so they get re-fetched and re-compared
            migrationBuilder.Sql(
                "UPDATE source_users SET photo_hash = NULL WHERE photo_hash IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Cannot restore previous hashes — they were incorrect anyway.
            // The next sync run will recompute all hashes correctly.
        }
    }
}
