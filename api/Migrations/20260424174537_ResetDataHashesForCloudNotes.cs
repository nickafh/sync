using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Resets contact data hashes to force a re-sync of the Notes field (April 2026).
    /// Historical note: this was written when Notes was expected to come from Graph User.aboutMe;
    /// aboutMe cannot be $select-ed on the /users list endpoint, so Notes is sourced from
    /// Exchange CustomAttribute5 (onPremisesExtensionAttributes.extensionAttribute5).
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
