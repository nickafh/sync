using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Resets contact data hashes to force re-sync after switching the Notes source
    /// from onPremisesExtensionAttributes.extensionAttribute5 to Graph User.aboutMe.
    /// All users were migrated cloud-only, so ext5 is no longer populated — aboutMe
    /// is the authoritative source for the Teams/OWA contact-card "Notes" field
    /// (e.g., the April Avalon Gate Code for the 342 Avalon Users mailboxes).
    /// </summary>
    public partial class ResetDataHashesForCloudNotes : Migration
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
