using AFHSync.Api.Migrations;
using AFHSync.Shared.Data;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace AFHSync.Tests.Integration;

/// <summary>
/// Phase 2 migration tests. The Postgres-backed test creates a throw-away database, runs
/// MigrateAsync to the latest migration and asserts the Phase 2 schema; it is skipped when
/// no server is reachable. The operations test needs no database.
/// </summary>
[Trait("Category", "Integration")]
public class MigrationTests
{
    [Fact]
    public void Phase2Migration_DeletesIdLessContactSyncStateRows()
    {
        var migration = new Phase2DataIntegrity();

        var sql = migration.UpOperations.OfType<SqlOperation>().Select(o => o.Sql).ToList();

        Assert.Contains(sql, s => s.Contains("DELETE FROM contact_sync_state WHERE graph_contact_id IS NULL", StringComparison.OrdinalIgnoreCase));
    }

    [PostgresFact]
    public async Task MigrateAsync_CreatesPhase2Columns_Table_And_UniqueIndex()
    {
        var dbName = "afhsync_mig_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(PostgresTestServer.AdminConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var testConnectionString = new NpgsqlConnectionStringBuilder(PostgresTestServer.AdminConnectionString)
            {
                Database = dbName
            }.ToString();

            var options = new DbContextOptionsBuilder<AFHSyncDbContext>()
                .UseNpgsql(testConnectionString, o =>
                {
                    o.MigrationsAssembly("AFHSync.Api");
                    o.MapEnum<SourceType>("source_type");
                    o.MapEnum<TargetScope>("target_scope");
                    o.MapEnum<StalePolicy>("stale_policy");
                    o.MapEnum<SyncBehavior>("sync_behavior");
                    o.MapEnum<SyncStatus>("sync_status");
                    o.MapEnum<TunnelStatus>("tunnel_status");
                    o.MapEnum<RunType>("run_type");
                    o.MapEnum<CleanupJobStatus>("cleanup_job_status");
                })
                .Options;

            await using (var db = new AFHSyncDbContext(options))
            {
                await db.Database.MigrateAsync();

                var mailboxColumns = await db.Database
                    .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'target_mailboxes'")
                    .ToListAsync();
                Assert.Contains("mailbox_unavailable_at", mailboxColumns);
                Assert.Contains("mailbox_last_probed_at", mailboxColumns);
                Assert.Contains("mailbox_unavailable_reason", mailboxColumns);

                var runColumns = await db.Database
                    .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'sync_runs'")
                    .ToListAsync();
                Assert.Contains("requested_tunnel_ids", runColumns);

                var folderColumns = await db.Database
                    .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'tunnel_mailbox_folders'")
                    .ToListAsync();
                Assert.Equal(
                    new[] { "folder_name", "graph_folder_id", "id", "target_mailbox_id", "tunnel_id", "updated_at" },
                    folderColumns.OrderBy(c => c).ToArray());

                var indexDefs = await db.Database
                    .SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'tunnel_mailbox_folders' AND indexname = 'idx_tunnel_mailbox_folders_tunnel_mailbox'")
                    .ToListAsync();
                var unique = Assert.Single(indexDefs);
                Assert.Contains("UNIQUE", unique);
                Assert.Contains("(tunnel_id, target_mailbox_id)", unique);

                var enums = await db.Database
                    .SqlQueryRaw<string>("SELECT typname AS \"Value\" FROM pg_type WHERE typtype = 'e'")
                    .ToListAsync();
                Assert.Contains("sync_status", enums);
                Assert.Contains("run_type", enums);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
