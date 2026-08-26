# Sync Reliability — Phase 2 Implementation Plan (Data integrity)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a sync run's bookkeeping trustworthy: unavailable mailboxes stop failing every run, dry runs write nothing, a failed source never triggers a stale pass, folders are tracked by Graph id, state is persisted per 20-op chunk, runs are claimed by id and finalized `Cancelled` on worker shutdown, and phone-side notes survive updates.

**Architecture:** One EF Core migration adds three availability columns to `target_mailboxes`, a `tunnel_mailbox_folders` table and `sync_runs.requested_tunnel_ids`, and deletes id-less `contact_sync_state` rows. In the worker, a new `RunClaimService` owns the advisory-lock guard + claim/create for both engines; `RunReconciler` fails `Running` rows at startup; `SyncEngine.ProcessMailboxAsync` gets an unavailable-mailbox classifier, dry-run guards, a `skipStale` flag and per-chunk persistence through new `onChunkCompleted` callbacks on `IContactWriter`; `ContactFolderManager` resolves folders by remembered id first. The API creates the run row (with the requested tunnel ids) and enqueues exactly one Hangfire job addressed by run id; a small `GET /api/targets/unavailable` endpoint feeds a new section on the Targets page.

**Tech Stack:** .NET 10 / ASP.NET Core (api), .NET 10 worker with Hangfire 1.8.23 (PostgreSQL storage), EF Core 10 + Npgsql (Postgres in prod, InMemory in unit tests), Microsoft.Graph 5.x, xUnit 2.9; Next.js 15 / React / TypeScript frontend with TanStack Query and shadcn-style `ui/` components.

**Spec:** `docs/superpowers/specs/2026-08-25-sync-reliability-design.md` — Phase 2 section (§2.0–§2.10) is the binding authority. Phase 1 conventions this plan builds on: `tunnelErrors` (run-level error list), `DdgTargetFailure`/`TargetFilterResolution`, and `SyncRunItem` rows with `Action="failed"`, `TunnelId` set and no source user/mailbox.

## Global Constraints

- Branch: `sync-reliability/phase-2` (already checked out; the spec commit is HEAD). PR target: `main` on `github.com/nickafh/sync`.
- Commit after every task. Use `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` as the last line of each commit message.
- Run all shell commands from the repo root `/Users/nick/Documents/Code/AFHsync` unless a step says otherwise.
- Backend gate: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet` (and `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet` for the migration task and the final verification). Baseline (Task 0): unit `Passed: 221, Skipped: 1`; integration `Passed: 34`.
- Frontend gate: `cd frontend && npm run build` (there is no component test harness).
- Dry runs must never write to Graph or to `contact_sync_state` (no folder create, no rename, no duplicate cleanup, no state insert/update/delete, no stale pass). Run items are still emitted.
- Use `CancellationToken.None` for every finalize/bookkeeping write that must survive cancellation (run claim, per-chunk state persistence, unavailable-mailbox stamps, folder-row upserts, item flush, finalize).
- Exactly **one** migration for the whole phase: `Phase2DataIntegrity` (Task 1). Later tasks must not add migrations; if a later task needs a schema change, stop and revisit Task 1.
- `IsActive` on `target_mailboxes` keeps its meaning (exists and enabled in Entra). The `IsActive=false` self-heal on the no-mailbox error is removed (Task 5).
- Copy rules (verbatim strings the spec and UI depend on): unavailable classifier code `MailboxNotEnabledForRESTAPI` / message fragment `inactive, soft-deleted, or is hosted on-premise`; error summaries `worker shutting down` and `interrupted by worker restart`; batch error `no contact id in response`; run-item messages `Source '{name}': {reason}` and `DDG target '{name}': {reason}`; tunnel-rename warning `The contact folder will be renamed on every phone at the next sync.`
- Keep `SyncEngine.cs` edits surgical: locate them by the `// Step N` comments and the quoted code, not by line number (lines drift as tasks land).

---

## File map

| File | Responsibility |
|---|---|
| `shared/AFHSync.Shared.csproj` | adds `Hangfire.Core` so `ISyncEngine` can carry `[AutomaticRetry]` |
| `shared/Entities/TargetMailbox.cs` | + `MailboxUnavailableAt`, `MailboxLastProbedAt`, `MailboxUnavailableReason` |
| `shared/Entities/SyncRun.cs` | + `RequestedTunnelIds` (JSON int array, null = all) |
| `shared/Entities/TunnelMailboxFolder.cs` (new) | remembered Graph folder id per (tunnel, mailbox) |
| `shared/Data/Configurations/TargetMailboxConfiguration.cs`, `SyncRunConfiguration.cs`, `TunnelMailboxFolderConfiguration.cs` (new) | column names, unique index, cascade FKs |
| `shared/Data/AFHSyncDbContext.cs` | + `DbSet<TunnelMailboxFolder> TunnelMailboxFolders` |
| `api/Migrations/<timestamp>_Phase2DataIntegrity.cs` (+ Designer, snapshot) | the one migration, incl. `DELETE FROM contact_sync_state WHERE graph_contact_id IS NULL` |
| `shared/Services/ISyncEngine.cs` | `RunAsync(int? runId, …)` + `[AutomaticRetry(Attempts = 0)]` |
| `worker/Services/IRunClaimService.cs`, `RunClaimService.cs` (new) | advisory lock + one-lane guard + claim-by-id / create, shared by both engines |
| `worker/Services/RunReconciler.cs` (new) | startup: `Running` → `Failed "interrupted by worker restart"`, clear `cancel_sync` |
| `worker/Services/StaleRunCleanupService.cs` | also fails `Pending` rows older than 10 minutes |
| `worker/Services/MailboxAvailability.cs` (new) | classifier for the no-REST-mailbox error + 7-day re-probe interval |
| `worker/Services/SyncEngine.cs` | claim by id; cancellation at tunnel/mailbox boundaries; unavailable stamps + exclusion; source failures ⇒ `skipStale`; dry-run guards; per-chunk persistence |
| `worker/Services/ISourceResolver.cs`, `SourceResolver.cs` | `SourceResolution` / `SourceFailure` |
| `worker/Services/IStaleContactHandler.cs` (unchanged), `StaleContactHandler.cs` | stale reset for returning users |
| `worker/Services/IContactFolderManager.cs`, `ContactFolderManager.cs` | `(tunnel, mailbox, isDryRun)` signature; id → 404 fallthrough → name → create → upsert → rename |
| `worker/Services/IContactWriter.cs`, `ContactWriter.cs` | `onChunkCompleted` on create/update batches; no-id ⇒ `Success=false`; `MapPayloadToContact(payload, isCreate)` |
| `worker/Services/IPhotoSyncService.cs`, `PhotoSyncService.cs` | claims its row through `IRunClaimService`; retry off; new folder-manager call |
| `worker/Program.cs` | DI for the new services; startup reconcile before the Hangfire server; shutdown timeouts |
| `compose.yaml` | `stop_grace_period: 60s` on the worker |
| `api/Controllers/SyncRunsController.cs` | creates the row with `RequestedTunnelIds`, enqueues ONE job by run id, enqueue failure ⇒ `Failed` |
| `api/Controllers/TargetsController.cs` (new), `api/DTOs/UnavailableMailboxesDto.cs` (new) | `GET /api/targets/unavailable` |
| `frontend/src/types/targets.ts` (new), `frontend/src/lib/api.ts`, `frontend/src/hooks/use-targets.ts` (new), `frontend/src/components/UnavailableMailboxes.tsx` (new), `frontend/src/app/(app)/lists/page.tsx` | Unavailable mailboxes section on the Targets page |
| `frontend/src/app/(app)/tunnels/[id]/page.tsx`, `frontend/src/components/ImpactPreviewDialog.tsx` | tunnel rename is high-impact with the folder-rename warning |
| `tests/AFHSync.Tests.Integration/PostgresFactAttribute.cs` (new), `MigrationTests.cs` (rewrite), `Api/SyncRunsControllerTests.cs`, `Api/TargetsControllerTests.cs` (new) | integration tests |
| `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`, `RunReconcilerTests.cs` (new), `StaleRunCleanupServiceTests.cs` (new), `MailboxAvailabilityTests.cs` (new), `SourceResolverTests.cs`, `StaleContactHandlerTests.cs`, `ContactFolderManagerTests.cs`, `ContactWriterTests.cs`, `PhotoSyncServiceTests.cs` | unit tests + per-file fakes |

---

### Task 0: Baseline

**Files:** none

- [ ] **Step 1: Confirm branch and clean tree**

Run: `git status --short && git branch --show-current && git log --oneline -1`
Expected: no output from status; branch `sync-reliability/phase-2`; HEAD is `6e4aea4 docs(spec): Phase 2 data-integrity design…`.

- [ ] **Step 2: Record the backend baseline**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 221, Skipped: 1, Total: 222`.

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 34, Skipped: 0, Total: 34`. (The integration project uses `WebApplicationFactory` with the InMemory provider — it does **not** need Postgres today. Task 1 adds the first test that does, and it self-skips when no server is reachable.) A `NU1903` warning about `Microsoft.Kiota.Abstractions` is pre-existing; ignore it.

- [ ] **Step 3: Record the frontend baseline**

Run: `cd frontend && (test -d node_modules || npm install) && npm run build 2>&1 | tail -5; cd ..`
Expected: `✓ Compiled successfully` and the route table; no type errors.

- [ ] **Step 4: Verify the EF tooling Task 1 relies on**

`dotnet ef` is installed as a global tool (there is no `.config/dotnet-tools.json`); `global.json` pins SDK `10.0.100`.

Run: `dotnet ef --version`
Expected: `Entity Framework Core .NET Command-line Tools` / `10.0.5`. If the command is not found, install it once: `dotnet tool install --global dotnet-ef --version 10.0.5` and re-run.

Run: `dotnet ef migrations list --project api --startup-project api --no-connect 2>&1 | tail -3`
Expected: the last line lists `20260713141115_AddPhotoCheckedAtToSourceUser` followed by `Pending status not shown. Unable to determine which migrations have been applied…` (that trailer is normal with `--no-connect`). This proves the design-time host builds; `migrations add` in Task 1 uses the same path.

- [ ] **Step 5: Check whether a Postgres server is reachable (informational)**

Run: `nc -z -w 2 localhost 5432 && echo "5432 open" || echo "5432 closed"`
Expected on the dev laptop with Docker stopped: `5432 closed` — the Postgres-backed migration test from Task 1 will report `Skipped`. On the box (or with `docker compose up -d postgres`) it runs for real; set `AFHSYNC_TEST_PG` to point it at another server.

---

### Task 1: Migration, entities and DbContext (§2.0)

**Files:**
- Modify: `shared/Entities/TargetMailbox.cs`
- Modify: `shared/Entities/SyncRun.cs`
- Create: `shared/Entities/TunnelMailboxFolder.cs`
- Modify: `shared/Data/Configurations/TargetMailboxConfiguration.cs`
- Modify: `shared/Data/Configurations/SyncRunConfiguration.cs`
- Create: `shared/Data/Configurations/TunnelMailboxFolderConfiguration.cs`
- Modify: `shared/Data/AFHSyncDbContext.cs`
- Create (generated): `api/Migrations/<timestamp>_Phase2DataIntegrity.cs`, `…Designer.cs`; regenerated `api/Migrations/AFHSyncDbContextModelSnapshot.cs`
- Create: `tests/AFHSync.Tests.Integration/PostgresFactAttribute.cs`
- Rewrite: `tests/AFHSync.Tests.Integration/MigrationTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // shared/Entities/TargetMailbox.cs (additions)
  public DateTime? MailboxUnavailableAt { get; set; }      // column mailbox_unavailable_at
  public DateTime? MailboxLastProbedAt { get; set; }       // column mailbox_last_probed_at
  public string? MailboxUnavailableReason { get; set; }    // column mailbox_unavailable_reason

  // shared/Entities/SyncRun.cs (addition)
  public string? RequestedTunnelIds { get; set; }          // column requested_tunnel_ids, JSON int[] or null

  // shared/Entities/TunnelMailboxFolder.cs
  public class TunnelMailboxFolder { int Id; int TunnelId; int TargetMailboxId; string GraphFolderId; string FolderName; DateTime UpdatedAt; Tunnel Tunnel; TargetMailbox TargetMailbox; }

  // shared/Data/AFHSyncDbContext.cs
  public DbSet<TunnelMailboxFolder> TunnelMailboxFolders
  ```
  Table `tunnel_mailbox_folders`, unique index `idx_tunnel_mailbox_folders_tunnel_mailbox` on `(tunnel_id, target_mailbox_id)`, both FKs cascade.
- Consumes: nothing from earlier tasks.

- [ ] **Step 1: Write the failing migration tests**

Create `tests/AFHSync.Tests.Integration/PostgresFactAttribute.cs`:

```csharp
using Npgsql;
using Xunit;

namespace AFHSync.Tests.Integration;

/// <summary>
/// A [Fact] that is skipped when no PostgreSQL server is reachable (dev laptops without Docker).
/// Point it at a server with AFHSYNC_TEST_PG (a connection string whose Database is the
/// maintenance DB, e.g. "Host=localhost;Port=5432;Username=afhsync;Password=…;Database=postgres").
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresTestServer.IsReachable)
            Skip = $"Postgres not reachable at {PostgresTestServer.HostDescription} — set AFHSYNC_TEST_PG to run";
    }
}

public static class PostgresTestServer
{
    public static string AdminConnectionString { get; } =
        Environment.GetEnvironmentVariable("AFHSYNC_TEST_PG")
        ?? "Host=localhost;Port=5432;Username=afhsync;Password=devpassword;Database=postgres;Timeout=3";

    public static string HostDescription { get; } =
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Password = null }.ToString();

    private static readonly Lazy<bool> Reachable = new(() =>
    {
        try
        {
            using var connection = new NpgsqlConnection(AdminConnectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    });

    public static bool IsReachable => Reachable.Value;
}
```

Replace the whole of `tests/AFHSync.Tests.Integration/MigrationTests.cs` with:

```csharp
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
```

- [ ] **Step 2: Run the integration tests to verify they fail to compile**

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | grep -E "error|Passed|Failed" | head -5`
Expected: build error `The type or namespace name 'Phase2DataIntegrity' does not exist in the namespace 'AFHSync.Api.Migrations'`.

- [ ] **Step 3: Add the entity properties and the new entity**

In `shared/Entities/TargetMailbox.cs`, after `public DateTime? LastVerifiedAt { get; set; }` add:

```csharp

    /// <summary>
    /// Phase 2 (§2.1): set the first time Graph reports the mailbox is not REST-enabled
    /// (soft-deleted / on-prem / unlicensed). Null = available. IsActive is unrelated: it
    /// still means "exists and enabled in Entra".
    /// </summary>
    public DateTime? MailboxUnavailableAt { get; set; }

    /// <summary>Last time the worker probed this mailbox and found it unavailable. Re-probed weekly.</summary>
    public DateTime? MailboxLastProbedAt { get; set; }

    /// <summary>The Graph error message from the last unavailable probe.</summary>
    public string? MailboxUnavailableReason { get; set; }
```

In `shared/Entities/SyncRun.cs`, replace the `HangfireJobIds` doc comment and add the new property so the block reads:

```csharp
    /// <summary>
    /// Hangfire background-job ID enqueued for this run (Phase 2: exactly one job per run,
    /// addressed by run id). The stop endpoint / StaleRunCleanupService call
    /// BackgroundJob.Delete on it so a queued-but-not-yet-started job can't resurrect a
    /// cancelled run. Kept as a string (historically comma-separated) for compatibility.
    /// </summary>
    public string? HangfireJobIds { get; set; }

    /// <summary>
    /// Phase 2 (§2.7): JSON array of tunnel ids this run was asked to process (e.g. "[3,5]").
    /// Null = all active tunnels. Written by the API when it creates the row; the worker
    /// reads it after claiming the row and never trusts the job arguments for this.
    /// </summary>
    public string? RequestedTunnelIds { get; set; }
```

Create `shared/Entities/TunnelMailboxFolder.cs`:

```csharp
namespace AFHSync.Shared.Entities;

/// <summary>
/// Phase 2 (§2.5): the Graph contact folder the worker last used for a (tunnel, mailbox)
/// pair. Lets the folder be found by id after the tunnel is renamed, so a rename becomes a
/// PATCH of displayName on every phone instead of a brand-new folder.
/// </summary>
public class TunnelMailboxFolder
{
    public int Id { get; set; }
    public int TunnelId { get; set; }
    public int TargetMailboxId { get; set; }
    public string GraphFolderId { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public Tunnel Tunnel { get; set; } = null!;
    public TargetMailbox TargetMailbox { get; set; } = null!;
}
```

- [ ] **Step 4: Map the columns**

In `shared/Data/Configurations/TargetMailboxConfiguration.cs`, after the `LastVerifiedAt` line add:

```csharp
        builder.Property(e => e.MailboxUnavailableAt).HasColumnName("mailbox_unavailable_at");
        builder.Property(e => e.MailboxLastProbedAt).HasColumnName("mailbox_last_probed_at");
        builder.Property(e => e.MailboxUnavailableReason).HasColumnName("mailbox_unavailable_reason");
```

In `shared/Data/Configurations/SyncRunConfiguration.cs`, after the `HangfireJobIds` line add:

```csharp
        builder.Property(e => e.RequestedTunnelIds).HasColumnName("requested_tunnel_ids");
```

Create `shared/Data/Configurations/TunnelMailboxFolderConfiguration.cs`:

```csharp
using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class TunnelMailboxFolderConfiguration : IEntityTypeConfiguration<TunnelMailboxFolder>
{
    public void Configure(EntityTypeBuilder<TunnelMailboxFolder> builder)
    {
        builder.ToTable("tunnel_mailbox_folders");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TunnelId).HasColumnName("tunnel_id").IsRequired();
        builder.Property(e => e.TargetMailboxId).HasColumnName("target_mailbox_id").IsRequired();
        builder.Property(e => e.GraphFolderId).HasColumnName("graph_folder_id").HasMaxLength(300).IsRequired();
        builder.Property(e => e.FolderName).HasColumnName("folder_name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");

        builder.HasIndex(e => new { e.TunnelId, e.TargetMailboxId })
            .IsUnique()
            .HasDatabaseName("idx_tunnel_mailbox_folders_tunnel_mailbox");

        builder.HasOne(e => e.Tunnel)
            .WithMany()
            .HasForeignKey(e => e.TunnelId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.TargetMailbox)
            .WithMany()
            .HasForeignKey(e => e.TargetMailboxId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

In `shared/Data/AFHSyncDbContext.cs`, after the `CleanupJobs` DbSet add:

```csharp
    public DbSet<TunnelMailboxFolder> TunnelMailboxFolders => Set<TunnelMailboxFolder>();
```

- [ ] **Step 5: Generate the migration**

Run: `dotnet ef migrations add Phase2DataIntegrity --project api --startup-project api 2>&1 | tail -3`
Expected: `Build succeeded.` then `Done. To undo this action, use 'ef migrations remove'`.

Run: `ls api/Migrations | grep Phase2DataIntegrity`
Expected: `<timestamp>_Phase2DataIntegrity.Designer.cs` and `<timestamp>_Phase2DataIntegrity.cs`.

Open `api/Migrations/<timestamp>_Phase2DataIntegrity.cs` and check that `Up()` contains, in some order:
- `migrationBuilder.AddColumn<DateTime>(name: "mailbox_last_probed_at", table: "target_mailboxes", type: "timestamp with time zone", nullable: true)`
- `AddColumn<DateTime>(name: "mailbox_unavailable_at", …)` and `AddColumn<string>(name: "mailbox_unavailable_reason", table: "target_mailboxes", type: "text", nullable: true)`
- `AddColumn<string>(name: "requested_tunnel_ids", table: "sync_runs", type: "text", nullable: true)`
- `migrationBuilder.CreateTable(name: "tunnel_mailbox_folders", …)` with columns `id`, `tunnel_id`, `target_mailbox_id`, `graph_folder_id` (`character varying(300)`), `folder_name` (`character varying(200)`), `updated_at` (`defaultValueSql: "NOW()"`), and two `ForeignKey` entries with `onDelete: ReferentialAction.Cascade`
- `migrationBuilder.CreateIndex(name: "idx_tunnel_mailbox_folders_tunnel_mailbox", table: "tunnel_mailbox_folders", columns: new[] { "tunnel_id", "target_mailbox_id" }, unique: true)` plus EF's automatic `IX_tunnel_mailbox_folders_target_mailbox_id`.

If any of those is missing, the entity/configuration edit in Steps 3–4 was not picked up: run `dotnet ef migrations remove --project api --startup-project api`, fix, and regenerate.

- [ ] **Step 6: Add the data fix-up to `Up()`**

In the generated `Up()` method, as the **last** statement (after the `CreateIndex` calls), add:

```csharp

            // Phase 2 (§2.0): dry-run artifacts and lost-id creates. A state row without a Graph
            // contact id can never be updated or deleted; the next real run recreates the contact.
            // Deploy step 1 counts these first (see the plan's Task 13).
            migrationBuilder.Sql("DELETE FROM contact_sync_state WHERE graph_contact_id IS NULL;");
```

`Down()` needs no counterpart for the delete (the rows are unrecoverable by design); leave the generated `DropTable`/`DropColumn` calls as they are.

- [ ] **Step 7: Run the integration and unit tests**

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 35, Skipped: 1, Total: 36` on a laptop without Postgres (the `[PostgresFact]` is skipped); `Passed: 36, Skipped: 0` when a server is reachable (e.g. `docker compose up -d postgres` and `AFHSYNC_TEST_PG="Host=localhost;Port=5432;Username=afhsync;Password=<POSTGRES_PASSWORD from .env>;Database=postgres"`).

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 221, Skipped: 1` (unchanged — nothing consumes the new columns yet).

- [ ] **Step 8: Commit**

```bash
git add shared/Entities/TargetMailbox.cs shared/Entities/SyncRun.cs shared/Entities/TunnelMailboxFolder.cs shared/Data/Configurations/TargetMailboxConfiguration.cs shared/Data/Configurations/SyncRunConfiguration.cs shared/Data/Configurations/TunnelMailboxFolderConfiguration.cs shared/Data/AFHSyncDbContext.cs api/Migrations tests/AFHSync.Tests.Integration/PostgresFactAttribute.cs tests/AFHSync.Tests.Integration/MigrationTests.cs
git commit -m "feat(db): Phase 2 migration — mailbox availability columns, tunnel_mailbox_folders, requested_tunnel_ids, id-less state cleanup

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Run claiming by id (§2.7 — API creates the row, one job per run, worker claims it)

**Files:**
- Modify: `shared/AFHSync.Shared.csproj`
- Modify: `shared/Services/ISyncEngine.cs`
- Create: `worker/Services/IRunClaimService.cs`
- Create: `worker/Services/RunClaimService.cs`
- Modify: `worker/Services/SyncEngine.cs` (constructor, `RunAsync` guard block, `LoadTunnelsAsync`)
- Modify: `worker/Program.cs` (DI registration)
- Modify: `api/Controllers/SyncRunsController.cs` (`TriggerSync`)
- Test: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`
- Test: `tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // shared/Services/ISyncEngine.cs
  [AutomaticRetry(Attempts = 0)]
  Task<SyncRun> RunAsync(int? runId, RunType runType, bool isDryRun, CancellationToken ct);
  // runId given  ⇒ claim that row (Pending → Running); not Pending ⇒ return it untouched, no work.
  // runId null   ⇒ create a new Running row from runType/isDryRun (cron path).
  // After claim, RunType / IsDryRun / RequestedTunnelIds are read from the row, never the arguments.

  // worker/Services/IRunClaimService.cs
  public enum RunClaimOutcome { Claimed, Blocked, NotFound, AlreadyFinalized }
  public sealed record RunClaimResult(RunClaimOutcome Outcome, SyncRun? Run);
  public interface IRunClaimService
  {
      Task<RunClaimResult> ClaimAsync(int? runId, RunType runType, bool isDryRun, CancellationToken ct);
  }
  // SyncEngine
  internal static IReadOnlyList<int>? ParseRequestedTunnelIds(string? json);   // null = all; [] = none (unreadable)
  ```
  `SyncEngine`'s primary constructor gains `IRunClaimService runClaimService` immediately after `IRunLogger runLogger`.
  `FakeRunLogger` (SyncEngineTests) gains `public SyncStatus? FinalizedStatus`; `FakeSourceResolver` gains `public List<int> ResolvedTunnelIds`.
- Consumes: `SyncRun.RequestedTunnelIds` (Task 1).
- Callers that keep compiling unchanged (their `null` first argument now means "create a new row"): `api/Controllers/SettingsController.cs` (`engine.RunAsync(null, RunType.Scheduled, false, CancellationToken.None)`) and `worker/Program.cs` `sync-all` recurring job. Verify in Step 8; do not edit them.

- [ ] **Step 1: Update the engine tests to the new signature and add the claim tests**

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`:

(a) In `CreateEngine`, after the `runLogger ?? new FakeRunLogger(),` argument insert a new argument line:

```csharp
            new RunClaimService(CreateFactory(dbName), NullLogger<RunClaimService>.Instance),
```

(b) Replace the `FakeSourceResolver` class with:

```csharp
    private sealed class FakeSourceResolver(List<SourceUser> users) : ISourceResolver
    {
        public int ResolveCallCount { get; private set; }
        public List<int> ResolvedTunnelIds { get; } = [];

        public Task<List<SourceUser>> ResolveAsync(Tunnel tunnel, CancellationToken ct)
        {
            ResolveCallCount++;
            ResolvedTunnelIds.Add(tunnel.Id);
            return Task.FromResult(users);
        }
    }
```

(c) In `FakeRunLogger`, add the property `public SyncStatus? FinalizedStatus { get; private set; }` next to `FinalizedErrorSummary`, and in `FinalizeRunAsync` add `FinalizedStatus = status;` next to `FinalizedErrorSummary = errorSummary;`.

(d) Add these tests after `RunAsync_CreatesAndFinalizesSyncRunWithNoTunnels`:

```csharp
    // ==============================
    // Phase 2 (2.7): explicit run claiming
    // ==============================

    [Fact]
    public async Task RunAsync_WithRunId_ClaimsThatRowAndReadsTunnelsAndDryRunFromIt()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var seedCtx = MakeDbContext(dbName))
        {
            var t1 = new Tunnel { Id = 1, Name = "T1", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
            var t2 = new Tunnel { Id = 2, Name = "T2", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
            var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
            var tpl = new TunnelPhoneList { TunnelId = 2, PhoneListId = 1, Tunnel = t2, PhoneList = phoneList };
            t2.TunnelPhoneLists.Add(tpl);
            seedCtx.Tunnels.AddRange(t1, t2);
            seedCtx.PhoneLists.Add(phoneList);
            seedCtx.TunnelPhoneLists.Add(tpl);
            seedCtx.TargetMailboxes.Add(new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
            // The API created this row: dry run, tunnel 2 only. The job arguments below say
            // otherwise (Manual, not dry) and must be ignored.
            seedCtx.SyncRuns.Add(new SyncRun
            {
                Id = 7, RunType = RunType.DryRun, Status = SyncStatus.Pending, IsDryRun = true,
                RequestedTunnelIds = "[2]", CreatedAt = DateTime.UtcNow
            });
            await seedCtx.SaveChangesAsync();
        }

        var sourceResolver = new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]);
        var contactWriter = new FakeContactWriter();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, contactWriter: contactWriter, runLogger: runLogger);

        var run = await engine.RunAsync(7, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(7, run.Id);
        Assert.Equal(new[] { 2 }, sourceResolver.ResolvedTunnelIds);
        Assert.Empty(contactWriter.CreatedContactIds);                       // dry run honoured from the row
        Assert.Contains(runLogger.AddedItems, i => i.Action == "created");    // but the dry run still reports
        Assert.True(runLogger.WasFinalized);

        await using var verifyCtx = MakeDbContext(dbName);
        var row = await verifyCtx.SyncRuns.SingleAsync(r => r.Id == 7);
        Assert.NotNull(row.StartedAt);
        Assert.Equal(1, await verifyCtx.SyncRuns.CountAsync());             // no second row was created
    }

    [Fact]
    public async Task RunAsync_WithFinalizedRunId_ReturnsRowUntouchedAndDoesNoWork()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.Tunnels.Add(new Tunnel { Id = 1, Name = "T1", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove });
            seedCtx.SyncRuns.Add(new SyncRun
            {
                Id = 9, RunType = RunType.Manual, Status = SyncStatus.Success, IsDryRun = false,
                StartedAt = DateTime.UtcNow.AddMinutes(-5), CompletedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow.AddMinutes(-6)
            });
            await seedCtx.SaveChangesAsync();
        }
        var sourceResolver = new FakeSourceResolver([]);
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, runLogger: runLogger);

        var run = await engine.RunAsync(9, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(9, run.Id);
        Assert.Equal(SyncStatus.Success, run.Status);
        Assert.Equal(0, sourceResolver.ResolveCallCount);
        Assert.False(runLogger.WasFinalized);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(1, await verifyCtx.SyncRuns.CountAsync());
    }

    [Fact]
    public async Task RunAsync_WithRunId_WhileAnotherRunIsRunning_FailsThatRowWithoutWork()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.SyncRuns.Add(new SyncRun { Id = 1, RunType = RunType.Scheduled, Status = SyncStatus.Running, StartedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
            seedCtx.SyncRuns.Add(new SyncRun { Id = 2, RunType = RunType.Manual, Status = SyncStatus.Pending, CreatedAt = DateTime.UtcNow });
            await seedCtx.SaveChangesAsync();
        }
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, runLogger: runLogger);

        var run = await engine.RunAsync(2, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(SyncStatus.Failed, run.Status);
        Assert.False(runLogger.WasFinalized);
        await using var verifyCtx = MakeDbContext(dbName);
        var row = await verifyCtx.SyncRuns.SingleAsync(r => r.Id == 2);
        Assert.Equal(SyncStatus.Failed, row.Status);
        Assert.Equal("another run was already in progress", row.ErrorSummary);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public void ParseRequestedTunnelIds_HandlesNullJsonAndGarbage()
    {
        Assert.Null(SyncEngine.ParseRequestedTunnelIds(null));
        Assert.Null(SyncEngine.ParseRequestedTunnelIds(""));
        Assert.Equal(new[] { 3, 5 }, SyncEngine.ParseRequestedTunnelIds("[3,5]")!);
        Assert.Empty(SyncEngine.ParseRequestedTunnelIds("not json")!);   // unreadable ⇒ process nothing, never "all"
    }
```

- [ ] **Step 2: Add the integration test for the API side**

In `tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs`, after `PostSync_Returns409_WhenRunAlreadyInProgress` add:

```csharp
    [Fact]
    public async Task PostSync_StoresRequestedTunnelIds_AndExactlyOneJobId()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        db.SyncRuns.RemoveRange(db.SyncRuns.Where(r => r.Status == SyncStatus.Running || r.Status == SyncStatus.Pending));
        await db.SaveChangesAsync();

        var response = await AuthenticatedPostAsync("/api/sync-runs", new
        {
            runType = "dry_run",
            isDryRun = true,
            tunnelIds = new[] { 3, 5 }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var runId = body.GetProperty("runId").GetInt32();

        var run = await db.SyncRuns.FindAsync(runId);
        Assert.NotNull(run);
        Assert.Equal(SyncStatus.Pending, run!.Status);
        Assert.True(run.IsDryRun);
        Assert.Equal(RunType.DryRun, run.RunType);
        Assert.Equal("[3,5]", run.RequestedTunnelIds);
        Assert.False(string.IsNullOrEmpty(run.HangfireJobIds));
        Assert.DoesNotContain(",", run.HangfireJobIds);   // one job, not one per tunnel
    }
```

- [ ] **Step 3: Run the unit tests to verify they fail to compile**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | grep -E "error CS" | head -3`
Expected: `error CS0246: The type or namespace name 'RunClaimService' could not be found`.

- [ ] **Step 4: Give the shared project Hangfire and change `ISyncEngine`**

In `shared/AFHSync.Shared.csproj`, inside the existing `<ItemGroup>` with the package references, add:

```xml
    <PackageReference Include="Hangfire.Core" Version="1.8.23" />
```

Replace the whole of `shared/Services/ISyncEngine.cs` with:

```csharp
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Hangfire;

namespace AFHSync.Shared.Services;

/// <summary>
/// Top-level orchestrator for the sync pipeline.
/// Resolves source members, builds payloads, delta-compares via hash,
/// writes to Graph, handles stale contacts, and produces a full audit trail.
/// Interface in shared project so API can reference it for Hangfire job enqueue
/// without a circular project dependency (Worker references API for DbContext).
/// </summary>
public interface ISyncEngine
{
    /// <summary>
    /// Executes a sync run.
    /// </summary>
    /// <param name="runId">
    /// Phase 2 (§2.7). When set, the worker claims that <c>sync_runs</c> row (Pending → Running)
    /// under the run-start advisory lock and reads RunType, IsDryRun and RequestedTunnelIds
    /// from it; a row that is no longer Pending is returned untouched and no work is done.
    /// When null (cron), a new row is created from <paramref name="runType"/> / <paramref name="isDryRun"/>.
    /// </param>
    /// <param name="runType">Used only when <paramref name="runId"/> is null.</param>
    /// <param name="isDryRun">Used only when <paramref name="runId"/> is null.</param>
    /// <param name="ct">
    /// Hangfire replaces the token passed at enqueue time (callers pass CancellationToken.None)
    /// with its own, which is signalled on worker shutdown and on job deletion.
    /// </param>
    /// <returns>The run record with its final status.</returns>
    [AutomaticRetry(Attempts = 0)]
    Task<SyncRun> RunAsync(
        int? runId,
        RunType runType,
        bool isDryRun,
        CancellationToken ct);
}
```

- [ ] **Step 5: Create the claim service**

Create `worker/Services/IRunClaimService.cs`:

```csharp
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;

namespace AFHSync.Worker.Services;

public enum RunClaimOutcome
{
    /// <summary>The row is now Running and belongs to this job.</summary>
    Claimed,
    /// <summary>Another run is Running (any run type — one lane). A requested Pending row was marked Failed.</summary>
    Blocked,
    /// <summary>No sync_runs row with the requested id.</summary>
    NotFound,
    /// <summary>The requested row is not Pending (Running, Success, Warning, Failed or Cancelled). Returned untouched.</summary>
    AlreadyFinalized
}

public sealed record RunClaimResult(RunClaimOutcome Outcome, SyncRun? Run);

/// <summary>
/// Phase 2 (§2.7): the single place that decides whether a run may start. Serialises on the
/// Postgres advisory lock (key 1) so two Hangfire workers cannot both pass the "is anything
/// Running?" guard. Used by SyncEngine and PhotoSyncService — contact runs and photo runs
/// share one lane because they write the same contacts.
/// </summary>
public interface IRunClaimService
{
    Task<RunClaimResult> ClaimAsync(int? runId, RunType runType, bool isDryRun, CancellationToken ct);
}
```

Create `worker/Services/RunClaimService.cs`:

```csharp
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace AFHSync.Worker.Services;

public sealed class RunClaimService(
    IDbContextFactory<AFHSyncDbContext> dbContextFactory,
    ILogger<RunClaimService> logger) : IRunClaimService
{
    public const string BlockedSummary = "another run was already in progress";

    public async Task<RunClaimResult> ClaimAsync(int? runId, RunType runType, bool isDryRun, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        // Advisory lock key 1 = sync run start serialisation. Postgres-specific and
        // transaction-scoped, so skip it on non-relational providers (the in-memory
        // provider used by unit tests) — mirrors the IsInMemory checks elsewhere.
        IDbContextTransaction? tx = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        await using var _tx = tx;
        if (tx is not null)
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1)", ct);

        SyncRun? requested = null;
        if (runId.HasValue)
        {
            requested = await db.SyncRuns.FirstOrDefaultAsync(r => r.Id == runId.Value, ct);
            if (requested is null)
            {
                await CommitAsync(tx, ct);
                return new RunClaimResult(RunClaimOutcome.NotFound, null);
            }
            if (requested.Status != SyncStatus.Pending)
            {
                await CommitAsync(tx, ct);
                return new RunClaimResult(RunClaimOutcome.AlreadyFinalized, requested);
            }
        }

        var now = DateTime.UtcNow;
        var alreadyRunning = await db.SyncRuns.AnyAsync(r => r.Status == SyncStatus.Running, ct);
        if (alreadyRunning)
        {
            if (requested is not null)
            {
                // Fail the requested row now rather than leaving it Pending for the 10-minute
                // cleanup — the UI shows the outcome immediately.
                requested.Status = SyncStatus.Failed;
                requested.CompletedAt = now;
                requested.ErrorSummary = BlockedSummary;
                await db.SaveChangesAsync(ct);
            }
            await CommitAsync(tx, ct);
            logger.LogWarning("Run claim blocked — another run is already Running (requested RunId={RunId})",
                runId?.ToString() ?? "new");
            return new RunClaimResult(RunClaimOutcome.Blocked, requested);
        }

        SyncRun run;
        if (requested is not null)
        {
            requested.Status = SyncStatus.Running;
            requested.StartedAt = now;
            run = requested;
        }
        else
        {
            run = new SyncRun
            {
                RunType = runType,
                Status = SyncStatus.Running,
                IsDryRun = isDryRun,
                StartedAt = now,
                CreatedAt = now
            };
            db.SyncRuns.Add(run);
        }
        await db.SaveChangesAsync(ct);
        await CommitAsync(tx, ct);

        logger.LogInformation("Claimed RunId={RunId} (RunType={RunType}, IsDryRun={IsDryRun}, RequestedTunnelIds={Tunnels})",
            run.Id, run.RunType, run.IsDryRun, run.RequestedTunnelIds ?? "all");
        return new RunClaimResult(RunClaimOutcome.Claimed, run);
    }

    private static async Task CommitAsync(IDbContextTransaction? tx, CancellationToken ct)
    {
        if (tx is not null)
            await tx.CommitAsync(ct);
    }
}
```

Register it in `worker/Program.cs`, directly after `services.AddScoped<IRunLogger, RunLogger>();`:

```csharp
    services.AddScoped<IRunClaimService, RunClaimService>();
```

- [ ] **Step 6: Rewire `SyncEngine.RunAsync`**

In `worker/Services/SyncEngine.cs`:

(a) Remove the now-unused `using Microsoft.EntityFrameworkCore.Storage;` line.

(b) In the primary constructor, insert `IRunClaimService runClaimService,` on its own line directly after `IRunLogger runLogger,`.

(c) Replace the method header and the whole guard block — from `public async Task<SyncRun> RunAsync(` down to and including the `logger.LogInformation("SyncEngine starting RunId={RunId}, TunnelId={TunnelId}, IsDryRun={IsDryRun}", …);` statement — with:

```csharp
    public async Task<SyncRun> RunAsync(
        int? runId,
        RunType runType,
        bool isDryRun,
        CancellationToken ct)
    {
        // Phase 2 (§2.7): claim (or create) the run row under the advisory lock. Once a row is
        // claimed, RunType / IsDryRun / the tunnel list come from the ROW, never the arguments —
        // the API decides what a run is when it creates the row; this job merely executes it.
        // CancellationToken.None: claiming is bookkeeping that must not be skipped by a
        // shutdown token (Task 4 finalizes such a run as Cancelled instead).
        var claim = await runClaimService.ClaimAsync(runId, runType, isDryRun, CancellationToken.None);
        switch (claim.Outcome)
        {
            case RunClaimOutcome.Blocked:
                logger.LogWarning("Skipping sync — another run is already in progress (requested RunId={RunId})",
                    runId?.ToString() ?? "new");
                return claim.Run ?? new SyncRun { Status = SyncStatus.Failed };
            case RunClaimOutcome.NotFound:
                logger.LogWarning("Sync run {RunId} does not exist — nothing to do", runId);
                return new SyncRun { Id = runId ?? 0, Status = SyncStatus.Failed };
            case RunClaimOutcome.AlreadyFinalized:
                logger.LogInformation("Sync run {RunId} is already {Status} — nothing to do", claim.Run!.Id, claim.Run.Status);
                return claim.Run;
        }

        var run = claim.Run!;
        isDryRun = run.IsDryRun;
        var requestedTunnelIds = ParseRequestedTunnelIds(run.RequestedTunnelIds);
        if (requestedTunnelIds is { Count: 0 })
            logger.LogError("RunId={RunId}: requested_tunnel_ids '{Json}' is unreadable — processing no tunnels",
                run.Id, run.RequestedTunnelIds);

        logger.LogInformation(
            "SyncEngine starting RunId={RunId}, RunType={RunType}, Tunnels={Tunnels}, IsDryRun={IsDryRun}",
            run.Id, run.RunType, requestedTunnelIds is null ? "all" : string.Join(",", requestedTunnelIds), isDryRun);
```

(d) In `// Step 3: Load tunnels.` change `var tunnels = await LoadTunnelsAsync(tunnelId, ct);` to `var tunnels = await LoadTunnelsAsync(requestedTunnelIds, ct);`.

(e) Replace the whole `LoadTunnelsAsync` method with:

```csharp
    private async Task<List<Tunnel>> LoadTunnelsAsync(IReadOnlyList<int>? tunnelIds, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        if (tunnelIds is not null)
        {
            // Explicit ids (manual trigger): no status filter — an operator may deliberately run
            // an inactive tunnel once. Missing ids are logged, not fatal.
            var ids = tunnelIds.ToList();
            var tunnels = await db.Tunnels
                .Where(t => ids.Contains(t.Id))
                .Include(t => t.TunnelSources)
                .Include(t => t.FieldProfile)
                    .ThenInclude(fp => fp!.FieldProfileFields)
                .Include(t => t.TunnelPhoneLists)
                    .ThenInclude(tpl => tpl.PhoneList)
                .ToListAsync(ct);

            foreach (var missing in ids.Where(id => tunnels.All(t => t.Id != id)))
                logger.LogWarning("Tunnel {TunnelId} not found", missing);

            return tunnels;
        }

        return await db.Tunnels
            .Where(t => t.Status == TunnelStatus.Active)
            .Include(t => t.TunnelSources)
            .Include(t => t.FieldProfile)
                .ThenInclude(fp => fp!.FieldProfileFields)
            .Include(t => t.TunnelPhoneLists)
                .ThenInclude(tpl => tpl.PhoneList)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Phase 2 (§2.7): sync_runs.requested_tunnel_ids is a JSON int array. Null/blank ⇒ all
    /// active tunnels. Unreadable JSON ⇒ an EMPTY list (process nothing) — never widen to "all".
    /// </summary>
    internal static IReadOnlyList<int>? ParseRequestedTunnelIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var ids = JsonSerializer.Deserialize<int[]>(json);
            return ids is { Length: > 0 } ? ids : null;
        }
        catch (JsonException)
        {
            return Array.Empty<int>();
        }
    }
```

- [ ] **Step 7: Make the API create the row and enqueue one job**

In `api/Controllers/SyncRunsController.cs`, `TriggerSync`: replace everything from `// Create a pending SyncRun record` down to `return Ok(new { runId = run.Id });` (inclusive) with:

```csharp
        // Create a pending SyncRun record. Phase 2 (§2.7): the row carries everything the worker
        // needs (RunType, IsDryRun, RequestedTunnelIds); the job only says WHICH row to run.
        var run = new AFHSync.Shared.Entities.SyncRun
        {
            RunType = runType,
            Status = SyncStatus.Pending,
            IsDryRun = request.IsDryRun,
            RequestedTunnelIds = request.TunnelIds is { Length: > 0 }
                ? System.Text.Json.JsonSerializer.Serialize(request.TunnelIds)
                : null,
            CreatedAt = DateTime.UtcNow
        };

        db.SyncRuns.Add(run);
        await db.SaveChangesAsync();
        if (tx != null) { await tx.CommitAsync(); await tx.DisposeAsync(); }

        // Exactly ONE Hangfire job per run, addressed by run id (the per-tunnel fan-out is gone —
        // it raced N jobs for one Pending row). The job id is stored so StopSync /
        // StaleRunCleanupService can BackgroundJob.Delete a queued-but-not-started job.
        var runId = run.Id;
        try
        {
            var jobId = jobs.Enqueue<ISyncEngine>(engine =>
                engine.RunAsync(runId, runType, request.IsDryRun, CancellationToken.None));
            run.HangfireJobIds = jobId;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            run.Status = SyncStatus.Failed;
            run.CompletedAt = DateTime.UtcNow;
            run.ErrorSummary = $"Failed to enqueue sync job: {ex.Message}";
            await db.SaveChangesAsync();
            return StatusCode(500, new { message = $"Sync run {runId} could not be queued: {ex.Message}" });
        }

        return Ok(new { runId });
```

- [ ] **Step 8: Verify the untouched callers**

Run: `grep -n "RunAsync(null, RunType.Scheduled" api/Controllers/SettingsController.cs worker/Program.cs`
Expected: one hit in each file — both already pass `null`, which now means "create a new Scheduled row". No edit.

- [ ] **Step 9: Run the tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 225, Skipped: 1` (221 + 4 new).

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 36, Skipped: 1` (or `Passed: 37` with Postgres).

- [ ] **Step 10: Commit**

```bash
git add shared/AFHSync.Shared.csproj shared/Services/ISyncEngine.cs worker/Services/IRunClaimService.cs worker/Services/RunClaimService.cs worker/Services/SyncEngine.cs worker/Program.cs api/Controllers/SyncRunsController.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs
git commit -m "feat(sync): runs are claimed by id — API creates the row with requested tunnel ids and enqueues one job; retries off

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Startup reconcile, Pending cleanup, photo sync through the locked path (§2.7 cont.)

**Files:**
- Create: `worker/Services/RunReconciler.cs`
- Modify: `worker/Services/StaleRunCleanupService.cs`
- Modify: `worker/Services/IPhotoSyncService.cs`
- Modify: `worker/Services/PhotoSyncService.cs` (constructor, `RunAllAsync`)
- Modify: `worker/Program.cs` (DI + startup reconcile)
- Modify: `tests/AFHSync.Tests.Unit/AFHSync.Tests.Unit.csproj` (Hangfire.Core for the job-client fake)
- Create: `tests/AFHSync.Tests.Unit/Sync/RunReconcilerTests.cs`
- Create: `tests/AFHSync.Tests.Unit/Sync/StaleRunCleanupServiceTests.cs`
- Modify: `tests/AFHSync.Tests.Unit/Sync/PhotoSyncServiceTests.cs`
- Modify: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` (`FakePhotoSyncService` signature)

**Interfaces:**
- Produces:
  ```csharp
  // worker/Services/RunReconciler.cs
  public sealed class RunReconciler(IDbContextFactory<AFHSyncDbContext> dbContextFactory, ILogger<RunReconciler> logger)
  {
      public const string InterruptedSummary = "interrupted by worker restart";
      public Task<int> ReconcileAsync(CancellationToken ct);   // returns number of rows failed
  }
  // worker/Services/IPhotoSyncService.cs — skipRunningCheck parameter REMOVED
  [AutomaticRetry(Attempts = 0)]
  Task RunAllAsync(RunType runType, bool isDryRun, CancellationToken ct);
  // worker/Services/StaleRunCleanupService.cs
  public const string PendingNeverClaimedSummary = "Automatically marked as failed — never claimed by the worker within 10 minutes";
  ```
  `PhotoSyncService`'s constructor gains `IRunClaimService runClaimService` after `IRunLogger runLogger`.
- Consumes: `IRunClaimService` / `RunClaimService` / `RunClaimOutcome` (Task 2).

- [ ] **Step 1: Write the failing tests**

Add `Hangfire.Core` to the unit test project so the tests can fake `IBackgroundJobClient` — in `tests/AFHSync.Tests.Unit/AFHSync.Tests.Unit.csproj`, in the `<ItemGroup>` with `PackageReference`s, add:

```xml
    <PackageReference Include="Hangfire.Core" Version="1.8.23" />
```

Create `tests/AFHSync.Tests.Unit/Sync/RunReconcilerTests.cs`:

```csharp
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>Phase 2 (§2.7): worker startup fails rows left Running by a dead process.</summary>
public class RunReconcilerTests
{
    private static AFHSyncDbContext MakeDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AFHSyncDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AFHSyncDbContext(options);
    }

    private sealed class TestDbContextFactory(string dbName) : IDbContextFactory<AFHSyncDbContext>
    {
        public AFHSyncDbContext CreateDbContext() => MakeDbContext(dbName);
    }

    [Fact]
    public async Task ReconcileAsync_FailsRunningRows_ClearsCancelFlag_LeavesOthersAlone()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.SyncRuns.AddRange(
                new SyncRun { Id = 1, RunType = RunType.Manual, Status = SyncStatus.Running, StartedAt = DateTime.UtcNow.AddMinutes(-20), CreatedAt = DateTime.UtcNow.AddMinutes(-21) },
                new SyncRun { Id = 2, RunType = RunType.PhotoSync, Status = SyncStatus.Running, StartedAt = DateTime.UtcNow.AddMinutes(-5), CreatedAt = DateTime.UtcNow.AddMinutes(-5) },
                new SyncRun { Id = 3, RunType = RunType.Manual, Status = SyncStatus.Pending, CreatedAt = DateTime.UtcNow },
                new SyncRun { Id = 4, RunType = RunType.Manual, Status = SyncStatus.Success, CompletedAt = DateTime.UtcNow.AddHours(-1), CreatedAt = DateTime.UtcNow.AddHours(-1) });
            seedCtx.AppSettings.Add(new AppSetting { Id = 1, Key = "cancel_sync", Value = "true", UpdatedAt = DateTime.UtcNow });
            await seedCtx.SaveChangesAsync();
        }

        var reconciler = new RunReconciler(new TestDbContextFactory(dbName), NullLogger<RunReconciler>.Instance);

        var count = await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, count);
        using var verifyCtx = MakeDbContext(dbName);
        var runs = await verifyCtx.SyncRuns.OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(SyncStatus.Failed, runs[0].Status);
        Assert.Equal(RunReconciler.InterruptedSummary, runs[0].ErrorSummary);
        Assert.NotNull(runs[0].CompletedAt);
        Assert.NotNull(runs[0].DurationMs);
        Assert.Equal(SyncStatus.Failed, runs[1].Status);
        Assert.Equal(SyncStatus.Pending, runs[2].Status);      // a queued job may still claim it
        Assert.Equal(SyncStatus.Success, runs[3].Status);
        var flag = await verifyCtx.AppSettings.SingleAsync(s => s.Key == "cancel_sync");
        Assert.Equal("false", flag.Value);
    }

    [Fact]
    public async Task ReconcileAsync_NothingRunning_ReturnsZero()
    {
        var dbName = Guid.NewGuid().ToString();
        var reconciler = new RunReconciler(new TestDbContextFactory(dbName), NullLogger<RunReconciler>.Instance);

        var count = await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, count);
    }
}
```

Create `tests/AFHSync.Tests.Unit/Sync/StaleRunCleanupServiceTests.cs`:

```csharp
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>Phase 2 (§2.7): Pending rows nobody claimed within 10 minutes are failed.</summary>
public class StaleRunCleanupServiceTests
{
    private static AFHSyncDbContext MakeDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AFHSyncDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AFHSyncDbContext(options);
    }

    private sealed class TestDbContextFactory(string dbName) : IDbContextFactory<AFHSyncDbContext>
    {
        public AFHSyncDbContext CreateDbContext() => MakeDbContext(dbName);
    }

    /// <summary>Records job ids passed to Delete (ChangeState with DeletedState).</summary>
    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<string> DeletedJobIds { get; } = [];
        public string Create(Job job, IState state) => Guid.NewGuid().ToString("N");
        public bool ChangeState(string jobId, IState state, string? expectedState)
        {
            if (state is DeletedState) DeletedJobIds.Add(jobId);
            return true;
        }
    }

    [Fact]
    public async Task CleanupAsync_FailsPendingRowsOlderThan10Minutes_LeavesYoungerOnes()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.SyncRuns.AddRange(
                new SyncRun { Id = 1, RunType = RunType.Manual, Status = SyncStatus.Pending, CreatedAt = DateTime.UtcNow.AddMinutes(-11), HangfireJobIds = "job-old" },
                new SyncRun { Id = 2, RunType = RunType.Manual, Status = SyncStatus.Pending, CreatedAt = DateTime.UtcNow.AddMinutes(-2), HangfireJobIds = "job-young" });
            await seedCtx.SaveChangesAsync();
        }
        var jobs = new RecordingJobClient();
        var service = new StaleRunCleanupService(new TestDbContextFactory(dbName), jobs, NullLogger<StaleRunCleanupService>.Instance);

        await service.CleanupAsync();

        using var verifyCtx = MakeDbContext(dbName);
        var old = await verifyCtx.SyncRuns.SingleAsync(r => r.Id == 1);
        Assert.Equal(SyncStatus.Failed, old.Status);
        Assert.Equal(StaleRunCleanupService.PendingNeverClaimedSummary, old.ErrorSummary);
        Assert.NotNull(old.CompletedAt);
        var young = await verifyCtx.SyncRuns.SingleAsync(r => r.Id == 2);
        Assert.Equal(SyncStatus.Pending, young.Status);
        Assert.Equal(new[] { "job-old" }, jobs.DeletedJobIds);
        // A never-started job needs no cancel flag.
        Assert.False(await verifyCtx.AppSettings.AnyAsync(s => s.Key == "cancel_sync" && s.Value == "true"));
    }

    [Fact]
    public async Task CleanupAsync_StillFailsLongRunningRows_AndRaisesCancelFlag()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.SyncRuns.Add(new SyncRun { Id = 1, RunType = RunType.Manual, Status = SyncStatus.Running, StartedAt = DateTime.UtcNow.AddHours(-3), CreatedAt = DateTime.UtcNow.AddHours(-3), HangfireJobIds = "job-stuck" });
            await seedCtx.SaveChangesAsync();
        }
        var jobs = new RecordingJobClient();
        var service = new StaleRunCleanupService(new TestDbContextFactory(dbName), jobs, NullLogger<StaleRunCleanupService>.Instance);

        await service.CleanupAsync();

        using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(SyncStatus.Failed, (await verifyCtx.SyncRuns.SingleAsync()).Status);
        Assert.Equal("true", (await verifyCtx.AppSettings.SingleAsync(s => s.Key == "cancel_sync")).Value);
        Assert.Equal(new[] { "job-stuck" }, jobs.DeletedJobIds);
    }
}
```

In `tests/AFHSync.Tests.Unit/Sync/PhotoSyncServiceTests.cs`:

(a) In `TestablePhotoSyncService`'s constructor change the base call to:

```csharp
            : base(dbContextFactory, null!, contactFolderManager, runLogger,
                   new RunClaimService(dbContextFactory, NullLogger<RunClaimService>.Instance),
                   throttleCounter, logger)
```

(b) In `RunAllAsync_LoadsActiveTunnelsAndProcessesEach`, replace

```csharp
        // Should have created a run
        Assert.True(testable.RunLogger.WasCreated);
```

with

```csharp
        // Should have claimed/created a run through RunClaimService (not IRunLogger.CreateRunAsync)
        Assert.False(testable.RunLogger.WasCreated);
        using var verifyCtx = MakeDbContext(dbName);
        var run = await verifyCtx.SyncRuns.SingleAsync();
        Assert.Equal(RunType.Scheduled, run.RunType);
        Assert.NotNull(run.StartedAt);
```

(c) Add this test after `RunAllAsync_LoadsActiveTunnelsAndProcessesEach`:

```csharp
    [Fact]
    public async Task RunAllAsync_SkipsWhenAnotherRunIsRunning()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedCtx = MakeDbContext(dbName);
        SeedDefaultFieldProfile(seedCtx);
        seedCtx.AppSettings.Add(new AppSetting { Id = 100, Key = "photo_sync_mode", Value = "separate_pass", Description = "Test", UpdatedAt = DateTime.UtcNow });
        seedCtx.SyncRuns.Add(new SyncRun { Id = 50, RunType = RunType.Manual, Status = SyncStatus.Running, StartedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
        await seedCtx.SaveChangesAsync();

        var testable = CreateTestableService(dbName);

        await testable.Service.RunAllAsync(RunType.Scheduled, isDryRun: false, CancellationToken.None);

        Assert.False(testable.RunLogger.WasFinalized);
        using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(1, await verifyCtx.SyncRuns.CountAsync());   // no photo run row was created
    }
```

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`, in `FakePhotoSyncService`, change the `RunAllAsync` signature to `public Task RunAllAsync(RunType runType, bool isDryRun, CancellationToken ct)`.

- [ ] **Step 2: Run to verify the new tests fail to compile**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | grep -E "error CS" | head -3`
Expected: `error CS0246: The type or namespace name 'RunReconciler' could not be found` (and the `PendingNeverClaimedSummary` / constructor errors).

- [ ] **Step 3: Create `RunReconciler` and wire it into worker startup**

Create `worker/Services/RunReconciler.cs`:

```csharp
using AFHSync.Shared.Data;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AFHSync.Worker.Services;

/// <summary>
/// Phase 2 (§2.7): runs once at worker startup, BEFORE the Hangfire server starts. Any row still
/// Running belonged to a process that died (crash, OOM, ungraceful stop) — mark it Failed and
/// clear the cancel_sync flag it may have left behind. Nothing is auto-restarted.
/// </summary>
public sealed class RunReconciler(
    IDbContextFactory<AFHSyncDbContext> dbContextFactory,
    ILogger<RunReconciler> logger)
{
    public const string InterruptedSummary = "interrupted by worker restart";

    /// <returns>The number of Running rows marked Failed.</returns>
    public async Task<int> ReconcileAsync(CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;

        var running = await db.SyncRuns
            .Where(r => r.Status == SyncStatus.Running)
            .ToListAsync(ct);

        foreach (var run in running)
        {
            run.Status = SyncStatus.Failed;
            run.CompletedAt = now;
            run.DurationMs = run.StartedAt.HasValue ? (int)(now - run.StartedAt.Value).TotalMilliseconds : null;
            run.ErrorSummary = InterruptedSummary;
            logger.LogWarning("Startup reconcile: RunId={RunId} ({RunType}, started {StartedAt}) was left Running — marked Failed",
                run.Id, run.RunType, run.StartedAt);
        }

        var cancelFlag = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "cancel_sync", ct);
        if (cancelFlag is not null && cancelFlag.Value != "false")
        {
            cancelFlag.Value = "false";
            cancelFlag.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return running.Count;
    }
}
```

In `worker/Program.cs`:

(a) After `services.AddScoped<IRunClaimService, RunClaimService>();` add:

```csharp
    services.AddScoped<RunReconciler>();
```

(b) Inside the startup block `using (var scope = app.Services.CreateScope())`, directly after `await using var db = await dbFactory.CreateDbContextAsync();` and before the `cronSetting` lookup, add:

```csharp
        // Phase 2 (§2.7): reconcile rows left Running by a dead worker BEFORE the Hangfire
        // server starts processing jobs (the server is a hosted service started by app.RunAsync).
        var reconciler = scope.ServiceProvider.GetRequiredService<RunReconciler>();
        var interrupted = await reconciler.ReconcileAsync(CancellationToken.None);
        if (interrupted > 0)
            Log.Warning("Startup reconcile: marked {Count} interrupted run(s) as Failed", interrupted);
```

- [ ] **Step 4: Fail unclaimed Pending rows in `StaleRunCleanupService`**

In `worker/Services/StaleRunCleanupService.cs`:

(a) After `private static readonly TimeSpan PhotoRunStaleAfter = TimeSpan.FromHours(6);` add:

```csharp
    private static readonly TimeSpan PendingStaleAfter = TimeSpan.FromMinutes(10);
    public const string PendingNeverClaimedSummary = "Automatically marked as failed — never claimed by the worker within 10 minutes";
```

(b) Replace the block from `var staleRuns = await db.SyncRuns` through `if (staleRuns.Count == 0)\n            return;` with:

```csharp
        var staleRuns = await db.SyncRuns
            .Where(r => r.Status == SyncStatus.Running
                && ((r.RunType == RunType.PhotoSync && r.StartedAt < photoCutoff)
                    || (r.RunType != RunType.PhotoSync && r.StartedAt < contactCutoff)))
            .ToListAsync();

        // Phase 2 (§2.7): a Pending row whose job never ran (worker down, enqueue lost).
        var pendingCutoff = now - PendingStaleAfter;
        var stalePending = await db.SyncRuns
            .Where(r => r.Status == SyncStatus.Pending && r.CreatedAt < pendingCutoff)
            .ToListAsync();

        foreach (var run in stalePending)
        {
            run.Status = SyncStatus.Failed;
            run.CompletedAt = now;
            run.ErrorSummary = PendingNeverClaimedSummary;
            logger.LogWarning("Stale run cleanup: marked Pending RunId={RunId} (created {CreatedAt}) as Failed — never claimed",
                run.Id, run.CreatedAt);
        }

        if (staleRuns.Count == 0)
        {
            if (stalePending.Count > 0)
            {
                await db.SaveChangesAsync();
                DeleteTrackedJobs(stalePending);
            }
            return;
        }
```

(c) Replace the trailing `foreach (var run in staleRuns) { if (string.IsNullOrWhiteSpace(run.HangfireJobIds)) continue; … }` loop at the end of `CleanupAsync` (the loop only — keep the method's closing brace) with the single statement:

```csharp
        DeleteTrackedJobs(staleRuns.Concat(stalePending));
```

Then add this helper method directly after `CleanupAsync` (before the class's closing brace):

```csharp
    /// <summary>
    /// Belt-and-suspenders: actively delete tracked Hangfire jobs so a worker blocked in a
    /// Graph call is cancelled via Hangfire's job-cancellation token, and a never-started
    /// job is removed from the queue. Delete is idempotent.
    /// </summary>
    private void DeleteTrackedJobs(IEnumerable<SyncRun> runs)
    {
        foreach (var run in runs)
        {
            if (string.IsNullOrWhiteSpace(run.HangfireJobIds)) continue;
            foreach (var id in run.HangfireJobIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try { backgroundJobs.Delete(id); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete Hangfire job {JobId} for stale run {RunId}", id, run.Id);
                }
            }
        }
    }
```

(The existing `cancelFlag` write and `await db.SaveChangesAsync();` between (b) and (c) stay as they are — they run only when `staleRuns.Count > 0`, which is what the second test asserts.)

- [ ] **Step 5: Route photo sync through the claim service**

Replace the whole of `worker/Services/IPhotoSyncService.cs` with:

```csharp
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Hangfire;

namespace AFHSync.Worker.Services;

/// <summary>
/// Fetches source user photos from Microsoft Graph, computes SHA-256 hashes for delta
/// comparison, and writes changed photos to target contact records. Supports three modes:
/// included (trailing pass within SyncEngine), separate_pass (own Hangfire job), disabled.
/// </summary>
public interface IPhotoSyncService
{
    /// <summary>
    /// Runs photo sync for a single tunnel. Called by SyncEngine (included mode) or RunAllAsync (separate_pass).
    /// Returns (photosUpdated, photosFailed).
    /// The <c>prior*</c> parameters let the caller thread cumulative cross-tunnel counts so
    /// mid-tunnel progress writes reflect the correct running totals on the dashboard.
    /// </summary>
    Task<(int updated, int failed)> SyncPhotosForTunnelAsync(
        Tunnel tunnel,
        SyncRun run,
        List<SourceUser> sourceUsers,
        bool isDryRun,
        CancellationToken ct,
        int priorPhotosUpdated = 0,
        int priorPhotosFailed = 0,
        int priorTunnelsProcessed = 0);

    /// <summary>
    /// Entry point for the separate_pass Hangfire job and the post-finalize auto-trigger.
    /// Phase 2 (§2.7): creates and claims its own SyncRun through IRunClaimService (one lane
    /// across run types), so it is a no-op while any run is Running. Never retried by Hangfire.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    Task RunAllAsync(RunType runType, bool isDryRun, CancellationToken ct);
}
```

In `worker/Services/PhotoSyncService.cs`:

(a) Add `using Hangfire;` to the usings.

(b) Add the field `private readonly IRunClaimService _runClaimService;` after `private readonly IRunLogger _runLogger;`, add the constructor parameter `IRunClaimService runClaimService,` after `IRunLogger runLogger,`, and the assignment `_runClaimService = runClaimService;` after `_runLogger = runLogger;`.

(c) In `RunAllAsync`, replace the method header and everything from `// Check for running sync to avoid overlap` down to and including `_logger.LogInformation("Photo sync RunAllAsync starting RunId={RunId}", run.Id);` with:

```csharp
    /// <inheritdoc />
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAllAsync(RunType runType, bool isDryRun, CancellationToken ct)
    {
        // Read photo_sync_mode once at start (T-06-04: prevents mid-run mode switch)
        await using var settingsDb = await _dbContextFactory.CreateDbContextAsync(ct);
        var modeSetting = await settingsDb.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "photo_sync_mode", ct);
        var photoSyncMode = modeSetting?.Value ?? "included";

        if (photoSyncMode != "separate_pass")
        {
            _logger.LogInformation(
                "Photo sync mode is '{Mode}', not 'separate_pass' -- RunAllAsync is a no-op",
                photoSyncMode);
            return;
        }

        // Phase 2 (§2.7): claim a row through the same locked path as contact runs. One lane
        // across run types — a Running contact run blocks photo sync and vice versa, because
        // both write the same contacts. The post-finalize auto-trigger runs after the contact
        // run is already Success/Warning, so it passes this guard.
        var claim = await _runClaimService.ClaimAsync(null, runType, isDryRun, CancellationToken.None);
        if (claim.Outcome != RunClaimOutcome.Claimed || claim.Run is null)
        {
            _logger.LogWarning("A sync run is already in progress, skipping photo sync");
            return;
        }
        var run = claim.Run;
        isDryRun = run.IsDryRun;

        // Clear any stale cancel_sync flag so this run doesn't self-cancel on the first
        // between-tunnel check. Prior killed runs may leave the flag set to true.
        var cancelClear = await settingsDb.AppSettings.FirstOrDefaultAsync(s => s.Key == "cancel_sync", ct);
        if (cancelClear != null && cancelClear.Value != "false")
        {
            cancelClear.Value = "false";
            await settingsDb.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Photo sync RunAllAsync starting RunId={RunId}", run.Id);
```

(The old `var run = await _runLogger.CreateRunAsync(runType, isDryRun, ct);` line is gone — make sure it was inside the replaced range. `IRunLogger.CreateRunAsync` stays on the interface; nothing in production calls it any more.)

- [ ] **Step 6: Run the unit tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 230, Skipped: 1` (225 + 2 reconciler + 2 cleanup + 1 photo).

- [ ] **Step 7: Build the worker and confirm the startup order**

Run: `dotnet build worker --nologo -v quiet 2>&1 | grep -E "error|Warn|Build succeeded" | head -5`
Expected: `Build succeeded.` (the `NU1903` warning is pre-existing).

Run: `grep -n "ReconcileAsync\|AddOrUpdate<ISyncEngine>\|await app.RunAsync" worker/Program.cs`
Expected: the `ReconcileAsync` line appears before the `AddOrUpdate<ISyncEngine>` line, which appears before `await app.RunAsync();`.

- [ ] **Step 8: Commit**

```bash
git add worker/Services/RunReconciler.cs worker/Services/StaleRunCleanupService.cs worker/Services/IPhotoSyncService.cs worker/Services/PhotoSyncService.cs worker/Program.cs tests/AFHSync.Tests.Unit/AFHSync.Tests.Unit.csproj tests/AFHSync.Tests.Unit/Sync/RunReconcilerTests.cs tests/AFHSync.Tests.Unit/Sync/StaleRunCleanupServiceTests.cs tests/AFHSync.Tests.Unit/Sync/PhotoSyncServiceTests.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -m "feat(worker): startup reconcile of interrupted runs, 10-minute Pending cleanup, photo sync claims through the locked path

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Cancellation on worker shutdown (§2.6b)

**Files:**
- Modify: `worker/Services/SyncEngine.cs` (`RunAsync` body, mailbox lambda in `ProcessTunnelAsync`)
- Modify: `worker/Program.cs` (Hangfire + host shutdown timeouts)
- Modify: `compose.yaml` (worker `stop_grace_period`)
- Test: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`

**Interfaces:**
- Produces: `SyncEngine.WorkerShutdownReason` (`internal const string` = `"worker shutting down"`). Behaviour: when the `ct` passed to `RunAsync` is cancelled, the run is finalized `Cancelled` with that `errorSummary`, at the next tunnel boundary or mailbox boundary, using `CancellationToken.None` for the finalize and item flush.
- Consumes: `FakeRunLogger.FinalizedStatus` (Task 2). `CreateEngine`'s `sourceResolver` parameter is widened to `ISourceResolver?` here.
- How the token reaches `RunAsync` (verified against the Hangfire docs, *Using cancellation tokens*): a `CancellationToken` parameter on a job method is a special parameter — callers pass `CancellationToken.None` at enqueue time and Hangfire **replaces it at execution time** with a token that is signalled on server shutdown and when the job is deleted (`BackgroundJob.Delete`, which `StopSync` and `StaleRunCleanupService` already call). Hangfire polls job state every `CancellationCheckInterval` (default 5 s) for the delete case; the shutdown case is immediate. No enqueue-site change is needed.

- [ ] **Step 1: Write the failing tests**

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`:

(a) Change the `CreateEngine` parameter `FakeSourceResolver? sourceResolver = null` to `ISourceResolver? sourceResolver = null` (the body already coalesces with `?? new FakeSourceResolver([])`).

(b) Add this fake next to the other private fakes:

```csharp
    /// <summary>Cancels the given source on its first call — simulates a shutdown arriving mid-run.</summary>
    private sealed class CancellingSourceResolver(CancellationTokenSource cts) : ISourceResolver
    {
        public int ResolveCallCount { get; private set; }

        public Task<List<SourceUser>> ResolveAsync(Tunnel tunnel, CancellationToken ct)
        {
            ResolveCallCount++;
            cts.Cancel();
            return Task.FromResult(new List<SourceUser>());
        }
    }
```

(c) Add the tests after `ParseRequestedTunnelIds_HandlesNullJsonAndGarbage`:

```csharp
    // ==============================
    // Phase 2 (2.6b): Hangfire's shutdown token ⇒ Cancelled "worker shutting down"
    // ==============================

    [Fact]
    public async Task RunAsync_PreCancelledToken_FinalizesCancelledWithoutProcessingAnyTunnel()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.Tunnels.Add(new Tunnel { Id = 1, Name = "T1", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove });
            await seedCtx.SaveChangesAsync();
        }
        var sourceResolver = new FakeSourceResolver([]);
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, runLogger: runLogger);

        var run = await engine.RunAsync(null, RunType.Scheduled, isDryRun: false, new CancellationToken(canceled: true));

        Assert.Equal(SyncStatus.Cancelled, run.Status);
        Assert.True(runLogger.WasFinalized);
        Assert.Equal(SyncStatus.Cancelled, runLogger.FinalizedStatus);
        Assert.Equal("worker shutting down", runLogger.FinalizedErrorSummary);
        Assert.Equal(0, sourceResolver.ResolveCallCount);
        // The row was still claimed (bookkeeping ignores the shutdown token) so it can be finalized.
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(1, await verifyCtx.SyncRuns.CountAsync());
    }

    [Fact]
    public async Task RunAsync_TokenCancelledMidRun_StopsAtNextTunnelBoundary()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.Tunnels.AddRange(
                new Tunnel { Id = 1, Name = "T1", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove },
                new Tunnel { Id = 2, Name = "T2", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove });
            await seedCtx.SaveChangesAsync();
        }
        using var cts = new CancellationTokenSource();
        var sourceResolver = new CancellingSourceResolver(cts);
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, runLogger: runLogger);

        var run = await engine.RunAsync(null, RunType.Scheduled, isDryRun: false, cts.Token);

        Assert.Equal(1, sourceResolver.ResolveCallCount);              // second tunnel never started
        Assert.Equal(SyncStatus.Cancelled, run.Status);
        Assert.Equal(SyncStatus.Cancelled, runLogger.FinalizedStatus);
        Assert.Equal("worker shutting down", runLogger.FinalizedErrorSummary);
    }
```

- [ ] **Step 2: Run the two tests to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~RunAsync_PreCancelledToken|FullyQualifiedName~RunAsync_TokenCancelledMidRun" 2>&1 | tail -6`
Expected: both FAIL — today a pre-cancelled token surfaces as `Failed` ("Sync run failed with unhandled exception: The operation was canceled") and a mid-run cancel processes both tunnels.

- [ ] **Step 3: Check the token at every boundary in `SyncEngine`**

In `worker/Services/SyncEngine.cs`:

(a) After `private const int DefaultParallelism = 4;` add:

```csharp

    /// <summary>Phase 2 (§2.6b): errorSummary when Hangfire's shutdown/delete token stops a run.</summary>
    internal const string WorkerShutdownReason = "worker shutting down";
```

(b) After `var tunnelErrors = new List<string>();` (just before `try`) add:

```csharp
        string? cancelReason = null;
```

(c) Inside the `try`, immediately before `// Step 3: Load tunnels.`, add:

```csharp
            // Phase 2 (§2.6b): a run claimed during shutdown does no work — finalize it Cancelled.
            ct.ThrowIfCancellationRequested();

```

(d) At the top of the tunnel loop body, before `// Check for cancellation request (stop sync button)`, add:

```csharp
                // Phase 2 (§2.6b): Hangfire signals this token on worker shutdown and job deletion.
                if (ct.IsCancellationRequested)
                {
                    logger.LogInformation("Shutdown requested — stopping after {Processed} tunnel(s)", tunnelsProcessed + tunnelsWarned);
                    wasCancelled = true;
                    cancelReason = WorkerShutdownReason;
                    break;
                }

```

(e) Inside the loop, the per-tunnel `try { … ProcessTunnelAsync … }` is followed by `catch (Exception ex) { logger.LogError(ex, "Tunnel {TunnelId} ({TunnelName}) failed with unhandled exception", …`. Insert this clause **before** that `catch (Exception ex)`:

```csharp
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    logger.LogInformation("Shutdown requested during tunnel {TunnelId} ({TunnelName}) — stopping", tunnel.Id, tunnel.Name);
                    wasCancelled = true;
                    cancelReason = WorkerShutdownReason;
                    break;
                }
```

(f) Replace the end of the run-level `try` — from `// Step 6: Flush all buffered SyncRunItems.` through the closing brace of `catch (Exception ex) { fatalError = …; logger.LogError(ex, "SyncRun {RunId} failed with unhandled exception", run.Id); }` — with:

```csharp
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Hangfire signalled shutdown (or deleted the job) while a tunnel/mailbox was in flight.
            logger.LogInformation("SyncRun {RunId} cancelled by worker shutdown", run.Id);
            wasCancelled = true;
            cancelReason = WorkerShutdownReason;
        }
        catch (Exception ex)
        {
            fatalError = $"Sync run failed with unhandled exception: {ex.Message}";
            logger.LogError(ex, "SyncRun {RunId} failed with unhandled exception", run.Id);
        }

        // Step 6: Flush all buffered SyncRunItems — always, including after cancellation or a
        // fatal error. CancellationToken.None so shutdown doesn't discard buffered items.
        try
        {
            await runLogger.FlushItemsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to flush run items for SyncRun {RunId}", run.Id);
        }
```

(g) In `// Step 8: Finalize the run`, change the `errorSummary:` argument to:

```csharp
                errorSummary: fatalError ?? cancelReason ?? (tunnelErrors.Count > 0
                    ? (tunnelsFailed > 0 ? $"{tunnelsFailed} tunnel(s) failed: " : "") + string.Join("; ", tunnelErrors)
                    : null),
```

(h) In `ProcessTunnelAsync`, in the mailbox lambda `var mailboxTasks = targetMailboxes.Select(async mailbox => {`, insert as the **first** statement (before `await semaphore.WaitAsync(ct);`):

```csharp
            // Phase 2 (§2.6b): mailbox boundary — don't start new mailboxes once shutdown is signalled.
            if (ct.IsCancellationRequested)
                return;
```

(A mailbox already waiting on the semaphore gets an `OperationCanceledException` from `WaitAsync(ct)`; the existing `catch (Exception ex) when (ex is not OperationCanceledException)` lets it propagate to (e).)

- [ ] **Step 4: Give the worker time to finalize**

In `worker/Program.cs`, replace the `services.AddHangfireServer(options => { … });` call with:

```csharp
    services.AddHangfireServer(options =>
    {
        options.WorkerCount = 2; // Low count: sync runs are heavy, bounded by semaphore
        options.Queues = new[] { "sync", "default" };
        // Phase 2 (§2.6b): on SIGTERM Hangfire cancels the job token and waits this long for
        // RunAsync to finalize the run as Cancelled. Must be < HostOptions.ShutdownTimeout (50s)
        // < compose stop_grace_period (60s), otherwise Docker SIGKILLs mid-finalize.
        options.ShutdownTimeout = TimeSpan.FromSeconds(45);
    });
    services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(50));
```

In `compose.yaml`, under the `worker:` service, directly after `restart: unless-stopped`, add:

```yaml
    stop_grace_period: 60s
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 232, Skipped: 1`.

Run: `docker compose config --quiet 2>/dev/null && echo "compose ok" || echo "compose config skipped (docker not running)"`
Expected: `compose ok` when Docker is up; otherwise the skip message (the YAML is a one-line scalar addition — re-check indentation by eye: `stop_grace_period` aligns with `restart`).

- [ ] **Step 6: Commit**

```bash
git add worker/Services/SyncEngine.cs worker/Program.cs compose.yaml tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -m "feat(worker): finalize runs as Cancelled on shutdown — token checked at tunnel and mailbox boundaries, 60s stop grace

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: Unavailable mailboxes (§2.1 — worker side)

**Files:**
- Create: `worker/Services/MailboxAvailability.cs`
- Modify: `worker/Services/SyncEngine.cs` (`ProcessMailboxAsync` folder catch, `LoadTargetMailboxesAsync`, two new helpers)
- Create: `tests/AFHSync.Tests.Unit/Sync/MailboxAvailabilityTests.cs`
- Test: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // worker/Services/MailboxAvailability.cs
  public static class MailboxAvailability
  {
      public const string UnavailableErrorCode = "MailboxNotEnabledForRESTAPI";
      public const string UnavailableMessageFragment = "inactive, soft-deleted, or is hosted on-premise";
      public static readonly TimeSpan ReprobeInterval = TimeSpan.FromDays(7);
      public static bool IsUnavailable(Exception ex);
  }
  ```
  Behaviour in `SyncEngine`: an unavailable folder-lookup failure stamps `MailboxUnavailableAt` (if null), `MailboxLastProbedAt = now`, `MailboxUnavailableReason = message` (`CancellationToken.None`), logs Information, writes **no** run item and counts **no** failure; `LoadTargetMailboxesAsync` excludes `IsActive` rows with `MailboxUnavailableAt != null && MailboxLastProbedAt > now − 7d` and logs `"{N} target mailbox(es) excluded (unavailable)"`; the first successful lookup clears all three columns. The `IsActive=false` self-heal is deleted.
  `FakeContactFolderManager` (SyncEngineTests) gains `Dictionary<string, Exception> Failures` and `List<string> Requested`.
- Consumes: the three `TargetMailbox` columns (Task 1).

- [ ] **Step 1: Write the failing tests**

Create `tests/AFHSync.Tests.Unit/Sync/MailboxAvailabilityTests.cs`:

```csharp
using AFHSync.Worker.Services;
using Microsoft.Graph.Models.ODataErrors;

namespace AFHSync.Tests.Unit.Sync;

public class MailboxAvailabilityTests
{
    [Fact]
    public void IsUnavailable_TrueForODataErrorCode()
    {
        var ex = new ODataError { Error = new MainError { Code = "MailboxNotEnabledForRESTAPI", Message = "REST API is not yet supported for this mailbox." } };
        Assert.True(MailboxAvailability.IsUnavailable(ex));
    }

    [Fact]
    public void IsUnavailable_TrueForODataErrorMessageFragment()
    {
        var ex = new ODataError { Error = new MainError { Code = "ErrorItemNotFound", Message = "The mailbox is either inactive, soft-deleted, or is hosted on-premise." } };
        Assert.True(MailboxAvailability.IsUnavailable(ex));
    }

    [Fact]
    public void IsUnavailable_TrueForPlainExceptionWithFragment()
    {
        var ex = new InvalidOperationException("Graph said: the mailbox is either INACTIVE, SOFT-DELETED, OR IS HOSTED ON-PREMISE.");
        Assert.True(MailboxAvailability.IsUnavailable(ex));
    }

    [Fact]
    public void IsUnavailable_FalseForOtherErrors()
    {
        Assert.False(MailboxAvailability.IsUnavailable(new ODataError { Error = new MainError { Code = "ErrorAccessDenied", Message = "Access is denied." } }));
        Assert.False(MailboxAvailability.IsUnavailable(new InvalidOperationException("boom")));
    }
}
```

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`:

(a) Add `using Microsoft.Graph.Models.ODataErrors;` to the usings.

(b) Replace the `FakeContactFolderManager` class with:

```csharp
    private sealed class FakeContactFolderManager : IContactFolderManager
    {
        /// <summary>Mailboxes (by EntraId) whose folder lookup throws the given exception.</summary>
        public Dictionary<string, Exception> Failures { get; } = new();

        /// <summary>Every mailbox EntraId the engine asked a folder for, in call order.</summary>
        public List<string> Requested { get; } = [];

        public Task<(string folderId, bool wasCreated)> GetOrCreateFolderAsync(string mailboxEntraId, string folderName, CancellationToken ct)
        {
            Requested.Add(mailboxEntraId);
            if (Failures.TryGetValue(mailboxEntraId, out var ex))
                throw ex;
            return Task.FromResult(("fake-folder-id", false));
        }

        public void ResetCache() { }
    }
```

(c) Add these helpers next to `CreateEmptyConfig`:

```csharp
    /// <summary>Seeds one active tunnel (Id 1, name "Avail Tunnel") with phone list 1 and the given mailboxes.</summary>
    private static async Task SeedTunnelWithMailboxesAsync(string dbName, params TargetMailbox[] mailboxes)
    {
        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel { Id = 1, Name = "Avail Tunnel", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
        var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
        var tpl = new TunnelPhoneList { TunnelId = 1, PhoneListId = 1, Tunnel = tunnel, PhoneList = phoneList };
        tunnel.TunnelPhoneLists.Add(tpl);
        seedCtx.Tunnels.Add(tunnel);
        seedCtx.PhoneLists.Add(phoneList);
        seedCtx.TunnelPhoneLists.Add(tpl);
        seedCtx.TargetMailboxes.AddRange(mailboxes);
        await seedCtx.SaveChangesAsync();
    }

    private static ODataError UnavailableMailboxError() => new()
    {
        Error = new MainError
        {
            Code = MailboxAvailability.UnavailableErrorCode,
            Message = "The mailbox is either inactive, soft-deleted, or is hosted on-premise."
        }
    };
```

(d) Add the tests after `RunAsync_TokenCancelledMidRun_StopsAtNextTunnelBoundary`:

```csharp
    // ==============================
    // Phase 2 (2.1): unavailable mailboxes are stamped and skipped, not failed
    // ==============================

    [Fact]
    public async Task RunAsync_UnavailableMailbox_IsStampedNotFailed_NoRunItem()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-dead", Email = "dead@contoso.com", IsActive = true });
        var folderManager = new FakeContactFolderManager();
        folderManager.Failures["mb-dead"] = UnavailableMailboxError();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderManager: folderManager,
            runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.DoesNotContain(runLogger.AddedItems, i => i.Action == "failed");
        Assert.Equal(0, runLogger.FinalizedFailed);
        Assert.Equal(SyncStatus.Success, runLogger.FinalizedStatus);
        await using var verifyCtx = MakeDbContext(dbName);
        var mb = await verifyCtx.TargetMailboxes.SingleAsync();
        Assert.True(mb.IsActive);                                  // IsActive keeps its Entra meaning
        Assert.NotNull(mb.MailboxUnavailableAt);
        Assert.NotNull(mb.MailboxLastProbedAt);
        Assert.Contains("soft-deleted", mb.MailboxUnavailableReason);
    }

    [Fact]
    public async Task RunAsync_UnavailableMailboxProbedWithin7Days_IsExcluded()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-recent", Email = "recent@contoso.com", IsActive = true,
                MailboxUnavailableAt = DateTime.UtcNow.AddDays(-3), MailboxLastProbedAt = DateTime.UtcNow.AddDays(-3), MailboxUnavailableReason = "x" },
            new TargetMailbox { Id = 2, EntraId = "mb-ok", Email = "ok@contoso.com", IsActive = true });
        var folderManager = new FakeContactFolderManager();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderManager: folderManager);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(new[] { "mb-ok" }, folderManager.Requested);
    }

    [Fact]
    public async Task RunAsync_UnavailableMailboxProbedOver7DaysAgo_IsReprobed_AndRestamped()
    {
        var dbName = Guid.NewGuid().ToString();
        var firstSeen = DateTime.UtcNow.AddDays(-30);
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-stale", Email = "stale@contoso.com", IsActive = true,
                MailboxUnavailableAt = firstSeen, MailboxLastProbedAt = DateTime.UtcNow.AddDays(-8), MailboxUnavailableReason = "old reason" });
        var folderManager = new FakeContactFolderManager();
        folderManager.Failures["mb-stale"] = UnavailableMailboxError();   // still dead
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderManager: folderManager);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(new[] { "mb-stale" }, folderManager.Requested);
        await using var verifyCtx = MakeDbContext(dbName);
        var mb = await verifyCtx.TargetMailboxes.SingleAsync();
        Assert.Equal(firstSeen, mb.MailboxUnavailableAt);                            // first-seen is preserved
        Assert.True(mb.MailboxLastProbedAt > DateTime.UtcNow.AddMinutes(-1));        // probe time refreshed
        Assert.Contains("soft-deleted", mb.MailboxUnavailableReason);
    }

    [Fact]
    public async Task RunAsync_ReprobeSucceeds_ClearsUnavailableStamp()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-back", Email = "back@contoso.com", IsActive = true,
                MailboxUnavailableAt = DateTime.UtcNow.AddDays(-30), MailboxLastProbedAt = DateTime.UtcNow.AddDays(-8), MailboxUnavailableReason = "was dead" });
        var folderManager = new FakeContactFolderManager();   // no failure ⇒ lookup succeeds
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderManager: folderManager);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        await using var verifyCtx = MakeDbContext(dbName);
        var mb = await verifyCtx.TargetMailboxes.SingleAsync();
        Assert.Null(mb.MailboxUnavailableAt);
        Assert.Null(mb.MailboxLastProbedAt);
        Assert.Null(mb.MailboxUnavailableReason);
    }

    [Fact]
    public async Task RunAsync_OtherFolderError_StillFailsAndDoesNotStamp()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-err", Email = "err@contoso.com", IsActive = true });
        var folderManager = new FakeContactFolderManager();
        folderManager.Failures["mb-err"] = new InvalidOperationException("boom");
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderManager: folderManager,
            runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        var failedItem = Assert.Single(runLogger.AddedItems, i => i.Action == "failed");
        Assert.Equal("Folder 'Avail Tunnel': boom", failedItem.ErrorMessage);
        Assert.Equal(1, failedItem.TargetMailboxId);
        Assert.Equal(1, runLogger.FinalizedFailed);
        await using var verifyCtx = MakeDbContext(dbName);
        var mb = await verifyCtx.TargetMailboxes.SingleAsync();
        Assert.Null(mb.MailboxUnavailableAt);
        Assert.True(mb.IsActive);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~MailboxAvailability|FullyQualifiedName~UnavailableMailbox|FullyQualifiedName~ReprobeSucceeds|FullyQualifiedName~OtherFolderError" 2>&1 | grep -E "error CS|Failed|Passed" | head -5`
Expected: build error `The type or namespace name 'MailboxAvailability' could not be found`.

- [ ] **Step 3: Add the classifier**

Create `worker/Services/MailboxAvailability.cs`:

```csharp
using Microsoft.Graph.Models.ODataErrors;

namespace AFHSync.Worker.Services;

/// <summary>
/// Phase 2 (§2.1): classifies the Graph error returned for an enabled Entra account that has no
/// REST-enabled mailbox (soft-deleted, on-prem/hybrid, unlicensed service accounts). Such a
/// mailbox is UNAVAILABLE, not failed: it is stamped on target_mailboxes, skipped for
/// <see cref="ReprobeInterval"/>, then re-probed — forever, weekly. IsActive is untouched.
/// </summary>
public static class MailboxAvailability
{
    public const string UnavailableErrorCode = "MailboxNotEnabledForRESTAPI";
    public const string UnavailableMessageFragment = "inactive, soft-deleted, or is hosted on-premise";
    public static readonly TimeSpan ReprobeInterval = TimeSpan.FromDays(7);

    public static bool IsUnavailable(Exception ex)
    {
        if (ex is ODataError odata)
        {
            if (string.Equals(odata.Error?.Code, UnavailableErrorCode, StringComparison.OrdinalIgnoreCase))
                return true;
            if (odata.Error?.Message?.Contains(UnavailableMessageFragment, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        return ex.Message.Contains(UnavailableMessageFragment, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Classify in `ProcessMailboxAsync` and drop the self-heal**

In `worker/Services/SyncEngine.cs`, in `ProcessMailboxAsync`, the folder lookup `try { (folderId, folderWasCreated) = await contactFolderManager.GetOrCreateFolderAsync(mailbox.EntraId, tunnel.Name, ct); }` is followed by a `catch (Exception ex)` that (1) logs, (2) adds a failed run item, (3) has a `// Self-heal for dead mailboxes …` block that sets `IsActive = false`, and (4) ends with `failed++; return (created, updated, skipped, failed, removed);`. Replace that entire `catch` block with:

```csharp
        catch (Exception ex)
        {
            // Phase 2 (§2.1): an enabled account without a REST-enabled mailbox (soft-deleted,
            // on-prem, unlicensed) is UNAVAILABLE, not failed. Stamp it so LoadTargetMailboxesAsync
            // skips it for a week, write no run item, and don't count it against the tunnel.
            // The old IsActive=false self-heal is gone — RefreshTargetMailboxesAsync flipped it
            // back on the next refresh, so those mailboxes failed every single run.
            if (MailboxAvailability.IsUnavailable(ex))
            {
                logger.LogInformation(
                    "Mailbox {Email} (Id={MailboxId}) is unavailable for REST — skipping for {Days} days: {Reason}",
                    mailbox.Email, mailbox.Id, MailboxAvailability.ReprobeInterval.TotalDays, ex.Message);
                await MarkMailboxUnavailableAsync(mailbox.Id, ex.Message);
                return (created, updated, skipped, failed, removed);
            }

            logger.LogError(ex, "Failed to get/create folder '{FolderName}' in mailbox {MailboxId}", tunnel.Name, mailbox.Id);
            // Record as a SyncRunItem so the failure shows up in the run-detail "Failed" tab.
            runLogger.AddItem(new SyncRunItem
            {
                SyncRunId = run.Id,
                TunnelId = tunnel.Id,
                PhoneListId = canonicalPhoneList.Id,
                TargetMailboxId = mailbox.Id,
                SourceUserId = null,
                Action = "failed",
                ErrorMessage = $"Folder '{tunnel.Name}': {ex.Message}",
                CreatedAt = DateTime.UtcNow
            });
            failed++;
            return (created, updated, skipped, failed, removed);
        }

        // Phase 2 (§2.1): the first successful folder lookup after an unavailable stamp clears it.
        if (mailbox.MailboxUnavailableAt is not null)
            await ClearMailboxUnavailableAsync(mailbox.Id);
```

Then add the two helpers directly after the `ReadParallelismSettingAsync` method:

```csharp
    /// <summary>
    /// Phase 2 (§2.1): stamps a mailbox unavailable. First-seen is preserved; the probe time is
    /// refreshed so the weekly re-probe window restarts. Fresh context + CancellationToken.None
    /// so a run-level cancel doesn't lose the stamp.
    /// </summary>
    private async Task MarkMailboxUnavailableAsync(int mailboxId, string reason)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            var mb = await db.TargetMailboxes.FirstOrDefaultAsync(m => m.Id == mailboxId, CancellationToken.None);
            if (mb is null) return;
            var now = DateTime.UtcNow;
            mb.MailboxUnavailableAt ??= now;
            mb.MailboxLastProbedAt = now;
            mb.MailboxUnavailableReason = reason.Length > 1000 ? reason[..1000] : reason;
            mb.UpdatedAt = now;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stamp mailbox {MailboxId} as unavailable", mailboxId);
        }
    }

    private async Task ClearMailboxUnavailableAsync(int mailboxId)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            var mb = await db.TargetMailboxes.FirstOrDefaultAsync(m => m.Id == mailboxId, CancellationToken.None);
            if (mb is null || mb.MailboxUnavailableAt is null) return;
            mb.MailboxUnavailableAt = null;
            mb.MailboxLastProbedAt = null;
            mb.MailboxUnavailableReason = null;
            mb.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogInformation("Mailbox {Email} (Id={MailboxId}) is available again — cleared unavailable stamp", mb.Email, mb.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear the unavailable stamp on mailbox {MailboxId}", mailboxId);
        }
    }
```

- [ ] **Step 5: Exclude stamped mailboxes in `LoadTargetMailboxesAsync`**

In `worker/Services/SyncEngine.cs`, `LoadTargetMailboxesAsync`:

(a) Replace the opening

```csharp
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var allMailboxes = await db.TargetMailboxes
            .Where(m => m.IsActive)
            .ToListAsync(ct);
```

with

```csharp
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        // Phase 2 (§2.1): skip mailboxes stamped unavailable within the last 7 days; older stamps
        // are included so the mailbox is re-probed (and re-stamped or cleared) weekly, forever.
        var reprobeCutoff = DateTime.UtcNow - MailboxAvailability.ReprobeInterval;
        var allMailboxes = await AvailableActiveMailboxes(db, reprobeCutoff).ToListAsync(ct);
        var excludedUnavailable = await db.TargetMailboxes.CountAsync(
            m => m.IsActive && m.MailboxUnavailableAt != null && m.MailboxLastProbedAt != null && m.MailboxLastProbedAt > reprobeCutoff, ct);
        logger.LogInformation(
            "Tunnel {TunnelName}: {Excluded} target mailbox(es) excluded (unavailable)",
            tunnel.Name, excludedUnavailable);
```

(b) Replace the AllUsers re-read

```csharp
        await using var refreshedDb = await dbContextFactory.CreateDbContextAsync(ct);
        var refreshed = await refreshedDb.TargetMailboxes
            .Where(m => m.IsActive)
            .ToListAsync(ct);
```

with

```csharp
        await using var refreshedDb = await dbContextFactory.CreateDbContextAsync(ct);
        var refreshed = await AvailableActiveMailboxes(refreshedDb, reprobeCutoff).ToListAsync(ct);
```

(c) Add this helper directly after `LoadTargetMailboxesAsync`:

```csharp
    /// <summary>Active mailboxes that are not currently stamped unavailable (or whose stamp is due for a re-probe).</summary>
    private static IQueryable<TargetMailbox> AvailableActiveMailboxes(AFHSyncDbContext db, DateTime reprobeCutoff)
        => db.TargetMailboxes.Where(m => m.IsActive
            && (m.MailboxUnavailableAt == null || m.MailboxLastProbedAt == null || m.MailboxLastProbedAt <= reprobeCutoff));
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 241, Skipped: 1` (232 + 4 classifier + 5 engine).

- [ ] **Step 7: Commit**

```bash
git add worker/Services/MailboxAvailability.cs worker/Services/SyncEngine.cs tests/AFHSync.Tests.Unit/Sync/MailboxAvailabilityTests.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -m "feat(worker): unavailable mailboxes are stamped and skipped for 7 days instead of failing every run

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: Unavailable mailboxes — API endpoint and Targets page section (§2.1 UI)

**Files:**
- Create: `api/DTOs/UnavailableMailboxesDto.cs`
- Create: `api/Controllers/TargetsController.cs`
- Create: `tests/AFHSync.Tests.Integration/Api/TargetsControllerTests.cs`
- Create: `frontend/src/types/targets.ts`
- Modify: `frontend/src/lib/api.ts`
- Create: `frontend/src/hooks/use-targets.ts`
- Create: `frontend/src/components/UnavailableMailboxes.tsx`
- Modify: `frontend/src/app/(app)/lists/page.tsx` (this is the "Targets" page — the sidebar label `Targets` routes to `/lists`; `frontend/src/app/(app)/users` is "User Lookup")

**Interfaces:**
- Produces:
  ```csharp
  // GET /api/targets/unavailable  (new controller: TunnelsController already owns /api/tunnels/target-mailboxes for the picker; a
  // dedicated api/targets route keeps target-mailbox health separate from tunnel CRUD)
  public record UnavailableMailboxesDto(int TotalActive, int Unavailable, IReadOnlyList<UnavailableMailboxDto> Items);
  public record UnavailableMailboxDto(int Id, string? DisplayName, string Email, DateTime UnavailableSince, DateTime? LastCheckedAt, string? Reason);
  // Items ordered oldest UnavailableSince first. TotalActive = COUNT(IsActive); Unavailable = Items.Count.
  ```
  ```ts
  // frontend/src/types/targets.ts
  export interface UnavailableMailboxDto { id: number; displayName: string | null; email: string; unavailableSince: string; lastCheckedAt: string | null; reason: string | null; }
  export interface UnavailableMailboxesDto { totalActive: number; unavailable: number; items: UnavailableMailboxDto[]; }
  // frontend/src/lib/api.ts
  api.targets.unavailable(): Promise<UnavailableMailboxesDto>
  // frontend/src/hooks/use-targets.ts
  useUnavailableMailboxes()   // queryKey ['targets', 'unavailable']
  ```
- Consumes: the three `TargetMailbox` columns (Task 1).

- [ ] **Step 1: Write the failing integration test**

Create `tests/AFHSync.Tests.Integration/Api/TargetsControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AFHSync.Tests.Integration.Api;

/// <summary>Phase 2 (§2.1): GET /api/targets/unavailable lists stamped mailboxes with an "N of M" header.</summary>
[Trait("Category", "Integration")]
public class TargetsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TargetsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    private async Task<HttpResponseMessage> AuthenticatedGetAsync(string url)
    {
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        loginResponse.EnsureSuccessStatusCode();
        var cookie = loginResponse.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task GetUnavailable_ListsStampedMailboxes_OldestFirst_WithTotals()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var now = DateTime.UtcNow;
        db.TargetMailboxes.AddRange(
            new TargetMailbox { EntraId = "t6-ok", Email = "t6-ok@contoso.com", DisplayName = "OK", IsActive = true, CreatedAt = now, UpdatedAt = now },
            new TargetMailbox { EntraId = "t6-newer", Email = "t6-newer@contoso.com", DisplayName = "Newer", IsActive = true, CreatedAt = now, UpdatedAt = now,
                MailboxUnavailableAt = now.AddDays(-1), MailboxLastProbedAt = now.AddDays(-1), MailboxUnavailableReason = "soft-deleted" },
            new TargetMailbox { EntraId = "t6-older", Email = "t6-older@contoso.com", DisplayName = "Older", IsActive = true, CreatedAt = now, UpdatedAt = now,
                MailboxUnavailableAt = now.AddDays(-10), MailboxLastProbedAt = now.AddDays(-3), MailboxUnavailableReason = "on-prem" },
            new TargetMailbox { EntraId = "t6-inactive", Email = "t6-inactive@contoso.com", DisplayName = "Gone", IsActive = false, CreatedAt = now, UpdatedAt = now,
                MailboxUnavailableAt = now.AddDays(-20), MailboxLastProbedAt = now.AddDays(-20), MailboxUnavailableReason = "deleted" });
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync("/api/targets/unavailable");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        var emails = items.Select(i => i.GetProperty("email").GetString()).ToList();
        Assert.Contains("t6-older@contoso.com", emails);
        Assert.Contains("t6-newer@contoso.com", emails);
        Assert.DoesNotContain("t6-ok@contoso.com", emails);
        Assert.DoesNotContain("t6-inactive@contoso.com", emails);        // inactive rows are not target mailboxes
        Assert.True(emails.IndexOf("t6-older@contoso.com") < emails.IndexOf("t6-newer@contoso.com"));  // oldest first
        Assert.Equal(items.Count, body.GetProperty("unavailable").GetInt32());
        Assert.True(body.GetProperty("totalActive").GetInt32() >= 3);
        var older = items.Single(i => i.GetProperty("email").GetString() == "t6-older@contoso.com");
        Assert.Equal("on-prem", older.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrEmpty(older.GetProperty("unavailableSince").GetString()));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet --filter "FullyQualifiedName~TargetsControllerTests" 2>&1 | tail -4`
Expected: FAIL — `Assert.Equal() Failure: Expected OK, Actual NotFound` (no route yet).

- [ ] **Step 3: Add the DTO and controller**

Create `api/DTOs/UnavailableMailboxesDto.cs`:

```csharp
namespace AFHSync.Api.DTOs;

/// <summary>Phase 2 (§2.1): target mailboxes the worker is currently skipping because Graph reports no REST-enabled mailbox.</summary>
public record UnavailableMailboxesDto(
    int TotalActive,
    int Unavailable,
    IReadOnlyList<UnavailableMailboxDto> Items);

public record UnavailableMailboxDto(
    int Id,
    string? DisplayName,
    string Email,
    DateTime UnavailableSince,
    DateTime? LastCheckedAt,
    string? Reason);
```

Create `api/Controllers/TargetsController.cs`:

```csharp
using AFHSync.Api.DTOs;
using AFHSync.Shared.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AFHSync.Api.Controllers;

/// <summary>
/// Target-mailbox health. The picker list lives at /api/tunnels/target-mailboxes; this
/// controller reports on the mailboxes the worker cannot deliver to.
/// </summary>
[ApiController]
[Route("api/targets")]
public class TargetsController(AFHSyncDbContext db) : ControllerBase
{
    /// <summary>
    /// GET /api/targets/unavailable — active target mailboxes stamped unavailable (§2.1),
    /// oldest first, with totals for an "N of M" header. The worker re-probes each one weekly
    /// and clears the stamp on the first successful folder lookup.
    /// </summary>
    [HttpGet("unavailable")]
    public async Task<ActionResult<UnavailableMailboxesDto>> GetUnavailable(CancellationToken ct)
    {
        var totalActive = await db.TargetMailboxes.CountAsync(m => m.IsActive, ct);

        var items = await db.TargetMailboxes
            .Where(m => m.IsActive && m.MailboxUnavailableAt != null)
            .OrderBy(m => m.MailboxUnavailableAt)
            .ThenBy(m => m.Email)
            .Select(m => new UnavailableMailboxDto(
                m.Id,
                m.DisplayName,
                m.Email,
                m.MailboxUnavailableAt!.Value,
                m.MailboxLastProbedAt,
                m.MailboxUnavailableReason))
            .ToListAsync(ct);

        return Ok(new UnavailableMailboxesDto(totalActive, items.Count, items));
    }
}
```

- [ ] **Step 4: Run the integration tests**

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 37, Skipped: 1` (or 38/0 with Postgres).

- [ ] **Step 5: Frontend — type, api client, hook**

Create `frontend/src/types/targets.ts`:

```ts
export interface UnavailableMailboxDto {
  id: number;
  displayName: string | null;
  email: string;
  unavailableSince: string;
  lastCheckedAt: string | null;
  reason: string | null;
}

export interface UnavailableMailboxesDto {
  totalActive: number;
  unavailable: number;
  items: UnavailableMailboxDto[];
}
```

In `frontend/src/lib/api.ts`:

(a) After the line `import type { UserFolderStateDto } from '@/types/user-lookup';` add:

```ts
import type { UnavailableMailboxesDto } from '@/types/targets';
```

(b) Inside the `api` object, directly after the `users: { … },` block, add:

```ts
  targets: {
    unavailable: () => fetchApi<UnavailableMailboxesDto>('/targets/unavailable'),
  },
```

Create `frontend/src/hooks/use-targets.ts`:

```ts
'use client';

import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

export function useUnavailableMailboxes() {
  return useQuery({
    queryKey: ['targets', 'unavailable'],
    queryFn: () => api.targets.unavailable(),
    staleTime: 60 * 1000,
  });
}
```

- [ ] **Step 6: Frontend — the section component and its placement on the Targets page**

Create `frontend/src/components/UnavailableMailboxes.tsx`:

```tsx
'use client';

import { AlertCircle } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { useUnavailableMailboxes } from '@/hooks/use-targets';

function formatDate(iso: string | null): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleString();
}

/**
 * Phase 2 (§2.1): mailboxes the worker skips because Graph reports no REST-enabled mailbox
 * (soft-deleted, on-prem, unlicensed). Each is re-probed weekly; the row disappears on the
 * first successful folder lookup. "N of M" reconciles with the dashboard's Target Users:
 * M − N is the number of mailboxes a run can deliver to.
 */
export function UnavailableMailboxes() {
  const { data, isLoading, error } = useUnavailableMailboxes();

  if (isLoading) {
    return <Skeleton className="h-24 w-full mt-8" />;
  }

  if (error || !data) {
    return (
      <p className="text-sm text-text-muted mt-8">
        Unavailable mailboxes could not be loaded.
      </p>
    );
  }

  return (
    <Card className="mt-8">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <AlertCircle className="size-4 text-amber-600" strokeWidth={1.5} />
          Unavailable mailboxes ({data.unavailable} of {data.totalActive})
        </CardTitle>
        <p className="text-sm text-text-muted">
          Active accounts whose mailbox is inactive, soft-deleted, or hosted on-premise. Contacts
          are not delivered to them; each is re-checked weekly and drops off this list when it
          accepts contacts again.
        </p>
      </CardHeader>
      <CardContent className="pt-0">
        {data.items.length === 0 ? (
          <p className="text-sm text-text-muted py-4">
            Every active target mailbox currently accepts contacts.
          </p>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Email</TableHead>
                <TableHead>Since</TableHead>
                <TableHead>Last checked</TableHead>
                <TableHead>Reason</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.map((m) => (
                <TableRow key={m.id}>
                  <TableCell className="font-medium">{m.displayName ?? '—'}</TableCell>
                  <TableCell className="font-mono text-xs break-all">{m.email}</TableCell>
                  <TableCell title={m.unavailableSince}>{formatDate(m.unavailableSince)}</TableCell>
                  <TableCell title={m.lastCheckedAt ?? undefined}>{formatDate(m.lastCheckedAt)}</TableCell>
                  <TableCell className="text-xs text-text-muted max-w-md truncate" title={m.reason ?? undefined}>
                    {m.reason ?? '—'}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}
```

In `frontend/src/app/(app)/lists/page.tsx`:

(a) After the line `import { DDGSearchList } from '@/components/DDGSearchList';` add:

```ts
import { UnavailableMailboxes } from '@/components/UnavailableMailboxes';
```

(b) In `PhoneListsPage`'s JSX, find the trailing `<ConfirmDialog` whose props start with `open={deleteTarget !== null}` and insert directly **before** it:

```tsx
      {/* Phase 2 (§2.1): mailboxes the worker is currently unable to deliver to */}
      <UnavailableMailboxes />

```

- [ ] **Step 7: Frontend gate**

Run: `cd frontend && npm run build 2>&1 | tail -8; cd ..`
Expected: `✓ Compiled successfully`, no type or lint errors, and `/lists` still in the route table.

Manual check (when the stack is running): the Targets page shows the "Unavailable mailboxes (N of M)" card below the target lists; with no stamped rows it reads "Every active target mailbox currently accepts contacts."

- [ ] **Step 8: Commit**

```bash
git add api/DTOs/UnavailableMailboxesDto.cs api/Controllers/TargetsController.cs tests/AFHSync.Tests.Integration/Api/TargetsControllerTests.cs frontend/src/types/targets.ts frontend/src/lib/api.ts frontend/src/hooks/use-targets.ts frontend/src/components/UnavailableMailboxes.tsx "frontend/src/app/(app)/lists/page.tsx"
git commit -m "feat(targets): GET /api/targets/unavailable and an Unavailable mailboxes section on the Targets page

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: Failed source ⇒ no stale pass (§2.3) and stale reset (§2.4)

**Files:**
- Modify: `worker/Services/ISourceResolver.cs`
- Modify: `worker/Services/SourceResolver.cs` (`ResolveAsync`)
- Modify: `worker/Services/SyncEngine.cs` (`ProcessTunnelAsync` Step 5a, `ProcessMailboxAsync` signature + stale block)
- Modify: `worker/Services/StaleContactHandler.cs`
- Test: `tests/AFHSync.Tests.Unit/Sync/SourceResolverTests.cs`
- Test: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`
- Test: `tests/AFHSync.Tests.Unit/Sync/StaleContactHandlerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // worker/Services/ISourceResolver.cs
  public sealed record SourceFailure(int SourceId, string DisplayName, string Reason);
  public sealed record SourceResolution(List<SourceUser> Users, IReadOnlyList<SourceFailure> FailedSources);
  public interface ISourceResolver { Task<SourceResolution> ResolveAsync(Tunnel tunnel, CancellationToken ct); }
  // SyncEngine.ProcessMailboxAsync gains `bool skipStale` directly after `bool isDryRun`.
  ```
  Behaviour: each `SourceFailure` ⇒ Error log, `tunnelErrors.Add($"{tunnel.Name}: source '{name}' failed: {reason}")`, a `SyncRunItem { Action="failed", TunnelId, ErrorMessage=$"Source '{name}': {reason}" }` (no mailbox/source user), +1 to the tunnel's `failed` count (so it is warned), and `skipStale: true` for every mailbox of that tunnel. Zero users still short-circuits the tunnel. `StaleContactHandler` clears `IsStale`/`StaleDetectedAt` for states whose `SourceUserId` is in the current set, in the same `SaveChangesAsync`.
  Test fakes changed here: `FakeSourceResolver` and `CancellingSourceResolver` (SyncEngineTests) return `SourceResolution`. `SyncEngineTests` is the only test file implementing `ISourceResolver`.
- Consumes: `tunnelErrors` (Phase 1), `FakeRunLogger.FinalizedStatus` (Task 2).

- [ ] **Step 1: Write the failing tests**

In `tests/AFHSync.Tests.Unit/Sync/SourceResolverTests.cs`:

(a) Replace the usings at the top with:

```csharp
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models;
```

(b) Inside the class, before the first `[Fact]`, add:

```csharp
    private static AFHSyncDbContext MakeDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AFHSyncDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AFHSyncDbContext(options);
    }

    private sealed class TestDbContextFactory(string dbName) : IDbContextFactory<AFHSyncDbContext>
    {
        public AFHSyncDbContext CreateDbContext() => MakeDbContext(dbName);
    }

    // ==============================
    // Phase 2 (2.3): a source that throws is REPORTED, not swallowed
    // ==============================

    [Fact]
    public async Task ResolveAsync_SourceThrows_ReportsFailureAndReturnsNoUsers()
    {
        var dbName = Guid.NewGuid().ToString();
        var tunnel = new Tunnel { Id = 1, Name = "Buckhead Staff Tunnel" };
        tunnel.TunnelSources.Add(new TunnelSource
        {
            Id = 11, TunnelId = 1, SourceType = SourceType.Ddg,
            SourceIdentifier = "officeLocation eq 'Buckhead'", SourceDisplayName = "Buckhead Staff"
        });
        // No GraphClientFactory: the Graph call inside the per-source try throws, which is
        // exactly the failure path (network/auth/filter errors) the resolver must report.
        var resolver = new SourceResolver(null!, new TestDbContextFactory(dbName), NullLogger<SourceResolver>.Instance);

        var result = await resolver.ResolveAsync(tunnel, CancellationToken.None);

        Assert.Empty(result.Users);
        var failure = Assert.Single(result.FailedSources);
        Assert.Equal(11, failure.SourceId);
        Assert.Equal("Buckhead Staff", failure.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
    }
```

In `tests/AFHSync.Tests.Unit/Sync/StaleContactHandlerTests.cs`, after `HandleStaleAsync_ReturnsCorrectCountsForRunLogging` add:

```csharp
    // ==============================
    // Phase 2 (2.4): a user who is back in the source set is no longer stale
    // ==============================

    [Fact]
    public async Task HandleStaleAsync_ReturningUser_ResetsStaleFlag_InSameSave()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedCtx = MakeDbContext(dbName);
        seedCtx.ContactSyncStates.AddRange(
            new ContactSyncState { Id = 1, SourceUserId = 1, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1, GraphContactId = "back", IsStale = true, StaleDetectedAt = DateTime.UtcNow.AddDays(-3) },
            new ContactSyncState { Id = 2, SourceUserId = 2, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1, GraphContactId = "still-gone", IsStale = true, StaleDetectedAt = DateTime.UtcNow.AddDays(-3) },
            new ContactSyncState { Id = 3, SourceUserId = 3, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1, GraphContactId = "newly-gone", IsStale = false });
        await seedCtx.SaveChangesAsync();

        var writer = new FakeContactWriter();
        var handler = new StaleContactHandler(CreateFactory(dbName), writer, NullLogger<StaleContactHandler>.Instance);
        var tunnel = CreateTunnel(1, StalePolicy.FlagHold, staleHoldDays: 14);

        var result = await handler.HandleStaleAsync(tunnel, 1, 1, "mailbox@contoso.com", new HashSet<int> { 1 }, CancellationToken.None);

        Assert.Equal(0, result.Removed);
        Assert.Equal(2, result.StaleDetected);           // user 2 (still in hold) + user 3 (newly flagged)
        using var verifyCtx = MakeDbContext(dbName);
        var back = await verifyCtx.ContactSyncStates.SingleAsync(s => s.Id == 1);
        Assert.False(back.IsStale);
        Assert.Null(back.StaleDetectedAt);
        Assert.True((await verifyCtx.ContactSyncStates.SingleAsync(s => s.Id == 2)).IsStale);
        Assert.True((await verifyCtx.ContactSyncStates.SingleAsync(s => s.Id == 3)).IsStale);
    }
```

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`:

(a) Replace `FakeSourceResolver` with:

```csharp
    private sealed class FakeSourceResolver(List<SourceUser> users, IReadOnlyList<SourceFailure>? failures = null) : ISourceResolver
    {
        public int ResolveCallCount { get; private set; }
        public List<int> ResolvedTunnelIds { get; } = [];

        public Task<SourceResolution> ResolveAsync(Tunnel tunnel, CancellationToken ct)
        {
            ResolveCallCount++;
            ResolvedTunnelIds.Add(tunnel.Id);
            return Task.FromResult(new SourceResolution(users, failures ?? []));
        }
    }
```

(b) In `CancellingSourceResolver`, change the method to:

```csharp
        public Task<SourceResolution> ResolveAsync(Tunnel tunnel, CancellationToken ct)
        {
            ResolveCallCount++;
            cts.Cancel();
            return Task.FromResult(new SourceResolution([], []));
        }
```

(c) Add a recording stale handler next to `FakeStaleContactHandler`:

```csharp
    private sealed class RecordingStaleContactHandler : IStaleContactHandler
    {
        public int CallCount { get; private set; }

        public Task<StaleResult> HandleStaleAsync(
            Tunnel tunnel, int phoneListId, int targetMailboxId,
            string mailboxEntraId, HashSet<int> currentSourceUserIds, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new StaleResult(0, 0));
        }
    }
```

(d) Add the test after `RunAsync_OtherFolderError_StillFailsAndDoesNotStamp`:

```csharp
    // ==============================
    // Phase 2 (2.3): a failed source is reported, warns the tunnel and suppresses the stale pass
    // ==============================

    [Fact]
    public async Task RunAsync_SourceFails_RecordsItemWarnsTunnel_SkipsStale_StillWritesResolvedUsers()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
        var resolver = new FakeSourceResolver(
            [new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }],
            [new SourceFailure(11, "Buckhead Staff", "Request_UnsupportedQuery")]);
        var staleHandler = new RecordingStaleContactHandler();
        var contactWriter = new FakeContactWriter();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: resolver, contactWriter: contactWriter,
            staleHandler: staleHandler, runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        var failedItem = Assert.Single(runLogger.AddedItems, i => i.Action == "failed");
        Assert.Equal(1, failedItem.TunnelId);
        Assert.Null(failedItem.TargetMailboxId);
        Assert.Null(failedItem.SourceUserId);
        Assert.Equal("Source 'Buckhead Staff': Request_UnsupportedQuery", failedItem.ErrorMessage);
        Assert.Single(contactWriter.CreatedContactIds);                         // resolved users still written
        Assert.Contains(runLogger.AddedItems, i => i.Action == "created");
        Assert.Equal(0, staleHandler.CallCount);                                 // no stale pass
        Assert.Equal(SyncStatus.Warning, runLogger.FinalizedStatus);
        Assert.Contains("Avail Tunnel: source 'Buckhead Staff' failed: Request_UnsupportedQuery", runLogger.FinalizedErrorSummary);
    }

    [Fact]
    public async Task RunAsync_SourceFailsAndNoUsers_SkipsTunnelButStillWarns()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
        var resolver = new FakeSourceResolver([], [new SourceFailure(11, "Buckhead Staff", "boom")]);
        var staleHandler = new RecordingStaleContactHandler();
        var folderManager = new FakeContactFolderManager();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: resolver, folderManager: folderManager,
            staleHandler: staleHandler, runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Empty(folderManager.Requested);                                   // no mailbox was touched
        Assert.Equal(0, staleHandler.CallCount);
        Assert.Single(runLogger.AddedItems, i => i.Action == "failed");
        Assert.Equal(SyncStatus.Warning, runLogger.FinalizedStatus);
    }
```

- [ ] **Step 2: Run to verify they fail to compile**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | grep -E "error CS" | head -3`
Expected: `error CS0246: The type or namespace name 'SourceResolution' could not be found`.

- [ ] **Step 3: Change the resolver contract**

Replace the whole of `worker/Services/ISourceResolver.cs` with:

```csharp
using AFHSync.Shared.Entities;

namespace AFHSync.Worker.Services;

/// <summary>Phase 2 (§2.3): one tunnel source that could not contribute members this run.</summary>
public sealed record SourceFailure(int SourceId, string DisplayName, string Reason);

/// <summary>
/// Resolved, deduplicated, upserted source users plus every source that failed. A non-empty
/// <see cref="FailedSources"/> means <see cref="Users"/> is INCOMPLETE — the engine must not run
/// the stale pass against it.
/// </summary>
public sealed record SourceResolution(List<SourceUser> Users, IReadOnlyList<SourceFailure> FailedSources);

/// <summary>
/// Resolves source members for a tunnel by querying Microsoft Graph /users
/// with the tunnel's stored $filter, paginating with PageIterator, applying
/// post-query filtering, upserting to the database, and returning the filtered list.
/// </summary>
public interface ISourceResolver
{
    Task<SourceResolution> ResolveAsync(Tunnel tunnel, CancellationToken ct);
}
```

In `worker/Services/SourceResolver.cs`, `ResolveAsync`:

(a) Change the signature to `public async Task<SourceResolution> ResolveAsync(Tunnel tunnel, CancellationToken ct)`.

(b) After `var allSourceUsers = new List<SourceUser>();` add:

```csharp
        var failures = new List<SourceFailure>();
```

(c) Replace the per-source `catch (Exception ex)` body so it reads:

```csharp
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Source {SourceId} ({SourceType}, {SourceIdentifier}) failed for tunnel {TunnelId} — skipping this source",
                    source.Id, source.SourceType, source.SourceIdentifier, tunnel.Id);
                // Phase 2 (§2.3): report it — the engine records a run item and skips the stale pass.
                failures.Add(new SourceFailure(
                    source.Id,
                    string.IsNullOrWhiteSpace(source.SourceDisplayName) ? source.SourceIdentifier : source.SourceDisplayName,
                    ex.Message));
            }
```

(d) Change the final `return reloaded;` to `return new SourceResolution(reloaded, failures);`.

- [ ] **Step 4: Record failures in `SyncEngine` and thread `skipStale`**

In `worker/Services/SyncEngine.cs`, `ProcessTunnelAsync`:

(a) Replace

```csharp
        // Step 5a: Resolve source members.
        var sourceUsers = await sourceResolver.ResolveAsync(tunnel, ct);
        if (sourceUsers.Count == 0)
        {
            logger.LogWarning("Tunnel {TunnelName}: 0 source members resolved, skipping", tunnel.Name);
            return (0, 0, 0, 0, 0);
        }
```

with

```csharp
        // Step 5a: Resolve source members.
        var resolution = await sourceResolver.ResolveAsync(tunnel, ct);
        var sourceUsers = resolution.Users;

        // Phase 2 (§2.3): a source that failed means the current set is INCOMPLETE. Record each
        // failure (run item + tunnelErrors), count the tunnel as warned, and skip the stale pass
        // so nobody is flagged or removed because their source was unreachable this run.
        int sourceFailures = 0;
        foreach (var failure in resolution.FailedSources)
        {
            logger.LogError("Tunnel {TunnelName}: source '{Source}' failed: {Reason}",
                tunnel.Name, failure.DisplayName, failure.Reason);
            tunnelErrors.Add($"{tunnel.Name}: source '{failure.DisplayName}' failed: {failure.Reason}");
            runLogger.AddItem(new SyncRunItem
            {
                SyncRunId = run.Id,
                TunnelId = tunnel.Id,
                Action = "failed",
                ErrorMessage = $"Source '{failure.DisplayName}': {failure.Reason}",
                CreatedAt = DateTime.UtcNow
            });
            sourceFailures++;
        }
        var skipStale = sourceFailures > 0;

        if (sourceUsers.Count == 0)
        {
            logger.LogWarning("Tunnel {TunnelName}: 0 source members resolved, skipping", tunnel.Name);
            return (0, 0, 0, sourceFailures, 0);
        }
```

(b) Change the "no phone lists configured" early return `return (0, 0, 0, 0, 0);` (the one after `logger.LogWarning("Tunnel {TunnelName}: no phone lists configured, skipping", tunnel.Name);`) to `return (0, 0, 0, sourceFailures, 0);`.

(c) Change `failed += ddgTargetFailures;` to `failed += ddgTargetFailures + sourceFailures;`.

(d) In the mailbox lambda, change the `ProcessMailboxAsync(` call's last argument line from `sourceUsers, fieldSettings, isDryRun, ct);` to `sourceUsers, fieldSettings, isDryRun, skipStale, ct);`.

In `ProcessMailboxAsync`:

(e) Add the parameter `bool skipStale,` on its own line directly after `bool isDryRun,`.

(f) Replace

```csharp
        // Handle stale contacts after processing all source users.
        // Check across all phone lists for this tunnel+mailbox (stale handler scopes by phone list,
        // so call it for each phone list to catch records from any phone list).
        if (!isDryRun)
```

with

```csharp
        // Handle stale contacts after processing all source users.
        // Check across all phone lists for this tunnel+mailbox (stale handler scopes by phone list,
        // so call it for each phone list to catch records from any phone list).
        // Phase 2 (§2.3): skipped when any source failed — the current set is incomplete.
        if (!isDryRun && !skipStale)
```

- [ ] **Step 5: Reset returning users in `StaleContactHandler`**

In `worker/Services/StaleContactHandler.cs`, directly after the `var staleStates = existingStates.Where(…).ToList();` statement and before `int removed = 0;`, add:

```csharp
        // Phase 2 (§2.4): a source user who is back in the current set is no longer stale.
        // Saved below in the same SaveChangesAsync as the stale marking (FlagHold and Leave;
        // AutoRemove deletes rows so there is nothing to reset).
        int reset = 0;
        foreach (var state in existingStates)
        {
            if (state.IsStale && currentSourceUserIds.Contains(state.SourceUserId))
            {
                state.IsStale = false;
                state.StaleDetectedAt = null;
                reset++;
            }
        }
        if (reset > 0)
            logger.LogInformation("Reset stale flag on {Count} contact(s) that returned to the source set in mailbox {MailboxId}",
                reset, targetMailboxId);
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 245, Skipped: 1` (241 + 1 resolver + 1 stale + 2 engine).

- [ ] **Step 7: Commit**

```bash
git add worker/Services/ISourceResolver.cs worker/Services/SourceResolver.cs worker/Services/SyncEngine.cs worker/Services/StaleContactHandler.cs tests/AFHSync.Tests.Unit/Sync/SourceResolverTests.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs tests/AFHSync.Tests.Unit/Sync/StaleContactHandlerTests.cs
git commit -m "fix(worker): failed sources are recorded and suppress the stale pass; returning users are un-flagged

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: Dry runs write nothing; no-id batch step is a failure (§2.2) — includes the `IContactFolderManager` signature change

**Files:**
- Modify: `worker/Services/IContactFolderManager.cs`
- Modify: `worker/Services/ContactFolderManager.cs`
- Modify: `worker/Services/PhotoSyncService.cs` (folder call in `SyncPhotosForTunnelAsync`)
- Modify: `worker/Services/ContactWriter.cs` (create-result mapping, parse-failure catch)
- Modify: `worker/Services/SyncEngine.cs` (`ProcessMailboxAsync` dry-run guards)
- Test: `tests/AFHSync.Tests.Unit/Sync/ContactFolderManagerTests.cs` (rewrite)
- Test: `tests/AFHSync.Tests.Unit/Sync/PhotoSyncServiceTests.cs` (`FakeContactFolderManager` signature)
- Test: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` (`FakeContactFolderManager`, `FakeContactWriter.CreateReturnsNoId`, 3 tests)
- Test: `tests/AFHSync.Tests.Unit/Sync/ContactWriterTests.cs`

**Interfaces:**
- Produces (Task 10 keeps this signature unchanged and only adds seams):
  ```csharp
  // worker/Services/IContactFolderManager.cs
  Task<(string? folderId, bool wasCreated)> GetOrCreateFolderAsync(
      Tunnel tunnel, TargetMailbox mailbox, bool isDryRun, CancellationToken ct);
  // folderId is null ONLY when isDryRun && the folder does not exist ("would create").
  // wasCreated is true ONLY when this call created the folder — never in a dry run.
  void ResetCache();

  // worker/Services/ContactFolderManager.cs
  public sealed record GraphFolderInfo(string Id, string? DisplayName);
  protected virtual Task<GraphFolderInfo?> FindFolderByNameAsync(string mailboxEntraId, string folderName, CancellationToken ct);
  protected virtual Task<string> CreateFolderAsync(string mailboxEntraId, string folderName, CancellationToken ct);
  // FetchOrCreateFolderFromGraphAsync is REMOVED. Cache key becomes "{mailbox.EntraId}:{tunnel.Id}".

  // worker/Services/ContactWriter.cs
  public const string NoContactIdError = "no contact id in response";
  internal static BatchOperationResult MapCreateResponse(Microsoft.Graph.Models.Contact? created);
  ```
  Behaviour in `SyncEngine.ProcessMailboxAsync` for `isDryRun`: folder looked up never created; no `folderWasCreated` wipe; no duplicate cleanup (Graph or DB); no `contact_sync_state` insert/update/delete; no stale pass; run items still emitted; with no folder every source user is reported "created".
  Test fakes: `FakeContactFolderManager` in **SyncEngineTests** (adds `MissingFolderMailboxes`, `CreateCount`) and in **PhotoSyncServiceTests** (signature only); `ContactFolderManagerTests` subclass overrides the two new seams. `FakeContactWriter` (SyncEngineTests) gains `bool CreateReturnsNoId`.
- Consumes: `Tunnel`/`TargetMailbox` entities; `MailboxAvailability` classification (Task 5) still wraps the folder call.

- [ ] **Step 1: Write the failing tests**

Replace the whole of `tests/AFHSync.Tests.Unit/Sync/ContactFolderManagerTests.cs` with:

```csharp
using AFHSync.Shared.Entities;
using AFHSync.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Tests for ContactFolderManager — lazy folder creation with a per-run cache. A subclass
/// intercepts the Graph seams so no real Graph call is made.
/// </summary>
public class ContactFolderManagerTests
{
    private static Tunnel T(int id, string name) => new() { Id = id, Name = name };
    private static TargetMailbox M(int id, string entraId) => new() { Id = id, EntraId = entraId, Email = $"{entraId}@test.com" };

    private sealed class FakeContactFolderManager : ContactFolderManager
    {
        public int LookupCount { get; private set; }
        public int CreateCount { get; private set; }
        public int GraphCallCount => LookupCount + CreateCount;

        // mailboxEntraId -> folderId for the fake backend (one folder per mailbox is enough here)
        private readonly Dictionary<string, string> _foldersByMailbox;

        public FakeContactFolderManager(Dictionary<string, string>? backend = null)
            : base(null!, NullLogger<ContactFolderManager>.Instance)
        {
            _foldersByMailbox = backend ?? new Dictionary<string, string>();
        }

        protected override Task<GraphFolderInfo?> FindFolderByNameAsync(string mailboxEntraId, string folderName, CancellationToken ct)
        {
            LookupCount++;
            return Task.FromResult(_foldersByMailbox.TryGetValue(mailboxEntraId, out var id)
                ? new GraphFolderInfo(id, folderName)
                : null);
        }

        protected override Task<string> CreateFolderAsync(string mailboxEntraId, string folderName, CancellationToken ct)
        {
            CreateCount++;
            var id = $"folder-{mailboxEntraId}-{folderName}";
            _foldersByMailbox[mailboxEntraId] = id;
            return Task.FromResult(id);
        }
    }

    [Fact]
    public async Task GetOrCreateFolderAsync_ReturnsCachedId_OnSecondCall_ForSameMailbox()
    {
        var fake = new FakeContactFolderManager();
        var tunnel = T(1, "AFH Contacts");
        var mailbox = M(1, "mailbox-1");

        var (id1, created1) = await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);
        var (id2, created2) = await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);

        Assert.Equal(id1, id2);
        Assert.True(created1);
        Assert.False(created2);
        Assert.Equal(2, fake.GraphCallCount); // one lookup + one create; second call hits the cache
    }

    [Fact]
    public async Task GetOrCreateFolderAsync_ReturnsDifferentIds_ForDifferentMailboxes()
    {
        var fake = new FakeContactFolderManager();
        var tunnel = T(1, "AFH Contacts");

        var (id1, _) = await fake.GetOrCreateFolderAsync(tunnel, M(1, "mailbox-a"), false, CancellationToken.None);
        var (id2, _) = await fake.GetOrCreateFolderAsync(tunnel, M(2, "mailbox-b"), false, CancellationToken.None);

        Assert.NotEqual(id1, id2);
        Assert.Equal(2, fake.CreateCount);
    }

    [Fact]
    public async Task ResetCache_ClearsAllCachedEntries_ForcingNewGraphCalls()
    {
        var fake = new FakeContactFolderManager();
        var tunnel = T(1, "AFH Contacts");
        var mailbox = M(1, "mailbox-x");

        await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);
        Assert.Equal(2, fake.GraphCallCount);

        fake.ResetCache();

        var (_, wasCreated) = await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);
        Assert.False(wasCreated);                 // the backend already has it now
        Assert.Equal(3, fake.GraphCallCount);     // one more lookup, no create
    }

    [Fact]
    public async Task GetOrCreateFolderAsync_ExistingFolder_IsNotReportedAsCreated()
    {
        var fake = new FakeContactFolderManager(new() { ["mailbox-1"] = "existing-folder" });

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "AFH Contacts"), M(1, "mailbox-1"), false, CancellationToken.None);

        Assert.Equal("existing-folder", id);
        Assert.False(wasCreated);
        Assert.Equal(0, fake.CreateCount);
    }

    // ── Phase 2 (2.2): dry runs never create ─────────────────────────────────

    [Fact]
    public async Task GetOrCreateFolderAsync_DryRun_MissingFolder_ReturnsNullAndNeverCreates()
    {
        var fake = new FakeContactFolderManager();

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "AFH Contacts"), M(1, "mailbox-1"), true, CancellationToken.None);

        Assert.Null(id);
        Assert.False(wasCreated);
        Assert.Equal(1, fake.LookupCount);
        Assert.Equal(0, fake.CreateCount);
    }

    [Fact]
    public async Task GetOrCreateFolderAsync_DryRun_ExistingFolder_ReturnsItsId()
    {
        var fake = new FakeContactFolderManager(new() { ["mailbox-1"] = "existing-folder" });

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "AFH Contacts"), M(1, "mailbox-1"), true, CancellationToken.None);

        Assert.Equal("existing-folder", id);
        Assert.False(wasCreated);
    }
}
```

In `tests/AFHSync.Tests.Unit/Sync/ContactWriterTests.cs`, add after the last test:

```csharp
    // ── Phase 2 (2.2): a 2xx batch step without an id is NOT a success ───────

    [Fact]
    public void MapCreateResponse_NullOrIdLessContact_IsFailureWithNoIdError()
    {
        var fromNull = ContactWriter.MapCreateResponse(null);
        Assert.False(fromNull.Success);
        Assert.Equal("no contact id in response", fromNull.Error);

        var fromIdLess = ContactWriter.MapCreateResponse(new Contact { Id = null });
        Assert.False(fromIdLess.Success);
        Assert.Equal(ContactWriter.NoContactIdError, fromIdLess.Error);
    }

    [Fact]
    public void MapCreateResponse_ContactWithId_IsSuccess()
    {
        var result = ContactWriter.MapCreateResponse(new Contact { Id = "AAMkAG-abc" });

        Assert.True(result.Success);
        Assert.Equal("AAMkAG-abc", result.GraphContactId);
    }
```

In `tests/AFHSync.Tests.Unit/Sync/PhotoSyncServiceTests.cs`, replace `FakeContactFolderManager` with:

```csharp
    private sealed class FakeContactFolderManager : IContactFolderManager
    {
        public Task<(string? folderId, bool wasCreated)> GetOrCreateFolderAsync(
            Tunnel tunnel, TargetMailbox mailbox, bool isDryRun, CancellationToken ct)
            => Task.FromResult<(string? folderId, bool wasCreated)>(("fake-folder-id", false));

        public void ResetCache() { }
    }
```

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`:

(a) Replace `FakeContactFolderManager` with:

```csharp
    private sealed class FakeContactFolderManager : IContactFolderManager
    {
        /// <summary>Mailboxes (by EntraId) whose folder lookup throws the given exception.</summary>
        public Dictionary<string, Exception> Failures { get; } = new();

        /// <summary>Every mailbox EntraId the engine asked a folder for, in call order.</summary>
        public List<string> Requested { get; } = [];

        /// <summary>Mailboxes (by EntraId) with no folder yet: a real run "creates" it (wasCreated=true); a dry run gets null.</summary>
        public HashSet<string> MissingFolderMailboxes { get; } = [];

        public int CreateCount { get; private set; }

        public Task<(string? folderId, bool wasCreated)> GetOrCreateFolderAsync(
            Tunnel tunnel, TargetMailbox mailbox, bool isDryRun, CancellationToken ct)
        {
            Requested.Add(mailbox.EntraId);
            if (Failures.TryGetValue(mailbox.EntraId, out var ex))
                throw ex;
            if (MissingFolderMailboxes.Contains(mailbox.EntraId))
            {
                if (isDryRun)
                    return Task.FromResult<(string? folderId, bool wasCreated)>((null, false));
                CreateCount++;
                return Task.FromResult<(string? folderId, bool wasCreated)>(($"created-{mailbox.EntraId}", true));
            }
            return Task.FromResult<(string? folderId, bool wasCreated)>(("fake-folder-id", false));
        }

        public void ResetCache() { }
    }
```

(b) In `FakeContactWriter`, add the property

```csharp
        /// <summary>When true, batch creates report the no-id failure (Graph 2xx without an id).</summary>
        public bool CreateReturnsNoId { get; init; }
```

and change the loop body of `CreateContactsBatchAsync` to:

```csharp
            foreach (var (key, _) in operations)
            {
                if (CreateReturnsNoId)
                {
                    results[key] = new BatchOperationResult(false, Error: ContactWriter.NoContactIdError);
                    continue;
                }
                var id = Guid.NewGuid().ToString();
                CreatedContactIds.Add(id);
                results[key] = new BatchOperationResult(true, id);
            }
```

(c) Add the tests after `RunAsync_SourceFailsAndNoUsers_SkipsTunnelButStillWarns`:

```csharp
    // ==============================
    // Phase 2 (2.2): dry runs write nothing
    // ==============================

    [Fact]
    public async Task RunAsync_DryRun_LeavesStateAndFolderUntouched_ButStillReportsItems()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.ContactSyncStates.AddRange(
                // Alice: existing with an OLD hash ⇒ "would update"
                new ContactSyncState { Id = 1, SourceUserId = 1, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1, GraphContactId = "g1", DataHash = "old-hash", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                // a duplicate row for Alice that a real run would delete (Graph + DB)
                new ContactSyncState { Id = 2, SourceUserId = 1, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1, GraphContactId = "g1-dupe", DataHash = "old-hash", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await seedCtx.SaveChangesAsync();
        }
        var contactWriter = new FakeContactWriter();
        var staleHandler = new RecordingStaleContactHandler();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([
                new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" },
                new SourceUser { Id = 3, EntraId = "u3", DisplayName = "Carol" }]),   // new ⇒ "would create"
            contactWriter: contactWriter, staleHandler: staleHandler, runLogger: runLogger);

        await engine.RunAsync(null, RunType.DryRun, isDryRun: true, CancellationToken.None);

        Assert.Contains(runLogger.AddedItems, i => i.Action == "created" && i.SourceUserId == 3);
        Assert.Contains(runLogger.AddedItems, i => i.Action == "updated" && i.SourceUserId == 1);
        Assert.Empty(contactWriter.CreatedContactIds);
        Assert.Empty(contactWriter.UpdatedContactIds);
        Assert.Empty(contactWriter.DeletedContactIds);                 // no duplicate cleanup in Graph
        Assert.Equal(0, staleHandler.CallCount);                       // no stale pass
        await using var verifyCtx = MakeDbContext(dbName);
        var states = await verifyCtx.ContactSyncStates.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(2, states.Count);                                 // no insert, no duplicate delete
        Assert.Equal("old-hash", states[0].DataHash);                  // no update
        Assert.Equal("g1-dupe", states[1].GraphContactId);
    }

    [Fact]
    public async Task RunAsync_DryRun_NoFolder_NeverCreates_AndReportsEveryContactAsCreate()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
        using (var seedCtx = MakeDbContext(dbName))
        {
            // A state row with a MATCHING hash: if the folder existed this would be "skipped".
            seedCtx.ContactSyncStates.Add(new ContactSyncState { Id = 1, SourceUserId = 1, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1, GraphContactId = "g1", DataHash = "new-hash", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await seedCtx.SaveChangesAsync();
        }
        var folderManager = new FakeContactFolderManager();
        folderManager.MissingFolderMailboxes.Add("mbx");
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([
                new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" },
                new SourceUser { Id = 2, EntraId = "u2", DisplayName = "Bob" }]),
            folderManager: folderManager, runLogger: runLogger);

        await engine.RunAsync(null, RunType.DryRun, isDryRun: true, CancellationToken.None);

        Assert.Equal(0, folderManager.CreateCount);
        Assert.Equal(2, runLogger.AddedItems.Count(i => i.Action == "created"));
        Assert.DoesNotContain(runLogger.AddedItems, i => i.Action == "updated");
        Assert.Equal(2, runLogger.FinalizedCreated);
        Assert.Equal(0, runLogger.FinalizedSkipped);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(1, await verifyCtx.ContactSyncStates.CountAsync());
    }

    [Fact]
    public async Task RunAsync_CreateWithoutContactId_IsFailedAndWritesNoStateRow()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
        var contactWriter = new FakeContactWriter { CreateReturnsNoId = true };
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            contactWriter: contactWriter, runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        var failedItem = Assert.Single(runLogger.AddedItems, i => i.Action == "failed");
        Assert.Equal("no contact id in response", failedItem.ErrorMessage);
        Assert.Equal(1, runLogger.FinalizedFailed);
        Assert.Equal(0, runLogger.FinalizedCreated);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Empty(await verifyCtx.ContactSyncStates.ToListAsync());
    }
```

- [ ] **Step 2: Run to verify they fail to compile**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | grep -E "error CS" | head -3`
Expected: errors such as `'IContactFolderManager' does not contain a definition for 'GetOrCreateFolderAsync' that takes 4 arguments` / `no suitable method found to override` (`FindFolderByNameAsync`) / `'ContactWriter' does not contain a definition for 'MapCreateResponse'`.

- [ ] **Step 3: Change the folder-manager contract and implementation**

Replace the whole of `worker/Services/IContactFolderManager.cs` with:

```csharp
using AFHSync.Shared.Entities;

namespace AFHSync.Worker.Services;

/// <summary>
/// Manages contact folders in target mailboxes — lazily creating them when they don't
/// exist and caching folder IDs for the duration of a sync run to avoid redundant
/// Graph API calls across parallel mailbox tasks.
/// </summary>
public interface IContactFolderManager
{
    /// <summary>
    /// Returns the ID of the tunnel's contact folder in the given mailbox, creating it if it
    /// doesn't exist. Results are cached per (mailbox, tunnel) for the duration of the sync run
    /// (reset between runs via <see cref="ResetCache"/>).
    /// </summary>
    /// <param name="tunnel">The tunnel; its Name is the folder's display name.</param>
    /// <param name="mailbox">The target mailbox (EntraId is used for Graph, Id for bookkeeping).</param>
    /// <param name="isDryRun">
    /// Phase 2 (§2.2): when true the folder is only looked up, never created (and never renamed).
    /// A missing folder yields <c>folderId = null</c> — every contact is then "would create".
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// (folderId, wasCreated). folderId is null only in a dry run when the folder does not exist.
    /// wasCreated is true only when this call created the folder (never in a dry run).
    /// </returns>
    Task<(string? folderId, bool wasCreated)> GetOrCreateFolderAsync(
        Tunnel tunnel,
        TargetMailbox mailbox,
        bool isDryRun,
        CancellationToken ct);

    /// <summary>
    /// Clears the folder ID cache. Called at the start of each sync run so that
    /// folders deleted between runs are re-discovered rather than returning stale IDs.
    /// </summary>
    void ResetCache();
}
```

Replace the whole of `worker/Services/ContactFolderManager.cs` with:

```csharp
using System.Collections.Concurrent;
using AFHSync.Shared.Entities;
using AFHSync.Worker.Graph;
using Microsoft.Graph.Models;

namespace AFHSync.Worker.Services;

/// <summary>A Graph contact folder as seen by the folder manager's Graph seams.</summary>
public sealed record GraphFolderInfo(string Id, string? DisplayName);

/// <summary>
/// Creates contact folders lazily per mailbox and caches their IDs in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> for the duration of a sync run.
///
/// Thread-safe: multiple parallel mailbox tasks (bounded by semaphore in SyncEngine)
/// may call <see cref="GetOrCreateFolderAsync"/> concurrently. A per-key lock ensures
/// only one Graph round-trip is made per (mailbox, tunnel) even under concurrent access.
///
/// Lifecycle: one instance per sync run scope (registered as Scoped in DI). The SyncEngine
/// calls <see cref="ResetCache"/> at the start of each run so stale folder IDs from
/// previous runs don't persist.
///
/// Graph SDK calls are <c>protected virtual</c> seams so unit tests can subclass this class.
/// </summary>
public class ContactFolderManager : IContactFolderManager
{
    private readonly GraphClientFactory? _graphClientFactory;
    private readonly ILogger<ContactFolderManager> _logger;

    // ConcurrentDictionary: key = "mailboxEntraId:tunnelId", value = folderId.
    private readonly ConcurrentDictionary<string, string> _folderCache = new();

    // Per-key locks to prevent concurrent Graph calls for the same folder.
    // Without this, two parallel tasks could both miss the cache and both POST
    // a folder create to Graph, resulting in duplicate folders.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();

    public ContactFolderManager(GraphClientFactory graphClientFactory, ILogger<ContactFolderManager> logger)
    {
        _graphClientFactory = graphClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(string? folderId, bool wasCreated)> GetOrCreateFolderAsync(
        Tunnel tunnel,
        TargetMailbox mailbox,
        bool isDryRun,
        CancellationToken ct)
    {
        var cacheKey = $"{mailbox.EntraId}:{tunnel.Id}";

        // Fast path: return cached folder ID without Graph call
        if (_folderCache.TryGetValue(cacheKey, out var cachedId))
            return (cachedId, false);

        // Slow path: acquire per-key lock so only one Graph call fires per folder
        var keyLock = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock — another task may have populated the cache
            if (_folderCache.TryGetValue(cacheKey, out cachedId))
                return (cachedId, false);

            var existing = await FindFolderByNameAsync(mailbox.EntraId, tunnel.Name, ct);
            if (existing is not null)
            {
                _logger.LogDebug(
                    "Found existing contact folder '{FolderName}' ({FolderId}) in mailbox {MailboxId}",
                    tunnel.Name, existing.Id, mailbox.EntraId);
                _folderCache.TryAdd(cacheKey, existing.Id);
                return (existing.Id, false);
            }

            if (isDryRun)
            {
                // Phase 2 (§2.2): dry runs never create. Not cached — a null is not a folder.
                _logger.LogInformation(
                    "Dry run: contact folder '{FolderName}' does not exist in mailbox {MailboxId} — would create",
                    tunnel.Name, mailbox.EntraId);
                return (null, false);
            }

            _logger.LogInformation(
                "Creating contact folder '{FolderName}' in mailbox {MailboxId}",
                tunnel.Name, mailbox.EntraId);
            var createdId = await CreateFolderAsync(mailbox.EntraId, tunnel.Name, ct);
            _folderCache.TryAdd(cacheKey, createdId);
            return (createdId, true);
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <inheritdoc />
    public void ResetCache()
    {
        _folderCache.Clear();
        _keyLocks.Clear();
        _logger.LogDebug("Contact folder cache cleared for new sync run");
    }

    private Microsoft.Graph.GraphServiceClient Client =>
        _graphClientFactory?.Client
        ?? throw new InvalidOperationException("GraphClientFactory is required for Graph operations");

    // ==============================
    // Protected virtual Graph seams (overridden in unit tests)
    // ==============================

    /// <summary>Queries Graph for a contact folder whose displayName equals <paramref name="folderName"/>.</summary>
    protected virtual async Task<GraphFolderInfo?> FindFolderByNameAsync(
        string mailboxEntraId, string folderName, CancellationToken ct)
    {
        var foldersResponse = await Client
            .Users[mailboxEntraId]
            .ContactFolders
            .GetAsync(config =>
            {
                var escapedName = folderName.Replace("'", "''");
                config.QueryParameters.Filter = $"displayName eq '{escapedName}'";
                config.QueryParameters.Top = 1;
            }, cancellationToken: ct);

        var existingFolder = foldersResponse?.Value?.FirstOrDefault();
        return existingFolder?.Id is null ? null : new GraphFolderInfo(existingFolder.Id, existingFolder.DisplayName);
    }

    /// <summary>Creates a contact folder and returns its id.</summary>
    protected virtual async Task<string> CreateFolderAsync(
        string mailboxEntraId, string folderName, CancellationToken ct)
    {
        var created = await Client
            .Users[mailboxEntraId]
            .ContactFolders
            .PostAsync(new ContactFolder { DisplayName = folderName }, cancellationToken: ct);

        if (created?.Id is null)
            throw new InvalidOperationException(
                $"Graph returned null folder ID after POST for mailbox {mailboxEntraId}");

        return created.Id;
    }
}
```

In `worker/Services/PhotoSyncService.cs`, in `SyncPhotosForTunnelAsync`'s mailbox lambda, replace

```csharp
                var states = mailboxGroup.ToList();
                var mailboxEntraId = states.First().TargetMailbox.EntraId;
```

…through the `var (folderId, _) = await _contactFolderManager.GetOrCreateFolderAsync(\n                    mailboxEntraId, tunnel.Name, ct);` statement, with:

```csharp
                var states = mailboxGroup.ToList();
                var mailboxEntity = states.First().TargetMailbox;
                var mailboxEntraId = mailboxEntity.EntraId;

                // Resolve the contact folder ID for this mailbox+tunnel.
                // Photos must be written via the ContactFolders path since contacts
                // live in a subfolder (e.g. "Buckhead-test"), not the root contacts
                // collection. The flat /contacts/{id}/photo path does not reliably
                // resolve the photo sub-resource for subfolder contacts.
                var (folderId, _) = await _contactFolderManager.GetOrCreateFolderAsync(
                    tunnel, mailboxEntity, isDryRun, ct);
                if (folderId is null)
                {
                    // Phase 2 (§2.2): dry run against a mailbox with no folder — nothing to photo-sync.
                    _logger.LogInformation(
                        "Dry run: no contact folder for tunnel {TunnelId} in mailbox {MailboxId} — skipping photo pass",
                        tunnel.Id, mailboxEntraId);
                    return;
                }
```

(Keep the comment block that precedes the folder call if you prefer; the important part is the new call and the null guard before `ProcessMailboxPhotosAsync`.)

- [ ] **Step 4: No-id batch steps fail in `ContactWriter`**

In `worker/Services/ContactWriter.cs`:

(a) After `private const int MaxBatchSize = 20;` add:

```csharp
    /// <summary>Phase 2 (§2.2): a 2xx batch step whose body has no contact id (or does not parse).</summary>
    public const string NoContactIdError = "no contact id in response";

    /// <summary>Maps a create-step response to a result; no id ⇒ failure, so no state row is written for it.</summary>
    internal static BatchOperationResult MapCreateResponse(Contact? created)
        => string.IsNullOrEmpty(created?.Id)
            ? new BatchOperationResult(false, Error: NoContactIdError)
            : new BatchOperationResult(true, created.Id);
```

(b) In `CreateContactsBatchAsync`, change the `onSuccess` lambda body

```csharp
                var created = await response.GetResponseByIdAsync<Contact>(stepId);
                return new BatchOperationResult(true, created?.Id);
```

to

```csharp
                var created = await response.GetResponseByIdAsync<Contact>(stepId);
                return MapCreateResponse(created);
```

(c) In `ExecuteBatchWithRetryAsync`, change the parse-failure fallback

```csharp
                    _logger.LogWarning(ex, "Failed to parse batch response for step {StepId}", stepId);
                    results[key] = new BatchOperationResult(true);
```

to

```csharp
                    _logger.LogWarning(ex, "Failed to parse batch response for step {StepId}", stepId);
                    results[key] = new BatchOperationResult(false, Error: NoContactIdError);
```

- [ ] **Step 5: Guard every dry-run write in `SyncEngine.ProcessMailboxAsync`**

In `worker/Services/SyncEngine.cs`, `ProcessMailboxAsync`:

(a) Change the folder variables and call:

```csharp
        // Get or create the contact folder (looked up only, never created, in a dry run).
        string? folderId;
        bool folderWasCreated;
        try
        {
            (folderId, folderWasCreated) = await contactFolderManager.GetOrCreateFolderAsync(tunnel, mailbox, isDryRun, ct);
        }
```

(b) Change `if (folderWasCreated)` (the "folder was just created ⇒ clear stale sync state" block) to `if (folderWasCreated && !isDryRun)`.

(c) Directly after the `existingStates` dictionary is built (the statement ending with `.ThenBy(s => s.Id)\n                      .First());`), add:

```csharp

        // Phase 2 (§2.2): a dry run against a mailbox with no folder — every contact "would create".
        if (isDryRun && folderId is null)
            existingStates = new Dictionary<int, ContactSyncState>();
```

(d) Change `if (duplicateStates.Count > 0)` to `if (duplicateStates.Count > 0 && !isDryRun)` and add a comment line above it: `// Phase 2 (§2.2): no duplicate cleanup (Graph or DB) in a dry run.`

(e) In the create batch branch `if (!isDryRun && pendingCreates.Count > 0)`, add as its first statement:

```csharp
            var targetFolderId = folderId
                ?? throw new InvalidOperationException($"No contact folder id for mailbox {mailbox.Id} outside a dry run");
```

and change the `CreateContactsBatchAsync(\n                mailbox.EntraId, folderId, batchOps, ct);` call to use `targetFolderId`.

(f) In the `else if (isDryRun)` branch for creates, delete the `statesToAdd.Add(new ContactSyncState { … });` statement (keep the run item and `created++`), and change the comment to `// Dry-run: report creates without Graph calls and without state rows (§2.2).`

(g) In the `else if (isDryRun)` branch for updates, delete the `statesToUpdate.Add((pending.stateId, pending.dataHash, pending.previousHash, "updated"));` statement (keep the run item and `updated++`).

(h) Change the state-save guard `if (statesToAdd.Count > 0 || statesToUpdate.Count > 0 || statesToHeal.Count > 0)` to `if (!isDryRun && (statesToAdd.Count > 0 || statesToUpdate.Count > 0 || statesToHeal.Count > 0))`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 253, Skipped: 1` (245 − 3 old folder tests + 6 folder tests + 2 writer + 3 engine).

- [ ] **Step 7: Commit**

```bash
git add worker/Services/IContactFolderManager.cs worker/Services/ContactFolderManager.cs worker/Services/PhotoSyncService.cs worker/Services/ContactWriter.cs worker/Services/SyncEngine.cs tests/AFHSync.Tests.Unit/Sync/ContactFolderManagerTests.cs tests/AFHSync.Tests.Unit/Sync/PhotoSyncServiceTests.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs tests/AFHSync.Tests.Unit/Sync/ContactWriterTests.cs
git commit -m "fix(worker): dry runs write nothing (no folder create, no state rows, no cleanup); id-less batch creates are failures

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 9: Per-chunk bookkeeping (§2.6a)

**Files:**
- Modify: `worker/Services/IContactWriter.cs`
- Modify: `worker/Services/ContactWriter.cs`
- Modify: `worker/Services/SyncEngine.cs` (`ProcessMailboxAsync` Phase 2 write section + new helper)
- Test: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` (`FakeContactWriter` chunking, 1 test)
- Test: `tests/AFHSync.Tests.Unit/Sync/StaleContactHandlerTests.cs` (`FakeContactWriter` signature only)

**Interfaces:**
- Produces:
  ```csharp
  // worker/Services/IContactWriter.cs — the two batch write methods gain a callback before ct
  Task<Dictionary<string, BatchOperationResult>> CreateContactsBatchAsync(
      string mailboxEntraId, string folderId,
      List<(string key, SortedDictionary<string, string> payload)> operations,
      Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,
      CancellationToken ct);
  Task<Dictionary<string, BatchOperationResult>> UpdateContactsBatchAsync(
      string mailboxEntraId,
      List<(string key, string graphContactId, SortedDictionary<string, string> payload)> operations,
      Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,
      CancellationToken ct);
  // onChunkCompleted is awaited after EACH 20-op chunk with that chunk's results only.
  // DeleteContactsBatchAsync is unchanged.
  ```
  `SyncEngine` persists each chunk's state rows inside the callback with `CancellationToken.None` (new helper `PersistStateChangesAsync`); the end-of-mailbox write handles heals only. Fakes: `FakeContactWriter` in **SyncEngineTests** gains `int ChunkSize = 20`, `int? ThrowOnChunkIndex` and invokes the callback per chunk; `FakeContactWriter` in **StaleContactHandlerTests** only gains the parameter (invokes it once). These two files are the only `IContactWriter` implementations in the tests.
- Consumes: `ContactWriter.NoContactIdError` and the `string? folderId` from Task 8.

- [ ] **Step 1: Write the failing test and update the fakes**

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`:

(a) In `FakeContactWriter`, add the properties:

```csharp
        /// <summary>Operations per chunk handed to onChunkCompleted (real writer: 20).</summary>
        public int ChunkSize { get; init; } = 20;

        /// <summary>When set, the batch throws when it reaches this chunk index (0-based) — simulates a mid-mailbox crash.</summary>
        public int? ThrowOnChunkIndex { get; init; }
```

and replace `CreateContactsBatchAsync` and `UpdateContactsBatchAsync` with:

```csharp
        public async Task<Dictionary<string, BatchOperationResult>> CreateContactsBatchAsync(
            string mailboxEntraId, string folderId,
            List<(string key, SortedDictionary<string, string> payload)> operations,
            Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,
            CancellationToken ct)
        {
            var results = new Dictionary<string, BatchOperationResult>();
            var chunkIndex = 0;
            foreach (var chunk in operations.Chunk(ChunkSize))
            {
                if (ThrowOnChunkIndex == chunkIndex)
                    throw new InvalidOperationException("simulated batch failure");

                var chunkResults = new Dictionary<string, BatchOperationResult>();
                foreach (var (key, _) in chunk)
                {
                    if (CreateReturnsNoId)
                    {
                        chunkResults[key] = new BatchOperationResult(false, Error: ContactWriter.NoContactIdError);
                        continue;
                    }
                    var id = Guid.NewGuid().ToString();
                    CreatedContactIds.Add(id);
                    chunkResults[key] = new BatchOperationResult(true, id);
                }
                foreach (var kv in chunkResults) results[kv.Key] = kv.Value;
                if (onChunkCompleted is not null) await onChunkCompleted(chunkResults);
                chunkIndex++;
            }
            return results;
        }

        public async Task<Dictionary<string, BatchOperationResult>> UpdateContactsBatchAsync(
            string mailboxEntraId,
            List<(string key, string graphContactId, SortedDictionary<string, string> payload)> operations,
            Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,
            CancellationToken ct)
        {
            var results = new Dictionary<string, BatchOperationResult>();
            foreach (var chunk in operations.Chunk(ChunkSize))
            {
                var chunkResults = new Dictionary<string, BatchOperationResult>();
                foreach (var (key, graphContactId, _) in chunk)
                {
                    UpdatedContactIds.Add(graphContactId);
                    chunkResults[key] = UpdateReturnsNotFound
                        ? new BatchOperationResult(false, Error: "HTTP 404", NotFound: true)
                        : new BatchOperationResult(true);
                }
                foreach (var kv in chunkResults) results[kv.Key] = kv.Value;
                if (onChunkCompleted is not null) await onChunkCompleted(chunkResults);
            }
            return results;
        }
```

(b) Add the test after `RunAsync_CreateWithoutContactId_IsFailedAndWritesNoStateRow`:

```csharp
    // ==============================
    // Phase 2 (2.6a): state is persisted per chunk — a crash loses at most the chunk in flight
    // ==============================

    [Fact]
    public async Task RunAsync_SecondChunkThrows_FirstChunkStateIsPersisted()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
        var contactWriter = new FakeContactWriter { ChunkSize = 2, ThrowOnChunkIndex = 1 };
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([
                new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" },
                new SourceUser { Id = 2, EntraId = "u2", DisplayName = "Bob" },
                new SourceUser { Id = 3, EntraId = "u3", DisplayName = "Carol" }]),
            contactWriter: contactWriter, runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        await using var verifyCtx = MakeDbContext(dbName);
        var states = await verifyCtx.ContactSyncStates.OrderBy(s => s.SourceUserId).ToListAsync();
        Assert.Equal(new[] { 1, 2 }, states.Select(s => s.SourceUserId).ToArray());   // chunk 1 persisted
        Assert.All(states, s => Assert.False(string.IsNullOrEmpty(s.GraphContactId)));
        Assert.Equal(2, runLogger.AddedItems.Count(i => i.Action == "created"));
        // The crash is contained as ONE mailbox-level failure (existing behaviour), not three.
        var failedItem = Assert.Single(runLogger.AddedItems, i => i.Action == "failed");
        Assert.Contains("simulated batch failure", failedItem.ErrorMessage);
        Assert.Equal(1, runLogger.FinalizedFailed);
    }
```

In `tests/AFHSync.Tests.Unit/Sync/StaleContactHandlerTests.cs`, in its `FakeContactWriter`, replace `CreateContactsBatchAsync` and `UpdateContactsBatchAsync` with:

```csharp
        public async Task<Dictionary<string, BatchOperationResult>> CreateContactsBatchAsync(
            string mailboxEntraId, string folderId,
            List<(string key, SortedDictionary<string, string> payload)> operations,
            Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,
            CancellationToken ct)
        {
            var results = new Dictionary<string, BatchOperationResult>();
            foreach (var (key, _) in operations)
            {
                var id = Guid.NewGuid().ToString();
                CreatedContactIds.Add(id);
                results[key] = new BatchOperationResult(true, id);
            }
            if (onChunkCompleted is not null) await onChunkCompleted(results);
            return results;
        }

        public async Task<Dictionary<string, BatchOperationResult>> UpdateContactsBatchAsync(
            string mailboxEntraId,
            List<(string key, string graphContactId, SortedDictionary<string, string> payload)> operations,
            Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,
            CancellationToken ct)
        {
            var results = new Dictionary<string, BatchOperationResult>();
            foreach (var (key, graphContactId, _) in operations)
            {
                UpdatedContactIds.Add(graphContactId);
                results[key] = new BatchOperationResult(true);
            }
            if (onChunkCompleted is not null) await onChunkCompleted(results);
            return results;
        }
```

- [ ] **Step 2: Run to verify it fails to compile**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | grep -E "error CS" | head -3`
Expected: `error CS0535: 'FakeContactWriter' does not implement interface member 'IContactWriter.CreateContactsBatchAsync(string, string, List<…>, CancellationToken)'`.

- [ ] **Step 3: Add the callback to `IContactWriter` and `ContactWriter`**

In `worker/Services/IContactWriter.cs`, change the two signatures (and their doc comments) to:

```csharp
    /// <summary>
    /// Creates multiple contacts in a single mailbox using Graph JSON batching ($batch).
    /// Bundles up to 20 requests per HTTP call for ~10-15x fewer round trips.
    /// </summary>
    /// <param name="mailboxEntraId">Entra ID of the target mailbox.</param>
    /// <param name="folderId">Graph contact folder ID within the mailbox.</param>
    /// <param name="operations">List of (correlationKey, payload) tuples.</param>
    /// <param name="onChunkCompleted">
    /// Phase 2 (§2.6a): awaited after EACH 20-op chunk with that chunk's results only, so the
    /// caller can persist bookkeeping before the next chunk is sent. Null to skip.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary mapping correlationKey to result (success + graphContactId or error).</returns>
    Task<Dictionary<string, BatchOperationResult>> CreateContactsBatchAsync(
        string mailboxEntraId,
        string folderId,
        List<(string key, SortedDictionary<string, string> payload)> operations,
        Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,
        CancellationToken ct);

    /// <summary>
    /// Updates multiple contacts in a single mailbox using Graph JSON batching ($batch).
    /// </summary>
    /// <param name="mailboxEntraId">Entra ID of the target mailbox.</param>
    /// <param name="operations">List of (correlationKey, graphContactId, payload) tuples.</param>
    /// <param name="onChunkCompleted">Awaited after each 20-op chunk with that chunk's results (see CreateContactsBatchAsync).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary mapping correlationKey to result (success or error).</returns>
    Task<Dictionary<string, BatchOperationResult>> UpdateContactsBatchAsync(
        string mailboxEntraId,
        List<(string key, string graphContactId, SortedDictionary<string, string> payload)> operations,
        Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,
        CancellationToken ct);
```

In `worker/Services/ContactWriter.cs`:

(a) Add the parameter `Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,` before `CancellationToken ct` in both `CreateContactsBatchAsync` and `UpdateContactsBatchAsync`.

(b) In both methods, directly after the `await ExecuteBatchWithRetryAsync(…)` call inside the `foreach (var chunk in ChunkOperations(operations, MaxBatchSize))` loop, add:

```csharp
            await NotifyChunkCompletedAsync(onChunkCompleted, stepIdToKey, results);
```

(c) Add this helper before `private static IEnumerable<List<T>> ChunkOperations<T>(…)`:

```csharp
    /// <summary>
    /// Phase 2 (§2.6a): hands the just-completed chunk's results (and only those) to the caller
    /// so state rows can be persisted before the next chunk goes out.
    /// </summary>
    private static async Task NotifyChunkCompletedAsync(
        Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,
        Dictionary<string, string> stepIdToKey,
        Dictionary<string, BatchOperationResult> results)
    {
        if (onChunkCompleted is null) return;

        var chunkResults = new Dictionary<string, BatchOperationResult>();
        foreach (var key in stepIdToKey.Values)
        {
            if (results.TryGetValue(key, out var result))
                chunkResults[key] = result;
        }
        await onChunkCompleted(chunkResults);
    }
```

- [ ] **Step 4: Persist per chunk in `SyncEngine.ProcessMailboxAsync`**

In `worker/Services/SyncEngine.cs`, `ProcessMailboxAsync`, replace everything from the comment `// Phase 2: Execute Graph writes using batching (up to 20 per HTTP call).` down to the closing brace of the state-save block (the `if (!isDryRun && (statesToAdd.Count > 0 || statesToUpdate.Count > 0 || statesToHeal.Count > 0)) { … await writeDb.SaveChangesAsync(ct); }` block from Task 8) with:

```csharp
        // Phase 2: Execute Graph writes using batching (up to 20 per HTTP call).
        // Sync-state IDs whose contact 404'd on update (deleted on the device). We drop the dead
        // state row at the end of the mailbox so the next run recreates the contact.
        var statesToHeal = new List<int>();

        if (!isDryRun && pendingCreates.Count > 0)
        {
            var targetFolderId = folderId
                ?? throw new InvalidOperationException($"No contact folder id for mailbox {mailbox.Id} outside a dry run");
            var pendingByKey = pendingCreates.ToDictionary(c => c.key);
            var handledKeys = new HashSet<string>();
            var batchOps = pendingCreates
                .Select(c => (c.key, c.payload))
                .ToList();

            // Phase 2 (§2.6a): persist each 20-op chunk as soon as Graph confirms it, with
            // CancellationToken.None, so a crash or shutdown loses at most the chunk in flight
            // (its Graph contacts are caught by the duplicate cleanup on the next run).
            async Task OnCreateChunkCompleted(IReadOnlyDictionary<string, BatchOperationResult> chunkResults)
            {
                var chunkStates = new List<ContactSyncState>();
                foreach (var (key, result) in chunkResults)
                {
                    if (!pendingByKey.TryGetValue(key, out var pending) || !handledKeys.Add(key))
                        continue;

                    if (result.Success && !string.IsNullOrEmpty(result.GraphContactId))
                    {
                        chunkStates.Add(new ContactSyncState
                        {
                            SourceUserId = pending.sourceUserId,
                            PhoneListId = canonicalPhoneList.Id,
                            TargetMailboxId = mailbox.Id,
                            TunnelId = tunnel.Id,
                            GraphContactId = result.GraphContactId,
                            DataHash = pending.dataHash,
                            LastSyncedAt = DateTime.UtcNow,
                            LastResult = "created",
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        });

                        runLogger.AddItem(new SyncRunItem
                        {
                            SyncRunId = run.Id,
                            TunnelId = tunnel.Id,
                            PhoneListId = canonicalPhoneList.Id,
                            TargetMailboxId = mailbox.Id,
                            SourceUserId = pending.sourceUserId,
                            Action = "created",
                            CreatedAt = DateTime.UtcNow
                        });
                        created++;
                    }
                    else
                    {
                        var error = result.Error ?? "No batch result returned";
                        logger.LogError("Batch create failed for SourceUserId={SourceUserId} in mailbox {MailboxId}: {Error}",
                            pending.sourceUserId, mailbox.Id, error);

                        runLogger.AddItem(new SyncRunItem
                        {
                            SyncRunId = run.Id,
                            TunnelId = tunnel.Id,
                            PhoneListId = canonicalPhoneList.Id,
                            TargetMailboxId = mailbox.Id,
                            SourceUserId = pending.sourceUserId,
                            Action = "failed",
                            ErrorMessage = error,
                            CreatedAt = DateTime.UtcNow
                        });
                        failed++;
                    }
                }

                if (chunkStates.Count > 0)
                    await PersistStateChangesAsync(chunkStates, [], mailbox.Id);
            }

            await contactWriter.CreateContactsBatchAsync(
                mailbox.EntraId, targetFolderId, batchOps, OnCreateChunkCompleted, ct);

            foreach (var pending in pendingCreates.Where(p => !handledKeys.Contains(p.key)))
            {
                logger.LogError("Batch create returned no result for SourceUserId={SourceUserId} in mailbox {MailboxId}",
                    pending.sourceUserId, mailbox.Id);
                runLogger.AddItem(new SyncRunItem
                {
                    SyncRunId = run.Id,
                    TunnelId = tunnel.Id,
                    PhoneListId = canonicalPhoneList.Id,
                    TargetMailboxId = mailbox.Id,
                    SourceUserId = pending.sourceUserId,
                    Action = "failed",
                    ErrorMessage = "No batch result returned",
                    CreatedAt = DateTime.UtcNow
                });
                failed++;
            }
        }
        else if (isDryRun)
        {
            // Dry-run: report creates without Graph calls and without state rows (§2.2).
            foreach (var pending in pendingCreates)
            {
                runLogger.AddItem(new SyncRunItem
                {
                    SyncRunId = run.Id,
                    TunnelId = tunnel.Id,
                    PhoneListId = canonicalPhoneList.Id,
                    TargetMailboxId = mailbox.Id,
                    SourceUserId = pending.sourceUserId,
                    Action = "created",
                    CreatedAt = DateTime.UtcNow
                });
                created++;
            }
        }

        if (!isDryRun && pendingUpdates.Count > 0)
        {
            var pendingByKey = pendingUpdates.ToDictionary(u => u.key);
            var handledKeys = new HashSet<string>();
            var batchOps = pendingUpdates
                .Select(u => (u.key, u.graphContactId, u.payload))
                .ToList();

            async Task OnUpdateChunkCompleted(IReadOnlyDictionary<string, BatchOperationResult> chunkResults)
            {
                var chunkUpdates = new List<(int StateId, string DataHash, string? PreviousHash, string LastResult)>();
                foreach (var (key, result) in chunkResults)
                {
                    if (!pendingByKey.TryGetValue(key, out var pending) || !handledKeys.Add(key))
                        continue;

                    if (result.Success)
                    {
                        var fieldChangesJson = BuildFieldChangesJson(pending.payload, pending.previousHash);
                        chunkUpdates.Add((pending.stateId, pending.dataHash, pending.previousHash, "updated"));

                        runLogger.AddItem(new SyncRunItem
                        {
                            SyncRunId = run.Id,
                            TunnelId = tunnel.Id,
                            PhoneListId = canonicalPhoneList.Id,
                            TargetMailboxId = mailbox.Id,
                            SourceUserId = pending.sourceUserId,
                            Action = "updated",
                            FieldChanges = fieldChangesJson,
                            CreatedAt = DateTime.UtcNow
                        });
                        updated++;
                    }
                    else if (result.NotFound)
                    {
                        // The contact was deleted on the device. Drop the dead sync-state so it
                        // recreates next run, rather than 404'ing on every future update.
                        logger.LogInformation(
                            "Contact gone (404) for SourceUserId={SourceUserId} in mailbox {MailboxId}; clearing state to recreate next run",
                            pending.sourceUserId, mailbox.Id);
                        statesToHeal.Add(pending.stateId);

                        runLogger.AddItem(new SyncRunItem
                        {
                            SyncRunId = run.Id,
                            TunnelId = tunnel.Id,
                            PhoneListId = canonicalPhoneList.Id,
                            TargetMailboxId = mailbox.Id,
                            SourceUserId = pending.sourceUserId,
                            Action = "removed",
                            CreatedAt = DateTime.UtcNow
                        });
                        removed++;
                    }
                    else
                    {
                        var error = result.Error ?? "No batch result returned";
                        logger.LogError("Batch update failed for SourceUserId={SourceUserId} in mailbox {MailboxId}: {Error}",
                            pending.sourceUserId, mailbox.Id, error);

                        runLogger.AddItem(new SyncRunItem
                        {
                            SyncRunId = run.Id,
                            TunnelId = tunnel.Id,
                            PhoneListId = canonicalPhoneList.Id,
                            TargetMailboxId = mailbox.Id,
                            SourceUserId = pending.sourceUserId,
                            Action = "failed",
                            ErrorMessage = error,
                            CreatedAt = DateTime.UtcNow
                        });
                        failed++;
                    }
                }

                if (chunkUpdates.Count > 0)
                    await PersistStateChangesAsync([], chunkUpdates, mailbox.Id);
            }

            await contactWriter.UpdateContactsBatchAsync(
                mailbox.EntraId, batchOps, OnUpdateChunkCompleted, ct);

            foreach (var pending in pendingUpdates.Where(p => !handledKeys.Contains(p.key)))
            {
                logger.LogError("Batch update returned no result for SourceUserId={SourceUserId} in mailbox {MailboxId}",
                    pending.sourceUserId, mailbox.Id);
                runLogger.AddItem(new SyncRunItem
                {
                    SyncRunId = run.Id,
                    TunnelId = tunnel.Id,
                    PhoneListId = canonicalPhoneList.Id,
                    TargetMailboxId = mailbox.Id,
                    SourceUserId = pending.sourceUserId,
                    Action = "failed",
                    ErrorMessage = "No batch result returned",
                    CreatedAt = DateTime.UtcNow
                });
                failed++;
            }
        }
        else if (isDryRun)
        {
            // Dry-run: report updates without Graph calls and without state rows (§2.2).
            foreach (var pending in pendingUpdates)
            {
                var fieldChangesJson = BuildFieldChangesJson(pending.payload, pending.previousHash);

                runLogger.AddItem(new SyncRunItem
                {
                    SyncRunId = run.Id,
                    TunnelId = tunnel.Id,
                    PhoneListId = canonicalPhoneList.Id,
                    TargetMailboxId = mailbox.Id,
                    SourceUserId = pending.sourceUserId,
                    Action = "updated",
                    FieldChanges = fieldChangesJson,
                    CreatedAt = DateTime.UtcNow
                });
                updated++;
            }
        }

        // Note: live progress for the dashboard is updated in the per-tunnel loop
        // (ProcessTunnelAsync caller) which has access to the overall totals.

        // Heals only: a contact that 404'd on update — drop the dead state so the next run
        // recreates it. Creates and hash updates were persisted per chunk above (§2.6a).
        if (!isDryRun && statesToHeal.Count > 0)
        {
            await using var healDb = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            var healStates = await healDb.ContactSyncStates
                .Where(s => statesToHeal.Contains(s.Id))
                .ToListAsync(CancellationToken.None);
            healDb.ContactSyncStates.RemoveRange(healStates);
            await healDb.SaveChangesAsync(CancellationToken.None);
        }
```

Then add the helper directly after `ProcessMailboxAsync` (before `LoadDefaultFieldProfileAsync`):

```csharp
    /// <summary>
    /// Phase 2 (§2.6a): writes one chunk's new state rows and hash updates with a fresh context
    /// and CancellationToken.None, so a shutdown mid-mailbox cannot lose confirmed Graph writes.
    /// </summary>
    private async Task PersistStateChangesAsync(
        List<ContactSyncState> statesToAdd,
        List<(int StateId, string DataHash, string? PreviousHash, string LastResult)> statesToUpdate,
        int mailboxId)
    {
        if (statesToAdd.Count == 0 && statesToUpdate.Count == 0)
            return;

        await using var writeDb = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);

        if (statesToAdd.Count > 0)
            writeDb.ContactSyncStates.AddRange(statesToAdd);

        if (statesToUpdate.Count > 0)
        {
            var updateIds = statesToUpdate.Select(u => u.StateId).ToList();
            var trackedStates = await writeDb.ContactSyncStates
                .Where(s => updateIds.Contains(s.Id))
                .ToListAsync(CancellationToken.None);

            var updateDict = statesToUpdate.ToDictionary(u => u.StateId);
            foreach (var s in trackedStates)
            {
                if (updateDict.TryGetValue(s.Id, out var update))
                {
                    s.PreviousDataHash = update.PreviousHash;
                    s.DataHash = update.DataHash;
                    s.LastResult = update.LastResult;
                    s.LastSyncedAt = DateTime.UtcNow;
                    s.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        await writeDb.SaveChangesAsync(CancellationToken.None);
        logger.LogDebug("Persisted {Added} new and {Updated} updated state row(s) for mailbox {MailboxId}",
            statesToAdd.Count, statesToUpdate.Count, mailboxId);
    }
```

The old `statesToAdd` / `statesToUpdate` locals and the `if (batchResults.TryGetValue(pending.key, out var result) && result.Success)` loops are gone — search the method for `statesToAdd` and `batchResults` and confirm zero hits remain.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 254, Skipped: 1`. (`RunAsync_AggregateCountsAreCorrect` and `RunAsync_UpdateReturns404_…` now exercise the update callback path.)

- [ ] **Step 6: Commit**

```bash
git add worker/Services/IContactWriter.cs worker/Services/ContactWriter.cs worker/Services/SyncEngine.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs tests/AFHSync.Tests.Unit/Sync/StaleContactHandlerTests.cs
git commit -m "feat(worker): persist contact_sync_state per 20-op batch chunk via onChunkCompleted; heals stay end-of-mailbox

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 10: Folder identity (§2.5 — remembered Graph folder id, 404 fallthrough, rename)

**Files:**
- Modify: `worker/Services/ContactFolderManager.cs` (constructor, resolution order, DB upsert, two new seams)
- Test: `tests/AFHSync.Tests.Unit/Sync/ContactFolderManagerTests.cs` (rewrite of the fake + 5 new tests)

**Interfaces:**
- Produces:
  ```csharp
  // worker/Services/ContactFolderManager.cs — constructor gains the DbContext factory (DI resolves it; no Program.cs change)
  public ContactFolderManager(GraphClientFactory graphClientFactory, IDbContextFactory<AFHSyncDbContext> dbContextFactory, ILogger<ContactFolderManager> logger)
  protected virtual Task<GraphFolderInfo?> GetFolderByIdAsync(string mailboxEntraId, string folderId, CancellationToken ct);   // null on 404
  protected virtual Task RenameFolderAsync(string mailboxEntraId, string folderId, string newName, CancellationToken ct);      // PATCH displayName
  ```
  `GetOrCreateFolderAsync(Tunnel, TargetMailbox, bool isDryRun, CancellationToken)` keeps Task 8's signature. Resolution order: run-scoped cache → `tunnel_mailbox_folders` row → `GET /contactFolders/{id}` (404 ⇒ fall through) → search by name → create (skipped in dry run) → upsert the row with the id and `tunnel.Name` → if the row's `FolderName != tunnel.Name`, `PATCH displayName` and update the row. `wasCreated` is true only for the create step. Dry runs never create, never rename, never write the row.
- Consumes: `TunnelMailboxFolder` + `AFHSyncDbContext.TunnelMailboxFolders` (Task 1); `GraphFolderInfo`, `FindFolderByNameAsync`, `CreateFolderAsync` (Task 8). `SyncEngineTests`/`PhotoSyncServiceTests` fakes implement the interface and need no change.

- [ ] **Step 1: Rewrite the folder-manager tests**

Replace the whole of `tests/AFHSync.Tests.Unit/Sync/ContactFolderManagerTests.cs` with:

```csharp
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Tests for ContactFolderManager — folder identity by remembered Graph id (Phase 2 §2.5) with a
/// per-run cache. A subclass intercepts the four Graph seams so no real Graph call is made.
/// </summary>
public class ContactFolderManagerTests
{
    private static AFHSyncDbContext MakeDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AFHSyncDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AFHSyncDbContext(options);
    }

    private sealed class TestDbContextFactory(string dbName) : IDbContextFactory<AFHSyncDbContext>
    {
        public AFHSyncDbContext CreateDbContext() => MakeDbContext(dbName);
    }

    private static Tunnel T(int id, string name) => new() { Id = id, Name = name };
    private static TargetMailbox M(int id, string entraId) => new() { Id = id, EntraId = entraId, Email = $"{entraId}@test.com" };

    private static async Task SeedKnownFolderAsync(string dbName, int tunnelId, int mailboxId, string graphFolderId, string folderName)
    {
        using var ctx = MakeDbContext(dbName);
        ctx.TunnelMailboxFolders.Add(new TunnelMailboxFolder
        {
            TunnelId = tunnelId, TargetMailboxId = mailboxId, GraphFolderId = graphFolderId, FolderName = folderName, UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>In-memory Graph: folderId -> (mailboxEntraId, displayName).</summary>
    private sealed class FakeContactFolderManager : ContactFolderManager
    {
        public int LookupByIdCount { get; private set; }
        public int LookupByNameCount { get; private set; }
        public int CreateCount { get; private set; }
        public int RenameCount { get; private set; }
        public int GraphCallCount => LookupByIdCount + LookupByNameCount + CreateCount + RenameCount;

        public Dictionary<string, (string mailbox, string name)> Folders { get; }

        public FakeContactFolderManager(string dbName, Dictionary<string, (string mailbox, string name)>? folders = null)
            : base(null!, new TestDbContextFactory(dbName), NullLogger<ContactFolderManager>.Instance)
        {
            Folders = folders ?? new Dictionary<string, (string mailbox, string name)>();
        }

        protected override Task<GraphFolderInfo?> GetFolderByIdAsync(string mailboxEntraId, string folderId, CancellationToken ct)
        {
            LookupByIdCount++;
            return Task.FromResult(Folders.TryGetValue(folderId, out var f) && f.mailbox == mailboxEntraId
                ? new GraphFolderInfo(folderId, f.name)
                : null);
        }

        protected override Task<GraphFolderInfo?> FindFolderByNameAsync(string mailboxEntraId, string folderName, CancellationToken ct)
        {
            LookupByNameCount++;
            var hit = Folders.FirstOrDefault(kv => kv.Value.mailbox == mailboxEntraId
                && string.Equals(kv.Value.name, folderName, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(hit.Key is null ? null : new GraphFolderInfo(hit.Key, hit.Value.name));
        }

        protected override Task<string> CreateFolderAsync(string mailboxEntraId, string folderName, CancellationToken ct)
        {
            CreateCount++;
            var id = $"folder-{mailboxEntraId}-{CreateCount}";
            Folders[id] = (mailboxEntraId, folderName);
            return Task.FromResult(id);
        }

        protected override Task RenameFolderAsync(string mailboxEntraId, string folderId, string newName, CancellationToken ct)
        {
            RenameCount++;
            Folders[folderId] = (mailboxEntraId, newName);
            return Task.CompletedTask;
        }
    }

    // ── cache behaviour (unchanged from before Phase 2) ───────────────────────

    [Fact]
    public async Task GetOrCreateFolderAsync_ReturnsCachedId_OnSecondCall_ForSameMailbox()
    {
        var dbName = Guid.NewGuid().ToString();
        var fake = new FakeContactFolderManager(dbName);
        var tunnel = T(1, "AFH Contacts");
        var mailbox = M(1, "mailbox-1");

        var (id1, created1) = await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);
        var calls = fake.GraphCallCount;
        var (id2, created2) = await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);

        Assert.Equal(id1, id2);
        Assert.True(created1);
        Assert.False(created2);
        Assert.Equal(calls, fake.GraphCallCount);   // second call hit the cache
    }

    [Fact]
    public async Task ResetCache_ClearsAllCachedEntries_ForcingNewGraphCalls()
    {
        var dbName = Guid.NewGuid().ToString();
        var fake = new FakeContactFolderManager(dbName);
        var tunnel = T(1, "AFH Contacts");
        var mailbox = M(1, "mailbox-x");

        await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);
        var calls = fake.GraphCallCount;

        fake.ResetCache();
        var (_, wasCreated) = await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);

        Assert.False(wasCreated);
        Assert.True(fake.GraphCallCount > calls);
    }

    // ── Phase 2 (2.5): identity by remembered id ─────────────────────────────

    [Fact]
    public async Task RememberedId_Found_IsUsedWithoutNameLookup()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedKnownFolderAsync(dbName, tunnelId: 1, mailboxId: 1, graphFolderId: "f-1", folderName: "Buckhead");
        var fake = new FakeContactFolderManager(dbName, new() { ["f-1"] = ("mailbox-1", "Buckhead") });

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "Buckhead"), M(1, "mailbox-1"), false, CancellationToken.None);

        Assert.Equal("f-1", id);
        Assert.False(wasCreated);
        Assert.Equal(1, fake.LookupByIdCount);
        Assert.Equal(0, fake.LookupByNameCount);
        Assert.Equal(0, fake.RenameCount);
    }

    [Fact]
    public async Task RememberedId_Gone404_FallsThroughToName_AndUpdatesRow()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedKnownFolderAsync(dbName, 1, 1, graphFolderId: "f-gone", folderName: "Buckhead");
        var fake = new FakeContactFolderManager(dbName, new() { ["f-2"] = ("mailbox-1", "Buckhead") });

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "Buckhead"), M(1, "mailbox-1"), false, CancellationToken.None);

        Assert.Equal("f-2", id);
        Assert.False(wasCreated);
        Assert.Equal(1, fake.LookupByIdCount);
        Assert.Equal(1, fake.LookupByNameCount);
        using var ctx = MakeDbContext(dbName);
        var row = await ctx.TunnelMailboxFolders.SingleAsync();
        Assert.Equal("f-2", row.GraphFolderId);
        Assert.Equal("Buckhead", row.FolderName);
    }

    [Fact]
    public async Task TunnelRenamed_PatchesDisplayName_AndUpdatesRow()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedKnownFolderAsync(dbName, 1, 1, graphFolderId: "f-1", folderName: "Old Name");
        var fake = new FakeContactFolderManager(dbName, new() { ["f-1"] = ("mailbox-1", "Old Name") });

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "New Name"), M(1, "mailbox-1"), false, CancellationToken.None);

        Assert.Equal("f-1", id);
        Assert.False(wasCreated);                                  // a rename is not a create ⇒ no state wipe
        Assert.Equal(1, fake.RenameCount);
        Assert.Equal("New Name", fake.Folders["f-1"].name);
        Assert.Equal(0, fake.CreateCount);
        using var ctx = MakeDbContext(dbName);
        Assert.Equal("New Name", (await ctx.TunnelMailboxFolders.SingleAsync()).FolderName);
    }

    [Fact]
    public async Task WasCreated_TrueOnlyWhenCreated_AndRowIsUpserted()
    {
        var dbName = Guid.NewGuid().ToString();
        var fake = new FakeContactFolderManager(dbName, new() { ["f-existing"] = ("mailbox-a", "AFH Contacts") });

        var (idA, createdA) = await fake.GetOrCreateFolderAsync(T(1, "AFH Contacts"), M(1, "mailbox-a"), false, CancellationToken.None);
        var (idB, createdB) = await fake.GetOrCreateFolderAsync(T(1, "AFH Contacts"), M(2, "mailbox-b"), false, CancellationToken.None);

        Assert.Equal("f-existing", idA);
        Assert.False(createdA);                                    // found by name, no create
        Assert.True(createdB);                                     // created
        Assert.Equal(1, fake.CreateCount);
        using var ctx = MakeDbContext(dbName);
        var rows = await ctx.TunnelMailboxFolders.OrderBy(r => r.TargetMailboxId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("f-existing", rows[0].GraphFolderId);
        Assert.Equal(idB, rows[1].GraphFolderId);
        Assert.All(rows, r => Assert.Equal("AFH Contacts", r.FolderName));
    }

    [Fact]
    public async Task DryRun_NeverCreatesRenamesOrWritesTheRow()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedKnownFolderAsync(dbName, 1, 1, graphFolderId: "f-1", folderName: "Old Name");
        var fake = new FakeContactFolderManager(dbName, new() { ["f-1"] = ("mailbox-1", "Old Name") });

        // Known folder, tunnel renamed: a real run would PATCH; a dry run must not.
        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "New Name"), M(1, "mailbox-1"), true, CancellationToken.None);
        // Unknown mailbox: a real run would create; a dry run returns null.
        var (idMissing, createdMissing) = await fake.GetOrCreateFolderAsync(T(1, "New Name"), M(2, "mailbox-2"), true, CancellationToken.None);

        Assert.Equal("f-1", id);
        Assert.False(wasCreated);
        Assert.Null(idMissing);
        Assert.False(createdMissing);
        Assert.Equal(0, fake.RenameCount);
        Assert.Equal(0, fake.CreateCount);
        using var ctx = MakeDbContext(dbName);
        var row = await ctx.TunnelMailboxFolders.SingleAsync();
        Assert.Equal("Old Name", row.FolderName);                  // row untouched
        Assert.Equal(1, await ctx.TunnelMailboxFolders.CountAsync());
    }
}
```

- [ ] **Step 2: Run to verify they fail to compile**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | grep -E "error CS" | head -3`
Expected: `error CS1729: 'ContactFolderManager' does not contain a constructor that takes 3 arguments` and `no suitable method found to override` for `GetFolderByIdAsync`/`RenameFolderAsync`.

- [ ] **Step 3: Implement identity resolution in `ContactFolderManager`**

Replace the whole of `worker/Services/ContactFolderManager.cs` with:

```csharp
using System.Collections.Concurrent;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Worker.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace AFHSync.Worker.Services;

/// <summary>A Graph contact folder as seen by the folder manager's Graph seams.</summary>
public sealed record GraphFolderInfo(string Id, string? DisplayName);

/// <summary>
/// Resolves the contact folder for a (tunnel, mailbox) pair and caches the id for the duration
/// of a sync run.
///
/// Phase 2 (§2.5) resolution order: run cache → remembered id in tunnel_mailbox_folders
/// (GET by id; 404 falls through) → search by name → create (never in a dry run) → upsert the
/// row → if the remembered name differs from tunnel.Name, PATCH displayName. This makes a
/// tunnel rename a rename on every phone instead of a brand-new folder (and a state wipe).
///
/// Thread-safe: multiple parallel mailbox tasks (bounded by semaphore in SyncEngine)
/// may call <see cref="GetOrCreateFolderAsync"/> concurrently. A per-key lock ensures
/// only one Graph round-trip is made per (mailbox, tunnel) even under concurrent access.
///
/// Lifecycle: one instance per sync run scope (registered as Scoped in DI). The SyncEngine
/// calls <see cref="ResetCache"/> at the start of each run so stale folder IDs from
/// previous runs don't persist.
///
/// Graph SDK calls are <c>protected virtual</c> seams so unit tests can subclass this class.
/// </summary>
public class ContactFolderManager : IContactFolderManager
{
    private readonly GraphClientFactory? _graphClientFactory;
    private readonly IDbContextFactory<AFHSyncDbContext> _dbContextFactory;
    private readonly ILogger<ContactFolderManager> _logger;

    // ConcurrentDictionary: key = "mailboxEntraId:tunnelId", value = folderId.
    private readonly ConcurrentDictionary<string, string> _folderCache = new();

    // Per-key locks to prevent concurrent Graph calls for the same folder.
    // Without this, two parallel tasks could both miss the cache and both POST
    // a folder create to Graph, resulting in duplicate folders.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();

    public ContactFolderManager(
        GraphClientFactory graphClientFactory,
        IDbContextFactory<AFHSyncDbContext> dbContextFactory,
        ILogger<ContactFolderManager> logger)
    {
        _graphClientFactory = graphClientFactory;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(string? folderId, bool wasCreated)> GetOrCreateFolderAsync(
        Tunnel tunnel,
        TargetMailbox mailbox,
        bool isDryRun,
        CancellationToken ct)
    {
        var cacheKey = $"{mailbox.EntraId}:{tunnel.Id}";

        // Fast path: return cached folder ID without Graph call
        if (_folderCache.TryGetValue(cacheKey, out var cachedId))
            return (cachedId, false);

        // Slow path: acquire per-key lock so only one Graph round-trip fires per folder
        var keyLock = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock — another task may have populated the cache
            if (_folderCache.TryGetValue(cacheKey, out cachedId))
                return (cachedId, false);

            string? folderId = null;
            var wasCreated = false;
            var foundById = false;

            // (1) Remembered id → GET by id; 404 falls through.
            var known = await LoadKnownFolderAsync(tunnel.Id, mailbox.Id, ct);
            if (known is not null)
            {
                var byId = await GetFolderByIdAsync(mailbox.EntraId, known.GraphFolderId, ct);
                if (byId is not null)
                {
                    folderId = byId.Id;
                    foundById = true;
                }
                else
                {
                    _logger.LogInformation(
                        "Remembered folder {FolderId} for tunnel {TunnelId} in mailbox {MailboxId} is gone — falling back to name lookup",
                        known.GraphFolderId, tunnel.Id, mailbox.EntraId);
                }
            }

            // (2) Search by name.
            if (folderId is null)
            {
                var byName = await FindFolderByNameAsync(mailbox.EntraId, tunnel.Name, ct);
                if (byName is not null)
                {
                    _logger.LogDebug(
                        "Found existing contact folder '{FolderName}' ({FolderId}) in mailbox {MailboxId}",
                        tunnel.Name, byName.Id, mailbox.EntraId);
                    folderId = byName.Id;
                }
            }

            // (3) Create — never in a dry run (§2.2).
            if (folderId is null)
            {
                if (isDryRun)
                {
                    _logger.LogInformation(
                        "Dry run: contact folder '{FolderName}' does not exist in mailbox {MailboxId} — would create",
                        tunnel.Name, mailbox.EntraId);
                    return (null, false);
                }

                _logger.LogInformation(
                    "Creating contact folder '{FolderName}' in mailbox {MailboxId}",
                    tunnel.Name, mailbox.EntraId);
                folderId = await CreateFolderAsync(mailbox.EntraId, tunnel.Name, ct);
                wasCreated = true;
            }

            if (!isDryRun)
            {
                // (5) Rename when the remembered name differs from the tunnel's current name.
                if (foundById && known is not null && !string.Equals(known.FolderName, tunnel.Name, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Renaming contact folder {FolderId} in mailbox {MailboxId} from '{OldName}' to '{NewName}'",
                        folderId, mailbox.EntraId, known.FolderName, tunnel.Name);
                    await RenameFolderAsync(mailbox.EntraId, folderId, tunnel.Name, ct);
                }

                // (4) Remember id + current name. CancellationToken.None: bookkeeping must survive a cancel.
                await UpsertKnownFolderAsync(tunnel.Id, mailbox.Id, folderId, tunnel.Name);
            }

            _folderCache.TryAdd(cacheKey, folderId);

            _logger.LogDebug(
                "Contact folder '{FolderName}' resolved to {FolderId} for mailbox {MailboxId} (created={Created})",
                tunnel.Name, folderId, mailbox.EntraId, wasCreated);

            return (folderId, wasCreated);
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <inheritdoc />
    public void ResetCache()
    {
        _folderCache.Clear();
        _keyLocks.Clear();
        _logger.LogDebug("Contact folder cache cleared for new sync run");
    }

    // ==============================
    // tunnel_mailbox_folders bookkeeping
    // ==============================

    private async Task<TunnelMailboxFolder?> LoadKnownFolderAsync(int tunnelId, int mailboxId, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await db.TunnelMailboxFolders
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.TunnelId == tunnelId && f.TargetMailboxId == mailboxId, ct);
    }

    private async Task UpsertKnownFolderAsync(int tunnelId, int mailboxId, string graphFolderId, string folderName)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        var row = await db.TunnelMailboxFolders
            .FirstOrDefaultAsync(f => f.TunnelId == tunnelId && f.TargetMailboxId == mailboxId, CancellationToken.None);
        var now = DateTime.UtcNow;

        if (row is null)
        {
            db.TunnelMailboxFolders.Add(new TunnelMailboxFolder
            {
                TunnelId = tunnelId,
                TargetMailboxId = mailboxId,
                GraphFolderId = graphFolderId,
                FolderName = folderName,
                UpdatedAt = now
            });
        }
        else if (row.GraphFolderId != graphFolderId || row.FolderName != folderName)
        {
            row.GraphFolderId = graphFolderId;
            row.FolderName = folderName;
            row.UpdatedAt = now;
        }
        else
        {
            return;
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private Microsoft.Graph.GraphServiceClient Client =>
        _graphClientFactory?.Client
        ?? throw new InvalidOperationException("GraphClientFactory is required for Graph operations");

    // ==============================
    // Protected virtual Graph seams (overridden in unit tests)
    // ==============================

    /// <summary>GET /users/{mailbox}/contactFolders/{id}; null when Graph answers 404.</summary>
    protected virtual async Task<GraphFolderInfo?> GetFolderByIdAsync(
        string mailboxEntraId, string folderId, CancellationToken ct)
    {
        try
        {
            var folder = await Client
                .Users[mailboxEntraId]
                .ContactFolders[folderId]
                .GetAsync(config => config.QueryParameters.Select = ["id", "displayName"], cancellationToken: ct);

            return folder?.Id is null ? null : new GraphFolderInfo(folder.Id, folder.DisplayName);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>Queries Graph for a contact folder whose displayName equals <paramref name="folderName"/>.</summary>
    protected virtual async Task<GraphFolderInfo?> FindFolderByNameAsync(
        string mailboxEntraId, string folderName, CancellationToken ct)
    {
        var foldersResponse = await Client
            .Users[mailboxEntraId]
            .ContactFolders
            .GetAsync(config =>
            {
                var escapedName = folderName.Replace("'", "''");
                config.QueryParameters.Filter = $"displayName eq '{escapedName}'";
                config.QueryParameters.Top = 1;
            }, cancellationToken: ct);

        var existingFolder = foldersResponse?.Value?.FirstOrDefault();
        return existingFolder?.Id is null ? null : new GraphFolderInfo(existingFolder.Id, existingFolder.DisplayName);
    }

    /// <summary>Creates a contact folder and returns its id.</summary>
    protected virtual async Task<string> CreateFolderAsync(
        string mailboxEntraId, string folderName, CancellationToken ct)
    {
        var created = await Client
            .Users[mailboxEntraId]
            .ContactFolders
            .PostAsync(new ContactFolder { DisplayName = folderName }, cancellationToken: ct);

        if (created?.Id is null)
            throw new InvalidOperationException(
                $"Graph returned null folder ID after POST for mailbox {mailboxEntraId}");

        return created.Id;
    }

    /// <summary>PATCH /users/{mailbox}/contactFolders/{id} { displayName }.</summary>
    protected virtual async Task RenameFolderAsync(
        string mailboxEntraId, string folderId, string newName, CancellationToken ct)
    {
        await Client
            .Users[mailboxEntraId]
            .ContactFolders[folderId]
            .PatchAsync(new ContactFolder { DisplayName = newName }, cancellationToken: ct);
    }
}
```

`worker/Program.cs` needs no change: `AddScoped<IContactFolderManager, ContactFolderManager>()` resolves the new `IDbContextFactory<AFHSyncDbContext>` parameter from the existing `AddDbContextFactory` registration.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 255, Skipped: 1` (254 − 6 Task 8 folder tests + 7 here).

Run: `dotnet build worker --nologo -v quiet 2>&1 | grep -E "error|Build succeeded" | head -3`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add worker/Services/ContactFolderManager.cs tests/AFHSync.Tests.Unit/Sync/ContactFolderManagerTests.cs
git commit -m "feat(worker): contact folders resolved by remembered Graph id; tunnel rename renames the folder instead of recreating it

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 11: Notes prefix only when notes are in the payload or on create (§2.8)

**Files:**
- Modify: `worker/Services/ContactWriter.cs` (`MapPayloadToContact` + its four call sites)
- Test: `tests/AFHSync.Tests.Unit/Sync/ContactWriterTests.cs`

**Interfaces:**
- Produces: `public static Contact MapPayloadToContact(SortedDictionary<string, string> payload, bool isCreate)`. `PersonalNotes` is set only when `"PersonalNotes"` is a key in the payload **or** `isCreate` is true; the `Office: {OfficeLocation}` prefix logic is unchanged inside that condition. Call sites: `CreateContactAsync` and `CreateContactsBatchAsync` pass `true`; `UpdateContactAsync` and `UpdateContactsBatchAsync` pass `false`.
- Consumes: nothing new. (`ContactPayloadBuilder` already omits the `PersonalNotes` key for existing contacts under `AddMissing` — that omission is what now preserves phone-side edits.)

- [ ] **Step 1: Update the existing tests and add the new ones**

In `tests/AFHSync.Tests.Unit/Sync/ContactWriterTests.cs`, change every existing call `ContactWriter.MapPayloadToContact(payload)` (six of them) to `ContactWriter.MapPayloadToContact(payload, isCreate: true)`. Then append:

```csharp
    // ── Phase 2 (2.8): notes are written only when in the payload or on create ──

    [Fact]
    public void MapPayloadToContact_Update_WithoutNotesKey_LeavesPersonalNotesNull_EvenWithOffice()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Jane Doe",
            ["OfficeLocation"] = "Buckhead"
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: false);

        Assert.Equal("Buckhead", contact.OfficeLocation);
        Assert.Null(contact.PersonalNotes);   // phone-side notes survive an AddMissing update
    }

    [Fact]
    public void MapPayloadToContact_Create_WithOfficeAndNoNotes_SetsOfficePrefix()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Jane Doe",
            ["OfficeLocation"] = "Buckhead"
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: true);

        Assert.Equal("Office: Buckhead", contact.PersonalNotes);
    }

    [Fact]
    public void MapPayloadToContact_Update_WithNotesKey_PrefixesOffice()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Jane Doe",
            ["OfficeLocation"] = "Buckhead",
            ["PersonalNotes"] = "Team lead"
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: false);

        Assert.Equal("Office: Buckhead\nTeam lead", contact.PersonalNotes);
    }

    [Fact]
    public void MapPayloadToContact_Update_WithEmptyNotesKey_ClearsToPrefixOnly()
    {
        // Nosync on an existing contact sends an explicit empty string to clear the field.
        var payload = new SortedDictionary<string, string>
        {
            ["OfficeLocation"] = "Buckhead",
            ["PersonalNotes"] = ""
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: false);

        Assert.Equal("Office: Buckhead", contact.PersonalNotes);
    }
```

- [ ] **Step 2: Run to verify they fail to compile**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~ContactWriterTests" 2>&1 | grep -E "error CS" | head -2`
Expected: `error CS1501: No overload for method 'MapPayloadToContact' takes 2 arguments`.

- [ ] **Step 3: Implement**

In `worker/Services/ContactWriter.cs`:

(a) Change the signature and doc comment:

```csharp
    /// <summary>
    /// Converts a normalized payload dictionary (from <see cref="IContactPayloadBuilder"/>)
    /// into a <see cref="Contact"/> object ready for Graph API submission.
    ///
    /// Phase 2 (§2.8): PersonalNotes is written only when the payload carries a "PersonalNotes"
    /// key or <paramref name="isCreate"/> is true. On updates where the field profile omits
    /// notes (AddMissing), phone-side edits survive even when OfficeLocation is synced.
    ///
    /// Made <c>public static</c> so unit tests can validate field mapping directly
    /// without needing to mock Graph SDK or DI infrastructure.
    /// </summary>
    public static Contact MapPayloadToContact(SortedDictionary<string, string> payload, bool isCreate)
```

(b) Replace the notes block

```csharp
        // Build PersonalNotes: prepend OfficeLocation since iOS has no dedicated field for it
        payload.TryGetValue("PersonalNotes", out var personalNotes);
        if (!string.IsNullOrWhiteSpace(officeLocation))
        {
            var prefix = $"Office: {officeLocation}";
            contact.PersonalNotes = string.IsNullOrWhiteSpace(personalNotes)
                ? prefix
                : $"{prefix}\n{personalNotes}";
        }
        else if (personalNotes != null)
        {
            contact.PersonalNotes = personalNotes;
        }
```

with

```csharp
        // Build PersonalNotes: prepend OfficeLocation since iOS has no dedicated field for it.
        // Phase 2 (§2.8): only when notes are part of this payload, or on create — a PATCH that
        // omits PersonalNotes leaves whatever the user typed on the phone alone.
        var hasNotesKey = payload.TryGetValue("PersonalNotes", out var personalNotes);
        if (hasNotesKey || isCreate)
        {
            if (!string.IsNullOrWhiteSpace(officeLocation))
            {
                var prefix = $"Office: {officeLocation}";
                contact.PersonalNotes = string.IsNullOrWhiteSpace(personalNotes)
                    ? prefix
                    : $"{prefix}\n{personalNotes}";
            }
            else if (personalNotes != null)
            {
                contact.PersonalNotes = personalNotes;
            }
        }
```

(c) Update the four call sites: in `CreateContactAsync` → `MapPayloadToContact(payload, isCreate: true)`; in `UpdateContactAsync` → `MapPayloadToContact(payload, isCreate: false)`; in `CreateContactsBatchAsync` (inside the chunk loop) → `MapPayloadToContact(payload, isCreate: true)`; in `UpdateContactsBatchAsync` → `MapPayloadToContact(payload, isCreate: false)`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 259, Skipped: 1`.

- [ ] **Step 5: Commit**

```bash
git add worker/Services/ContactWriter.cs tests/AFHSync.Tests.Unit/Sync/ContactWriterTests.cs
git commit -m "fix(worker): write PersonalNotes only when in the payload or on create so phone-side notes survive updates

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 12: Tunnel rename is a high-impact change on the edit page (§2.5 UI)

**Files:**
- Modify: `frontend/src/components/ImpactPreviewDialog.tsx`
- Modify: `frontend/src/app/(app)/tunnels/[id]/page.tsx`

**Interfaces:**
- Produces: `ImpactPreviewDialog` gains an optional prop `notes?: string[]` rendered under the counts. `isHighImpactChange(original, edited)` returns `true` when `original.name.trim() !== edited.name.trim()`. The exact copy: `The contact folder will be renamed on every phone at the next sync.`
- Consumes: nothing from the backend (the rename itself is Task 10).

- [ ] **Step 1: Extend the dialog**

In `frontend/src/components/ImpactPreviewDialog.tsx`:

(a) Change the lucide import to `import { Plus, RefreshCw, Minus, AlertCircle } from 'lucide-react';`

(b) Add `notes?: string[];` to `ImpactPreviewDialogProps` (after `isLoading?: boolean;`) and `notes,` to the destructured props (after `isLoading = false,`).

(c) Directly after the `{impact && ( <div className="bg-warm-white rounded-lg p-4 space-y-3"> … </div> )}` block and before `<DialogFooter>`, add:

```tsx
        {notes && notes.length > 0 && (
          <ul className="space-y-2">
            {notes.map((note) => (
              <li key={note} className="flex items-start gap-2 text-sm text-amber-800">
                <AlertCircle className="size-4 mt-0.5 shrink-0" strokeWidth={1.5} />
                <span>{note}</span>
              </li>
            ))}
          </ul>
        )}
```

- [ ] **Step 2: Flag the rename on the edit page**

In `frontend/src/app/(app)/tunnels/[id]/page.tsx`:

(a) In `isHighImpactChange`, insert as the first statement of the function body:

```ts
  // Phase 2 (§2.5): renaming the tunnel renames the contact folder on every phone.
  if (original.name.trim() !== edited.name.trim()) return true;
```

(b) Add a module-level constant directly above `function isHighImpactChange(`:

```ts
const FOLDER_RENAME_NOTE =
  'The contact folder will be renamed on every phone at the next sync.';
```

(c) Inside `TunnelDetailPage`, after the `if (!tunnel) { return ( <div className="text-center py-16"> … ); }` early return and before the component's final `return (`, add:

```ts
  const folderRenameNote =
    isEditing && editForm.name.trim() !== tunnel.name.trim() ? FOLDER_RENAME_NOTE : null;
```

(d) In the JSX, change the `<ImpactPreviewDialog … />` element to:

```tsx
      <ImpactPreviewDialog
        open={impactDialogOpen}
        onOpenChange={setImpactDialogOpen}
        impact={impactData}
        onConfirm={doSave}
        isLoading={updateTunnel.isPending}
        notes={folderRenameNote ? [folderRenameNote] : undefined}
      />
```

(e) In the fallback `<ConfirmDialog open={fallbackConfirmOpen} …>`, change the `description` prop to:

```tsx
        description={
          folderRenameNote ? (
            <>
              Unable to estimate impact. {folderRenameNote} Save changes anyway?
            </>
          ) : (
            'Unable to estimate impact. Save changes anyway?'
          )
        }
```

- [ ] **Step 3: Frontend gate**

Run: `cd frontend && npm run build 2>&1 | tail -8; cd ..`
Expected: `✓ Compiled successfully`, no type or lint errors.

Manual check (when the stack is running): edit a tunnel, change only its name, click Save — the Review Changes dialog opens with `0/0/0` counts and the amber note "The contact folder will be renamed on every phone at the next sync."; changing nothing but the stale policy still saves directly.

- [ ] **Step 4: Commit**

```bash
git add frontend/src/components/ImpactPreviewDialog.tsx "frontend/src/app/(app)/tunnels/[id]/page.tsx"
git commit -m "feat(frontend): tunnel rename is high-impact — warns that the contact folder is renamed on every phone

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 13: Full verification and PR

**Files:** none new.

- [ ] **Step 1: Full backend test run**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 259, Skipped: 1` (baseline 221 + 38 new).

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 37, Skipped: 1` without Postgres; `Passed: 38, Skipped: 0` with `AFHSYNC_TEST_PG` pointing at a server (do this at least once before opening the PR — e.g. `docker compose up -d postgres` locally, then `AFHSYNC_TEST_PG="Host=localhost;Port=5432;Username=afhsync;Password=$(grep POSTGRES_PASSWORD .env | cut -d= -f2);Database=postgres" dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet --filter "FullyQualifiedName~MigrationTests"`).

- [ ] **Step 2: Frontend build + existing vitest**

Run: `cd frontend && npm run build 2>&1 | tail -3 && npm test 2>&1 | tail -5; cd ..`
Expected: build `✓ Compiled successfully`; vitest passes (`sync-error-classifier.test.ts`).

- [ ] **Step 3: Confirm the whole phase is one migration and the tree is clean**

Run: `ls api/Migrations | grep -c "_Phase2" ; git status --short ; git log --oneline main..HEAD | cat`
Expected: `2` (the `.cs` and `.Designer.cs` of `Phase2DataIntegrity`, nothing else new under `api/Migrations` besides the snapshot edit); empty status; one commit per task (13 commits including the spec commit already on the branch).

- [ ] **Step 4: Push and open the PR**

```bash
git push -u origin sync-reliability/phase-2
gh pr create --base main --title "Sync reliability Phase 2: data integrity" --body "$(cat <<'PRBODY'
## Why
The 2026-08-26 code map showed the sync's bookkeeping lying in several ways: 259 enabled Entra
accounts without a REST mailbox failed every run (the IsActive self-heal was undone by the next
refresh), dry runs created Graph folders and wrote id-less state rows a real run then treated as
synced, a failed source triggered a stale pass against an incomplete set, folders were found by
name only (a tunnel rename = new folder + state wipe on every phone), state was persisted once per
mailbox, a manual multi-tunnel trigger raced N Hangfire jobs for one Pending row, and nothing
reconciled a run left Running by a dead worker.
Spec: docs/superpowers/specs/2026-08-25-sync-reliability-design.md (Phase 2).

## What
- One migration (`Phase2DataIntegrity`): `target_mailboxes.mailbox_unavailable_at/last_probed_at/unavailable_reason`,
  new `tunnel_mailbox_folders` (unique per tunnel+mailbox, cascade), `sync_runs.requested_tunnel_ids`,
  and `DELETE FROM contact_sync_state WHERE graph_contact_id IS NULL`
- §2.7 Runs are claimed by id: `POST /api/sync-runs` creates the row (with requested tunnel ids) and
  enqueues ONE job; `RunClaimService` owns the advisory-lock guard for contact and photo runs;
  `[AutomaticRetry(Attempts = 0)]`; startup `RunReconciler` fails interrupted runs; Pending > 10 min fails
- §2.6b Hangfire's shutdown token is honoured at tunnel and mailbox boundaries ⇒ `Cancelled — worker
  shutting down`; worker `stop_grace_period: 60s` (+ host/Hangfire shutdown timeouts)
- §2.1 Unavailable mailboxes (`MailboxNotEnabledForRESTAPI` / "inactive, soft-deleted, or is hosted
  on-premise") are stamped, skipped for 7 days, re-probed weekly, cleared on success; `GET /api/targets/unavailable`
  and an "Unavailable mailboxes (N of M)" section on the Targets page; the IsActive self-heal is gone
- §2.3/§2.4 `SourceResolution` reports failed sources ⇒ run item + tunnelErrors + no stale pass;
  returning users are un-flagged
- §2.2 Dry runs write nothing (no folder create/rename, no state rows, no duplicate cleanup, no stale pass);
  a 2xx batch step without an id is `Success=false, "no contact id in response"`
- §2.6a `onChunkCompleted` on the batch writers; state persisted per 20-op chunk with `CancellationToken.None`
- §2.5 Folder identity: remembered Graph id → 404 fallthrough → name → create → upsert → rename PATCH;
  tunnel rename is high-impact in the UI ("The contact folder will be renamed on every phone at the next sync.")
- §2.8 `PersonalNotes` written only when in the payload or on create

## Tests
- Unit: +38 (claim by id / finalized no-op / blocked, reconcile, Pending cleanup, cancellation, unavailable
  classifier + 5 engine behaviours, source failure, stale reset, dry-run invariants, no-id, chunk persistence,
  folder identity, notes)
- Integration: +3 (`requested_tunnel_ids` + single job id, `/api/targets/unavailable`, migration operations);
  the Postgres-backed `MigrateAsync` schema test is `[PostgresFact]` (skips without a server; run with `AFHSYNC_TEST_PG`)
- Frontend: `npm run build` (no component harness)

## Deploy (spec §2.10)
1. Before: `docker exec afh-postgres psql -U afhsync -d afhsync -c "SELECT COUNT(*) FROM contact_sync_state WHERE graph_contact_id IS NULL;"`
   — this is the number the migration deletes.
2. With no run in progress, `./deploy.sh` (no manual `git pull` first — the script diffs its own pull).
   Note: the worker and api must both be rebuilt (shared/ and api/ changed); `deploy.sh` selects them from the diff.
   Any job still queued from before the deploy carries the old `RunAsync(tunnelId, …)` arguments; its
   first argument is now read as a run id, which resolves to an already-finalized row ⇒ no-op.
3. After: Targets page lists ~259 unavailable mailboxes; a manual run ends **Success** (not Warning) when
   nothing else is wrong; the worker log shows `N target mailbox(es) excluded (unavailable)` per tunnel;
   a run started during a deploy ends `Cancelled — worker shutting down`, never orphaned; a tunnel rename
   is followed by renamed folders (not new ones) on the next sync.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
PRBODY
)"
```

- [ ] **Step 5: After merge — deploy and verify on the box (Nick)**

```bash
# on the server, inside tmux
docker exec afh-postgres psql -U afhsync -d afhsync -c "SELECT COUNT(*) FROM contact_sync_state WHERE graph_contact_id IS NULL;"
./deploy.sh
docker logs afh-worker --since 5m 2>&1 | grep -E "Startup reconcile|Registered recurring sync job"
# then, logged in to the app:
#   Targets page → "Unavailable mailboxes (N of M)" lists ~259 rows, oldest first
#   Dashboard → Run Sync → run ends Success; Runs & Logs shows no "Folder '…': … inactive, soft-deleted" items
#   docker logs afh-worker | grep "excluded (unavailable)"   → one line per tunnel
#   (optional) trigger a run, then `docker compose restart worker` → the run shows Cancelled — worker shutting down
```

---

## Self-review

### 1. Spec coverage (Phase 2 → task)

| Spec bullet | Task |
|---|---|
| §2.0 three `target_mailboxes` columns; `IsActive` keeps its meaning; self-heal removed | 1 (columns), 5 (self-heal removed) |
| §2.0 `tunnel_mailbox_folders` table, unique `(tunnel_id, target_mailbox_id)`, cascade | 1 |
| §2.0 `sync_runs.requested_tunnel_ids`; `hangfire_job_ids` holds one id | 1 (column), 2 (one id) |
| §2.0 `DELETE FROM contact_sync_state WHERE graph_contact_id IS NULL` in the migration; deploy counts first | 1, 13 |
| §2.0 applied at API startup; worker assumes schema | unchanged (`api/Program.cs` MigrateAsync) — noted in Task 1 |
| §2.1 classify `MailboxNotEnabledForRESTAPI` / message substring; stamp; Information log; no run item; not a failure | 5 |
| §2.1 exclusion within 7 days, weekly re-probe forever, clear on success, `N excluded (unavailable)` log line | 5 |
| §2.1 UI: Targets page section, `GET /api/targets/unavailable`, "N of M" header, oldest first | 6 |
| §2.2 dry run: folder lookup only; no state insert/update/delete; no duplicate cleanup; no stale pass; items still emitted; dry-run branches stop populating states; final save + both `ExecuteDeleteAsync` guarded | 8 (folder wipe + duplicate delete + final save + branches), 9 (final save block becomes heals-only, still `!isDryRun`) |
| §2.2 no-id / unparseable batch step ⇒ `Success=false, "no contact id in response"`, no state row | 8 |
| §2.3 `SourceResolution`/`SourceFailure`; per-source catch records and continues | 7 |
| §2.3 engine: Error log, `tunnelErrors`, run item `Source '{name}': {reason}`, tunnel warned, `skipStale` for every mailbox; resolved sources still written; zero users still short-circuits | 7 |
| §2.4 stale reset in the same transaction | 7 |
| §2.5 row → GET by id → 404 fallthrough → name → create (not in dry run) → upsert → rename PATCH; `wasCreated` only on create; run cache first | 10 (dry-run lookup-only introduced in 8) |
| §2.5 UI: rename is high-impact with the folder-rename copy | 12 |
| §2.6 `onChunkCompleted` on both batch methods; per-chunk persist with `CancellationToken.None`; end-of-mailbox save for heals only | 9 |
| §2.6 Hangfire shutdown token; tunnel + mailbox boundary checks; `Cancelled` "worker shutting down" via `CancellationToken.None`; `stop_grace_period: 60s`; `cancel_sync` still serves Stop Sync | 4 |
| §2.7 POST creates row (`Pending`, `RunType`, `IsDryRun`, `RequestedTunnelIds`), enqueues one job by run id, enqueue failure ⇒ `Failed`, fan-out removed | 2 |
| §2.7 `ISyncEngine.RunAsync(int? runId, …)`; claim under the lock; finalized ⇒ no work; null ⇒ create; row is the source of truth; `[AutomaticRetry(Attempts = 0)]` on the interface method | 2 |
| §2.7 startup reconcile before the Hangfire server; `cancel_sync` cleared; nothing restarted | 3 |
| §2.7 `StaleRunCleanupService` fails Pending > 10 min | 3 |
| §2.7 `StopSync` unchanged | 2 (not edited; the stored single id still gets `Delete`d) |
| §2.7 photo sync claims through the same locked path, retry off, startup reconcile covers it, one lane across run types | 3 |
| §2.8 `MapPayloadToContact(payload, isCreate)` | 11 |
| §2.9 unit tests list (2.1 ×6, 2.7 ×5, 2.3, 2.4, 2.2 ×2, 2.6, 2.8, 2.5 ×4); fakes updated | 5/6, 2/3/4, 7, 7, 8, 9, 11, 10 |
| §2.9 real `MigrationTests` on Postgres asserting columns, table, unique index, `requested_tunnel_ids` | 1 |
| §2.9 gates `dotnet test` (unit + integration) and `npm run build` | every task; 13 |
| §2.10 deploy verification incl. the pre-deploy count | 13 (`N excluded` is a worker log line per §2.1 — Phase 3's per-tunnel run records are what would put it in run detail) |

### 2. Placeholder scan

No "TBD", "TODO", "similar to Task N", or "add error handling" steps. Every code step carries the code; every run step carries the command and the expected output. Expected test counts are derived from the baseline (221/34) and the number of tests each task adds/removes — if an executor's count differs by the tests they actually added, the invariant is `Failed: 0`.

### 3. Type consistency

- `IRunClaimService.ClaimAsync(int? runId, RunType, bool, CancellationToken) → RunClaimResult(Outcome, Run)`: defined in Task 2, consumed by `SyncEngine` (Task 2) and `PhotoSyncService` (Task 3); `RunClaimService(IDbContextFactory<AFHSyncDbContext>, ILogger<RunClaimService>)` is what both test files construct.
- `IContactFolderManager.GetOrCreateFolderAsync(Tunnel, TargetMailbox, bool isDryRun, CancellationToken) → (string? folderId, bool wasCreated)`: defined in Task 8, unchanged in Task 10; `SyncEngine`/`PhotoSyncService` call sites (Task 8) and the three test fakes match. `ContactFolderManager`'s constructor is `(GraphClientFactory, ILogger)` in Task 8 and `(GraphClientFactory, IDbContextFactory<AFHSyncDbContext>, ILogger)` from Task 10 — the Task 10 test rewrite is the only subclass and passes three arguments.
- `IContactWriter.CreateContactsBatchAsync(…, Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted, CancellationToken)` / `UpdateContactsBatchAsync(…)`: defined in Task 9; both test fakes and both `SyncEngine` call sites pass the callback positionally before `ct`.
- `ISourceResolver.ResolveAsync → SourceResolution(Users, FailedSources)`, `SourceFailure(SourceId, DisplayName, Reason)`: Task 7; `FakeSourceResolver`/`CancellingSourceResolver` updated there.
- `ContactWriter.MapPayloadToContact(payload, bool isCreate)`: Task 11; `NoContactIdError` / `MapCreateResponse(Contact?)`: Task 8, used by the Task 8 and Task 9 fakes.
- `FakeRunLogger.FinalizedStatus` (Task 2) is asserted in Tasks 4, 5, 7; `FakeContactFolderManager.Failures/Requested` (Task 5) and `MissingFolderMailboxes/CreateCount` (Task 8) are used by Tasks 5, 7, 8; `SeedTunnelWithMailboxesAsync` (Task 5) is used by Tasks 5, 7, 8, 9.
- `SyncEngine.ProcessMailboxAsync(tunnel, canonicalPhoneList, allPhoneListIds, mailbox, run, sourceUsers, fieldSettings, isDryRun, skipStale, ct)`: `skipStale` added in Task 7; Tasks 8 and 9 edit the body only.
