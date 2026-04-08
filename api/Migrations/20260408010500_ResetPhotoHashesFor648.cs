using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class ResetPhotoHashesFor648 : Migration
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
