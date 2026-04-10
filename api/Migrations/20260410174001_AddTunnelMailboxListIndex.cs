using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTunnelMailboxListIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_contact_sync_state_tunnel_id",
                table: "contact_sync_state");

            migrationBuilder.CreateIndex(
                name: "idx_contact_sync_state_tunnel_mailbox_list",
                table: "contact_sync_state",
                columns: new[] { "tunnel_id", "target_mailbox_id", "phone_list_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_contact_sync_state_tunnel_mailbox_list",
                table: "contact_sync_state");

            migrationBuilder.CreateIndex(
                name: "IX_contact_sync_state_tunnel_id",
                table: "contact_sync_state",
                column: "tunnel_id");
        }
    }
}
