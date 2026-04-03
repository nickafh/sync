using System;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AFHSync.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:run_type", "dry_run,manual,scheduled")
                .Annotation("Npgsql:Enum:source_type", "ddg,mailbox_contacts")
                .Annotation("Npgsql:Enum:stale_policy", "auto_remove,flag_hold,leave")
                .Annotation("Npgsql:Enum:sync_behavior", "add_missing,always,nosync,remove_blank")
                .Annotation("Npgsql:Enum:sync_status", "cancelled,failed,pending,running,success,warning")
                .Annotation("Npgsql:Enum:target_scope", "all_users,specific_users")
                .Annotation("Npgsql:Enum:tunnel_status", "active,inactive");

            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "field_profiles",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "phone_lists",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    exchange_folder_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    contact_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    user_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phone_lists", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "source_users",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entra_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    email = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    business_phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    mobile_phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    job_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    department = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    office_location = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    company_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    street_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    state = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    postal_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    photo_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    extension_attr_1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    extension_attr_2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    extension_attr_3 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    extension_attr_4 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    mailbox_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    last_fetched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sync_runs",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    run_type = table.Column<RunType>(type: "run_type", nullable: false),
                    status = table.Column<SyncStatus>(type: "sync_status", nullable: false),
                    is_dry_run = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    tunnels_processed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    tunnels_warned = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    tunnels_failed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    contacts_created = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    contacts_updated = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    contacts_removed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    contacts_skipped = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    contacts_failed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    photos_updated = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    photos_failed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    throttle_events = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    error_summary = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "target_mailboxes",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entra_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    last_verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_target_mailboxes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "field_profile_fields",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    field_profile_id = table.Column<int>(type: "integer", nullable: false),
                    field_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    field_section = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    behavior = table.Column<SyncBehavior>(type: "sync_behavior", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_profile_fields", x => x.id);
                    table.ForeignKey(
                        name: "FK_field_profile_fields_field_profiles_field_profile_id",
                        column: x => x.field_profile_id,
                        principalTable: "field_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tunnels",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    source_type = table.Column<SourceType>(type: "source_type", nullable: false),
                    source_identifier = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    source_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    target_scope = table.Column<TargetScope>(type: "target_scope", nullable: false),
                    target_user_filter = table.Column<string>(type: "jsonb", nullable: true),
                    field_profile_id = table.Column<int>(type: "integer", nullable: true),
                    stale_policy = table.Column<StalePolicy>(type: "stale_policy", nullable: false),
                    stale_hold_days = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<TunnelStatus>(type: "tunnel_status", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tunnels", x => x.id);
                    table.ForeignKey(
                        name: "FK_tunnels_field_profiles_field_profile_id",
                        column: x => x.field_profile_id,
                        principalTable: "field_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "contact_sync_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_user_id = table.Column<int>(type: "integer", nullable: false),
                    phone_list_id = table.Column<int>(type: "integer", nullable: false),
                    target_mailbox_id = table.Column<int>(type: "integer", nullable: false),
                    tunnel_id = table.Column<int>(type: "integer", nullable: true),
                    graph_contact_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    data_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    photo_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    previous_data_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    previous_photo_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_stale = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    stale_detected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_result = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_sync_state", x => x.id);
                    table.ForeignKey(
                        name: "FK_contact_sync_state_phone_lists_phone_list_id",
                        column: x => x.phone_list_id,
                        principalTable: "phone_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contact_sync_state_source_users_source_user_id",
                        column: x => x.source_user_id,
                        principalTable: "source_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contact_sync_state_target_mailboxes_target_mailbox_id",
                        column: x => x.target_mailbox_id,
                        principalTable: "target_mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contact_sync_state_tunnels_tunnel_id",
                        column: x => x.tunnel_id,
                        principalTable: "tunnels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "sync_run_items",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    sync_run_id = table.Column<int>(type: "integer", nullable: false),
                    tunnel_id = table.Column<int>(type: "integer", nullable: true),
                    phone_list_id = table.Column<int>(type: "integer", nullable: true),
                    target_mailbox_id = table.Column<int>(type: "integer", nullable: true),
                    source_user_id = table.Column<int>(type: "integer", nullable: true),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    field_changes = table.Column<string>(type: "jsonb", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_run_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_sync_run_items_phone_lists_phone_list_id",
                        column: x => x.phone_list_id,
                        principalTable: "phone_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sync_run_items_source_users_source_user_id",
                        column: x => x.source_user_id,
                        principalTable: "source_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sync_run_items_sync_runs_sync_run_id",
                        column: x => x.sync_run_id,
                        principalTable: "sync_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sync_run_items_target_mailboxes_target_mailbox_id",
                        column: x => x.target_mailbox_id,
                        principalTable: "target_mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sync_run_items_tunnels_tunnel_id",
                        column: x => x.tunnel_id,
                        principalTable: "tunnels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "tunnel_phone_lists",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tunnel_id = table.Column<int>(type: "integer", nullable: false),
                    phone_list_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tunnel_phone_lists", x => x.id);
                    table.ForeignKey(
                        name: "FK_tunnel_phone_lists_phone_lists_phone_list_id",
                        column: x => x.phone_list_id,
                        principalTable: "phone_lists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tunnel_phone_lists_tunnels_tunnel_id",
                        column: x => x.tunnel_id,
                        principalTable: "tunnels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "app_settings",
                columns: new[] { "id", "description", "key", "value" },
                values: new object[,]
                {
                    { 1, "Sync runs every 4 hours", "sync_schedule_cron", "0 */4 * * *" },
                    { 2, "included | separate_pass | disabled", "photo_sync_mode", "included" },
                    { 3, "Contacts per batch for Graph writes", "batch_size", "50" },
                    { 4, "Concurrent target mailbox processing", "parallelism", "4" },
                    { 5, "Default stale policy for new tunnels", "stale_policy_default", "flag_hold" },
                    { 6, "Default hold period before auto-remove", "stale_hold_days_default", "14" },
                    { 7, "Azure AD Tenant ID", "graph_tenant_id", "" },
                    { 8, "Entra App Registration Client ID", "graph_client_id", "" },
                    { 9, "Entra App Registration Client Secret (use Key Vault in production)", "graph_client_secret", "" }
                });

            migrationBuilder.InsertData(
                table: "field_profiles",
                columns: new[] { "id", "description", "is_default", "name" },
                values: new object[] { 1, "Standard field sync profile for all office tunnels", true, "Default" });

            migrationBuilder.InsertData(
                table: "phone_lists",
                columns: new[] { "id", "description", "exchange_folder_id", "name" },
                values: new object[,]
                {
                    { 1, "All AFH and MSIR contacts combined", null, "All Atlanta Fine Homes" },
                    { 2, "All Mountain SIR contacts", null, "All Mountain" },
                    { 3, "AFH Sotheby's agents only", null, "AFHSIR" },
                    { 4, "Mountain SIR contacts only", null, "MSIR" },
                    { 5, "Gate access codes for Avalon community", null, "Avalon Gate Code" }
                });

            migrationBuilder.InsertData(
                table: "field_profile_fields",
                columns: new[] { "id", "behavior", "display_name", "display_order", "field_name", "field_profile_id", "field_section" },
                values: new object[,]
                {
                    { 1, SyncBehavior.Always, "Display Name", 1, "DisplayName", 1, "Identity" },
                    { 2, SyncBehavior.Always, "First Name", 2, "GivenName", 1, "Identity" },
                    { 3, SyncBehavior.Always, "Last Name", 3, "Surname", 1, "Identity" },
                    { 4, SyncBehavior.Always, "Job Title", 4, "JobTitle", 1, "Identity" },
                    { 5, SyncBehavior.Always, "Company", 5, "CompanyName", 1, "Identity" },
                    { 6, SyncBehavior.Always, "Email", 10, "EmailAddresses", 1, "Contact Info" },
                    { 7, SyncBehavior.Always, "Business Phone", 11, "BusinessPhones", 1, "Contact Info" },
                    { 8, SyncBehavior.Always, "Mobile Phone", 12, "MobilePhone", 1, "Contact Info" },
                    { 9, SyncBehavior.Nosync, "Fax", 13, "HomeFax", 1, "Contact Info" },
                    { 10, SyncBehavior.Always, "Business Street", 20, "BusinessStreet", 1, "Address" },
                    { 11, SyncBehavior.Always, "Business City", 21, "BusinessCity", 1, "Address" },
                    { 12, SyncBehavior.Always, "Business State", 22, "BusinessState", 1, "Address" },
                    { 13, SyncBehavior.Always, "Business Zip", 23, "BusinessPostalCode", 1, "Address" },
                    { 14, SyncBehavior.Nosync, "Home Address", 24, "HomeAddress", 1, "Address" },
                    { 15, SyncBehavior.Always, "Office Location", 30, "OfficeLocation", 1, "Organization" },
                    { 16, SyncBehavior.AddMissing, "Department", 31, "Department", 1, "Organization" },
                    { 17, SyncBehavior.Nosync, "Manager", 32, "Manager", 1, "Organization" },
                    { 18, SyncBehavior.AddMissing, "Notes", 40, "PersonalNotes", 1, "Extras" },
                    { 19, SyncBehavior.Nosync, "Birthday", 41, "Birthday", 1, "Extras" },
                    { 20, SyncBehavior.Nosync, "Nickname", 42, "NickName", 1, "Extras" },
                    { 21, SyncBehavior.Always, "Contact Photo", 50, "Photo", 1, "Photo" }
                });

            migrationBuilder.InsertData(
                table: "tunnels",
                columns: new[] { "id", "field_profile_id", "name", "source_display_name", "source_identifier", "source_type", "stale_hold_days", "stale_policy", "status", "target_scope", "target_user_filter" },
                values: new object[,]
                {
                    { 1, 1, "Buckhead", "Buckhead Office DDG", "buckhead-ddg@atlantafinehomes.com", SourceType.Ddg, 14, StalePolicy.FlagHold, TunnelStatus.Active, TargetScope.AllUsers, null },
                    { 2, 1, "North Atlanta", "North Atlanta Office DDG", "northatlanta-ddg@atlantafinehomes.com", SourceType.Ddg, 14, StalePolicy.FlagHold, TunnelStatus.Active, TargetScope.AllUsers, null },
                    { 3, 1, "Intown", "Intown Office DDG", "intown-ddg@atlantafinehomes.com", SourceType.Ddg, 14, StalePolicy.FlagHold, TunnelStatus.Active, TargetScope.AllUsers, null },
                    { 4, 1, "Blue Ridge", "Blue Ridge Office DDG", "blueridge-ddg@atlantafinehomes.com", SourceType.Ddg, 14, StalePolicy.FlagHold, TunnelStatus.Active, TargetScope.AllUsers, null },
                    { 5, 1, "Cobb", "Cobb Office DDG", "cobb-ddg@atlantafinehomes.com", SourceType.Ddg, 14, StalePolicy.FlagHold, TunnelStatus.Active, TargetScope.AllUsers, null },
                    { 6, 1, "Clayton", "Clayton Office DDG", "clayton-ddg@atlantafinehomes.com", SourceType.Ddg, 14, StalePolicy.FlagHold, TunnelStatus.Active, TargetScope.AllUsers, null }
                });

            migrationBuilder.InsertData(
                table: "tunnel_phone_lists",
                columns: new[] { "id", "phone_list_id", "tunnel_id" },
                values: new object[,]
                {
                    { 1, 1, 1 },
                    { 2, 2, 1 },
                    { 3, 1, 2 },
                    { 4, 2, 2 },
                    { 5, 1, 3 },
                    { 6, 2, 3 },
                    { 7, 1, 4 },
                    { 8, 2, 4 },
                    { 9, 1, 5 },
                    { 10, 2, 5 },
                    { 11, 1, 6 },
                    { 12, 2, 6 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_key",
                table: "app_settings",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_contact_sync_state_composite",
                table: "contact_sync_state",
                columns: new[] { "source_user_id", "phone_list_id", "target_mailbox_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_contact_sync_state_list",
                table: "contact_sync_state",
                column: "phone_list_id");

            migrationBuilder.CreateIndex(
                name: "idx_contact_sync_state_source",
                table: "contact_sync_state",
                column: "source_user_id");

            migrationBuilder.CreateIndex(
                name: "idx_contact_sync_state_stale",
                table: "contact_sync_state",
                column: "is_stale",
                filter: "is_stale = TRUE");

            migrationBuilder.CreateIndex(
                name: "idx_contact_sync_state_target",
                table: "contact_sync_state",
                column: "target_mailbox_id");

            migrationBuilder.CreateIndex(
                name: "IX_contact_sync_state_tunnel_id",
                table: "contact_sync_state",
                column: "tunnel_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_profile_fields_field_profile_id_field_name",
                table: "field_profile_fields",
                columns: new[] { "field_profile_id", "field_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_source_users_enabled",
                table: "source_users",
                column: "is_enabled");

            migrationBuilder.CreateIndex(
                name: "idx_source_users_entra",
                table: "source_users",
                column: "entra_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_sync_run_items_run",
                table: "sync_run_items",
                column: "sync_run_id");

            migrationBuilder.CreateIndex(
                name: "idx_sync_run_items_tunnel",
                table: "sync_run_items",
                column: "tunnel_id");

            migrationBuilder.CreateIndex(
                name: "IX_sync_run_items_phone_list_id",
                table: "sync_run_items",
                column: "phone_list_id");

            migrationBuilder.CreateIndex(
                name: "IX_sync_run_items_source_user_id",
                table: "sync_run_items",
                column: "source_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sync_run_items_target_mailbox_id",
                table: "sync_run_items",
                column: "target_mailbox_id");

            migrationBuilder.CreateIndex(
                name: "idx_sync_runs_started",
                table: "sync_runs",
                column: "started_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "idx_sync_runs_status",
                table: "sync_runs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_target_mailboxes_entra",
                table: "target_mailboxes",
                column: "entra_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tunnel_phone_lists_phone_list_id",
                table: "tunnel_phone_lists",
                column: "phone_list_id");

            migrationBuilder.CreateIndex(
                name: "IX_tunnel_phone_lists_tunnel_id_phone_list_id",
                table: "tunnel_phone_lists",
                columns: new[] { "tunnel_id", "phone_list_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_tunnels_status",
                table: "tunnels",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_tunnels_field_profile_id",
                table: "tunnels",
                column: "field_profile_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_settings");

            migrationBuilder.DropTable(
                name: "contact_sync_state");

            migrationBuilder.DropTable(
                name: "field_profile_fields");

            migrationBuilder.DropTable(
                name: "sync_run_items");

            migrationBuilder.DropTable(
                name: "tunnel_phone_lists");

            migrationBuilder.DropTable(
                name: "source_users");

            migrationBuilder.DropTable(
                name: "sync_runs");

            migrationBuilder.DropTable(
                name: "target_mailboxes");

            migrationBuilder.DropTable(
                name: "phone_lists");

            migrationBuilder.DropTable(
                name: "tunnels");

            migrationBuilder.DropTable(
                name: "field_profiles");
        }
    }
}
