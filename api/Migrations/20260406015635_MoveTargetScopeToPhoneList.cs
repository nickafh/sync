using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class MoveTargetScopeToPhoneList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "target_scope",
                table: "tunnels");

            migrationBuilder.DropColumn(
                name: "target_user_filter",
                table: "tunnels");

            migrationBuilder.AddColumn<TargetScope>(
                name: "target_scope",
                table: "phone_lists",
                type: "target_scope",
                nullable: false,
                defaultValue: TargetScope.AllUsers);

            migrationBuilder.AddColumn<string>(
                name: "target_user_filter",
                table: "phone_lists",
                type: "jsonb",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "phone_lists",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "target_scope", "target_user_filter" },
                values: new object[] { TargetScope.AllUsers, null });

            migrationBuilder.UpdateData(
                table: "phone_lists",
                keyColumn: "id",
                keyValue: 2,
                columns: new[] { "target_scope", "target_user_filter" },
                values: new object[] { TargetScope.AllUsers, null });

            migrationBuilder.UpdateData(
                table: "phone_lists",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "target_scope", "target_user_filter" },
                values: new object[] { TargetScope.AllUsers, null });

            migrationBuilder.UpdateData(
                table: "phone_lists",
                keyColumn: "id",
                keyValue: 4,
                columns: new[] { "target_scope", "target_user_filter" },
                values: new object[] { TargetScope.AllUsers, null });

            migrationBuilder.UpdateData(
                table: "phone_lists",
                keyColumn: "id",
                keyValue: 5,
                columns: new[] { "target_scope", "target_user_filter" },
                values: new object[] { TargetScope.AllUsers, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "target_scope",
                table: "phone_lists");

            migrationBuilder.DropColumn(
                name: "target_user_filter",
                table: "phone_lists");

            migrationBuilder.AddColumn<TargetScope>(
                name: "target_scope",
                table: "tunnels",
                type: "target_scope",
                nullable: false,
                defaultValue: TargetScope.AllUsers);

            migrationBuilder.AddColumn<string>(
                name: "target_user_filter",
                table: "tunnels",
                type: "jsonb",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "target_scope", "target_user_filter" },
                values: new object[] { TargetScope.AllUsers, null });

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 2,
                columns: new[] { "target_scope", "target_user_filter" },
                values: new object[] { TargetScope.AllUsers, null });

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "target_scope", "target_user_filter" },
                values: new object[] { TargetScope.AllUsers, null });

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 4,
                columns: new[] { "target_scope", "target_user_filter" },
                values: new object[] { TargetScope.AllUsers, null });

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 5,
                columns: new[] { "target_scope", "target_user_filter" },
                values: new object[] { TargetScope.AllUsers, null });

            migrationBuilder.UpdateData(
                table: "tunnels",
                keyColumn: "id",
                keyValue: 6,
                columns: new[] { "target_scope", "target_user_filter" },
                values: new object[] { TargetScope.AllUsers, null });
        }
    }
}
