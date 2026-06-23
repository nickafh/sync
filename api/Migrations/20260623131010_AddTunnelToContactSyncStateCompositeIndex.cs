using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTunnelToContactSyncStateCompositeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_contact_sync_state_composite",
                table: "contact_sync_state");

            migrationBuilder.CreateIndex(
                name: "idx_contact_sync_state_composite",
                table: "contact_sync_state",
                columns: new[] { "source_user_id", "phone_list_id", "target_mailbox_id", "tunnel_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_contact_sync_state_composite",
                table: "contact_sync_state");

            migrationBuilder.CreateIndex(
                name: "idx_contact_sync_state_composite",
                table: "contact_sync_state",
                columns: new[] { "source_user_id", "phone_list_id", "target_mailbox_id" },
                unique: true);
        }
    }
}
