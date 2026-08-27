# Sync Reliability — Phase 3 Implementation Plan (API/UI correctness)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make what the API and UI report about a sync true: per-tunnel run records replace the item-count guesswork, pagination stops lying about "next page", the tunnel edit page validates target scope the way the wizard does, the Graph pickers page instead of truncating, Contact Filters resolve the configured folder, the Exchange PowerShell session and the target-mailbox refresh have sane lifetimes, and Graph contacts orphaned by a lost batch are reconciled instead of duplicated.

**Architecture:** One EF Core migration adds `sync_run_tunnels` (one row per tunnel per run, written by `SyncEngine`) and `tunnel_mailbox_folders.reconcile_pending_at`. The API reads per-tunnel records for run detail and the tunnels list, wraps every paged endpoint in `{ items, hasMore }` (`+ total` for phone-list contacts), validates target scope in `TargetScopeValidation`, and pages Graph through a small `PageWindow<T>` helper. The worker gains `FolderReconciler` (adopt/remove strays after an unknown-outcome batch or a crash) and a memoised target-mailbox refresh; `DDGResolver` becomes a Singleton that survives Exchange session loss. The frontend derives the scope `Select` from a `TargetScopeOption` enum and consumes the paged envelopes. The phase closes with a behaviour-preserving split of `ProcessMailboxAsync`.

**Tech Stack:** .NET 10 / ASP.NET Core (api), .NET 10 worker with Hangfire 1.8.23 (PostgreSQL storage), EF Core 10 + Npgsql (Postgres in prod, InMemory in tests), Microsoft.Graph 5.x (`PageIterator`), System.Management.Automation (Exchange Online PowerShell), xUnit 2.9; Next.js 15 / React 19 / TypeScript frontend with TanStack Query, vitest 4, shadcn-style `ui/` components.

**Spec:** `docs/superpowers/specs/2026-08-25-sync-reliability-design.md` — Phase 3 (§3.1–§3.7) as amended by Task 1 of this plan (§3.7 persisted reconcile flag, §3.8 refactor + shared advisory key, §3.9 tests, §3.10 deploy verification).

## Global Constraints

- Branch: `sync-reliability/phase-3` from local `main` at `d873bf7` (Phase 2 merged; **not yet pushed to origin** — Nick pushes `main` and deploys Phase 2 himself; Phase 3 must be deployed only after Phase 2 is live). PR target: `main` on `github.com/nickafh/sync`.
- Commit after every task. Use `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` as the last line of each commit message.
- Run all shell commands from the repo root `/Users/nick/Documents/Code/AFHsync` unless a step says otherwise.
- Backend gate: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet` and `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet`. Baseline (Task 0): unit `Passed: 270, Skipped: 1`; integration `Passed: 35, Skipped: 1` without Postgres, `Passed: 36, Skipped: 0` via `.superpowers/sdd/2026-08-26-sync-reliability-phase-2/run-integration.sh` (needs `docker compose up -d postgres`). Expected counts in later tasks are derived from these; if an executor's count differs by the tests they actually added, the invariant is `Failed: 0`.
- Frontend gate: `cd frontend && npm run build && npm test` (vitest baseline 8 passed).
- Exactly **one** migration for the whole phase: `Phase3RunTunnels` (Task 2). Later tasks must not add migrations; if a later task needs a schema change, stop and revisit Task 2.
- Dry runs still write nothing to Graph or `contact_sync_state` (Phase 2 §2.2). Per-tunnel run records ARE written in dry runs (they are run bookkeeping, like run items). The folder reconciler never runs in a dry run.
- Use `CancellationToken.None` for every bookkeeping write that must survive cancellation (per-tunnel record, reconcile flag set/clear, adopted state rows).
- Paged envelope shape (verbatim, camelCase on the wire): `{ "items": [...], "hasMore": true|false }`; phone-list contacts add `"total": N`. `hasMore` is computed server-side by fetching `pageSize + 1`.
- Copy rules (verbatim strings the API, UI and tests depend on): scope validation messages `Select at least one user, or switch scope to All Users.` / `Select a security group, or switch scope to All Users.` / `A tunnel can be scoped to specific users or to a security group, not both.` / `targetUserEmails must be a JSON array of email addresses.`; reconcile state `LastResult = "adopted"`; per-tunnel cancelled summary `worker shutting down` (reuses `SyncEngine.WorkerShutdownReason`); lists page counter `Showing {n} of {total}`.
- Keep `SyncEngine.cs` edits surgical: locate them by the `// Step N` / `// Phase N` comments and the quoted code, not by line number (lines drift as tasks land). Task 12 (the split) is the ONLY task that moves code.
- `PageWindow<T>` clamps: `page ≥ 1`; `pageSize` in `[1, max]` where `max` is per endpoint (`/sync-runs` 100, `/sync-runs/{id}/items` 200, `/phone-lists/{id}/contacts` 500, `/graph/ddgs/{id}/members` 999). Security groups are capped at 2000 rows total.

---

## File map

| File | Responsibility |
|---|---|
| `docs/superpowers/specs/2026-08-25-sync-reliability-design.md` | Phase 3 amendments (§3.7 flag, §3.8–§3.10) |
| `shared/Entities/SyncRunTunnel.cs` (new), `shared/Data/Configurations/SyncRunTunnelConfiguration.cs` (new) | per-tunnel run record, table `sync_run_tunnels` |
| `shared/Entities/TunnelMailboxFolder.cs`, `shared/Data/Configurations/TunnelMailboxFolderConfiguration.cs` | + `ReconcilePendingAt` / `reconcile_pending_at` |
| `shared/Data/AFHSyncDbContext.cs` | + `DbSet<SyncRunTunnel> SyncRunTunnels` |
| `shared/Entities/SyncRun.cs` | + `ICollection<SyncRunTunnel> TunnelRecords` navigation |
| `api/Migrations/<timestamp>_Phase3RunTunnels.cs` (+ Designer, snapshot) | the one migration |
| `shared/Services/ICleanupJobRunner.cs` | `[AutomaticRetry(Attempts = 0)]` on the interface method |
| `shared/Services/RunLocks.cs` (new) | the single advisory-lock key/SQL shared by API guard and worker claim |
| `worker/Services/SyncEngine.cs` | `TunnelOutcome`; per-tunnel records; memoised target refresh incl. group scope; reconcile flag + trigger; `RecordFailedItem`; `ProcessMailboxAsync` split |
| `worker/Services/IFolderReconciler.cs`, `FolderReconciler.cs` (new) | §3.7 adopt/remove strays by deterministic key |
| `worker/Services/IContactWriter.cs`, `ContactWriter.cs` | `BatchOperationResult.OutcomeUnknown` |
| `worker/Services/CleanupJobRunner.cs`, `RunClaimService.cs`, `worker/Program.cs` | attribute moved; shared lock key; DI (`IFolderReconciler` scoped, `IDDGResolver` singleton) |
| `api/Services/DDGResolver.cs` | runspace disposal on connect failure; session-error reset + one retry; `Disconnect-ExchangeOnline` on dispose |
| `api/Services/TargetScopeValidation.cs` (new) | §3.2 request validation |
| `api/Services/GraphQuery.cs` (new), `api/Services/PageWindow.cs` (new) | OData literal escape; page/pageSize windowing over a Graph iterator |
| `api/DTOs/PagedResult.cs` (new), `api/DTOs/TunnelRunSummaryDto.cs`, `api/DTOs/DdgMemberDto.cs` | envelope; `Status`/`TargetsCount` on tunnel summaries |
| `api/Controllers/SyncRunsController.cs` | `tunnelSummaries` from records (items fallback); paged `/sync-runs` and `/items`; shared lock SQL |
| `api/Controllers/TunnelsController.cs` | per-tunnel `LastSync` + `EstimatedTargetUsers` from the latest record; scope validation on Create/Update; `@odata.count` + folder-aware counts in `Preview` |
| `api/Controllers/PhoneListsController.cs` | paged `/contacts` with `total`, clamp `[1,500]` |
| `api/Controllers/GraphController.cs` | members paging; security groups paged to 2000; `'` escaped in user search |
| `api/Controllers/ContactExclusionsController.cs` | folder-aware, paged mailbox contacts; atomic de-duplicated replace |
| `api/Program.cs` | `IDDGResolver` singleton |
| `frontend/src/types/common.ts`, `sync-run.ts`, `phone-list.ts`, `ddg.ts` | `PagedResult<T>`; `status`/`targetsCount` on tunnel summaries |
| `frontend/src/lib/api.ts`, `hooks/use-sync-runs.ts`, `hooks/use-phone-lists.ts` | envelope-aware calls; exact `pageSize` |
| `frontend/src/app/(app)/runs/page.tsx`, `runs/[id]/page.tsx`, `lists/page.tsx` | consume envelopes; "Showing N of M" + paging on the Targets page; per-tunnel status/mailboxes in run detail |
| `frontend/src/lib/target-scope.ts` (new) + `target-scope.test.ts` (new) | scope enum derivation + validation shared by wizard and edit page |
| `frontend/src/components/wizard/StepTargets.tsx`, `TunnelWizard.tsx`, `app/(app)/tunnels/[id]/page.tsx` | use `target-scope.ts` |
| `tests/AFHSync.Tests.Integration/MigrationTests.cs`, `Api/SyncRunsControllerTests.cs`, `Api/TunnelsControllerTests.cs`, `Api/PhoneListsControllerTests.cs`, `Api/ContactExclusionsControllerTests.cs` (new), `DiLifetimeTests.cs` (new) | integration tests |
| `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`, `FolderReconcilerTests.cs` (new), `ContactWriterTests.cs`, `CleanupJobRunnerTests.cs`; `Api/DDGResolverTests.cs`, `Api/TargetScopeValidationTests.cs` (new), `Api/PageWindowTests.cs` (new), `Api/GraphQueryTests.cs` (new) | unit tests |

---

### Task 0: Baseline

**Files:** none

- [ ] **Step 1: Create the branch and confirm a clean tree**

Run: `git status --short && git branch --show-current && git log --oneline -1`
Expected: no output from status; branch `main`; HEAD `d873bf7 docs(spec): §2.6 crash window is bounded not closed; §2.10 excluded count is a log line; add Phase 3.7`.

Run: `git checkout -b sync-reliability/phase-3 && git branch --show-current`
Expected: `sync-reliability/phase-3`.

- [ ] **Step 2: Record the backend baseline**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 270, Skipped: 1, Total: 271`.

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 35, Skipped: 1, Total: 36` without Postgres. With Docker Desktop running: `docker compose up -d postgres` then `.superpowers/sdd/2026-08-26-sync-reliability-phase-2/run-integration.sh 2>&1 | tail -3` → `Passed: 36, Skipped: 0`. A `NU1903` warning about `Microsoft.Kiota.Abstractions` is pre-existing; ignore it.

- [ ] **Step 3: Record the frontend baseline**

Run: `cd frontend && (test -d node_modules || npm install) && npm run build 2>&1 | tail -3 && npm test 2>&1 | tail -4; cd ..`
Expected: `✓ Compiled successfully`; vitest `Tests  8 passed (8)`.

- [ ] **Step 4: Verify the EF tooling Task 2 relies on**

Run: `dotnet ef --version`
Expected: `Entity Framework Core .NET Command-line Tools` / `10.0.5`. If the command is not found: `dotnet tool install --global dotnet-ef --version 10.0.5`.

Run: `dotnet ef migrations list --project api --startup-project api --no-connect 2>&1 | tail -3`
Expected: the last listed migration is `20260826133931_Phase2DataIntegrity`, followed by the normal `Pending status not shown…` trailer.

---

### Task 1: Spec amendments (docs)

**Files:**
- Modify: `docs/superpowers/specs/2026-08-25-sync-reliability-design.md`

**Interfaces:** none (documentation). Later tasks argue from these sections.

- [ ] **Step 1: Amend §3.1, §3.7 and add §3.8–§3.10**

In the spec, replace the **3.1** bullet with:

```markdown
- **3.1 Per-tunnel run records.** Written by `SyncEngine` after every tunnel (including zero-activity and skipped-for-error tunnels; a tunnel interrupted by worker shutdown gets a `Cancelled` record with zero counts). Columns: `sync_run_id, tunnel_id (SET NULL on tunnel delete), tunnel_name (snapshot), status (sync_status), targets_count, contacts_created/updated/removed/skipped/failed, error_summary, started_at, completed_at`. `SyncRunsController` builds `tunnelSummaries` from these records when the run has any (photo counts and error strings still come from run items; runs without records — photo-sync runs and pre-Phase-3 history — fall back to grouping items as before). `TunnelsController` computes `LastSync` per tunnel from that tunnel's latest record and `EstimatedTargetUsers` from its `targets_count` (falling back to the distinct-mailbox count in `contact_sync_state` when a tunnel has no record yet).
```

Replace the **3.7** bullet with:

```markdown
- **3.7 Orphaned Graph contacts.** `FolderReconciler` lists the tunnel's folder in a mailbox, compares Graph contact ids with `contact_sync_state` for that (tunnel, mailbox), and for every stray computes a deterministic key (primary email lower-cased, else display name lower-cased): a stray whose key matches a current source user that has no state row is **adopted** (state row with the Graph id, `data_hash = NULL` so the next classification PATCHes it, `last_result = 'adopted'`); every other stray is **removed**. Triggers: (a) in-run, when any step of a create batch has an unknown outcome (`BatchOperationResult.OutcomeUnknown` — the `$batch` POST threw or returned no response); (b) at the start of the next run for a mailbox whose `tunnel_mailbox_folders.reconcile_pending_at` is set — the engine sets it before the first create chunk and clears it only after every chunk's bookkeeping is persisted, so a crash or shutdown between the two leaves the flag for the next run. Never in a dry run.
```

After the 3.7 bullet add:

```markdown
- **3.8 Hygiene from the Phase 2 reviews.** `SyncEngine.RecordFailedItem(run, tunnel, phoneListId, mailboxId, sourceUserId, message)` replaces the nine copies of the failed-item block; `ProcessMailboxAsync` is split into folder resolution, state loading + duplicate cleanup, classification, create execution, update execution, healing and stale handling, with a `MailboxCounters` object threaded through — behaviour-preserving, existing tests unchanged. The API's run-trigger guard and the worker's `RunClaimService` take the same Postgres advisory-lock key from `RunLocks` in `shared/` (they currently use keys 2 and 1 and therefore do not serialise against each other).
- **3.9 Tests.** Unit: per-tunnel records (success, warning with error summary, failed with exception message, zero-activity); target refresh memo (once per run, group scope included); reconcile flag set/cleared, in-run trigger, next-run trigger; `FolderReconciler` adopt/remove/key normalisation; `TargetScopeValidation`; `PageWindow`; `GraphQuery.EscapeLiteral`; `DDGResolver.IsSessionError`; `[AutomaticRetry]` on `ICleanupJobRunner.RunAsync`. Integration: migration schema (Postgres); paged envelopes for runs, run items and phone-list contacts incl. the `[1,500]` clamp and `total`; `tunnelSummaries` from records with the items fallback; tunnels list `lastSync`/`estimatedTargetUsers` from the latest record; 400s for empty `targetUserEmails` and empty-string `targetGroupId`; exclusion replace de-duplicates and is atomic; `IDDGResolver` resolves to one instance across scopes. Frontend: vitest for `target-scope.ts`; `npm run build`.
- **3.10 Deploy verification.** After `./deploy.sh`: run detail of the first post-deploy run shows a per-tunnel breakdown with `Status · N mailboxes` for every tunnel (including ones that resolved zero targets); the Tunnels page shows a per-tunnel Last Run that differs between tunnels processed in different runs; `/api/sync-runs?page=1&pageSize=2` returns `{items:[…2…],hasMore:true}`; Targets page shows `Showing N of M` under the phone preview; `/api/graph/ddgs` still resolves all 16 DDGs after the API has been idle > 1 h (session reset path); editing a tunnel to Security Group without picking one is refused with the wizard's message; `docker logs afh-worker` shows `Target mailbox refresh complete` once per run, not once per tunnel.
```

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-08-25-sync-reliability-design.md
git commit -m "docs(spec): Phase 3 amendments — run-record semantics, reconcile flag, hygiene, tests, deploy verification

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Migration, entities and DbContext (§3.1 table, §3.7 flag)

**Files:**
- Create: `shared/Entities/SyncRunTunnel.cs`
- Modify: `shared/Entities/SyncRun.cs`
- Modify: `shared/Entities/TunnelMailboxFolder.cs`
- Create: `shared/Data/Configurations/SyncRunTunnelConfiguration.cs`
- Modify: `shared/Data/Configurations/TunnelMailboxFolderConfiguration.cs`
- Modify: `shared/Data/AFHSyncDbContext.cs`
- Create (generated): `api/Migrations/<timestamp>_Phase3RunTunnels.cs`, `…Designer.cs`; regenerated `api/Migrations/AFHSyncDbContextModelSnapshot.cs`
- Modify: `tests/AFHSync.Tests.Integration/MigrationTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // shared/Entities/SyncRunTunnel.cs
  public class SyncRunTunnel
  {
      int Id; int SyncRunId; int? TunnelId; string TunnelName; SyncStatus Status; int TargetsCount;
      int ContactsCreated; int ContactsUpdated; int ContactsRemoved; int ContactsSkipped; int ContactsFailed;
      string? ErrorSummary; DateTime StartedAt; DateTime CompletedAt;
      SyncRun SyncRun; Tunnel? Tunnel;
  }
  // shared/Entities/SyncRun.cs (addition)
  public ICollection<SyncRunTunnel> TunnelRecords { get; set; } = [];
  // shared/Entities/TunnelMailboxFolder.cs (addition)
  public DateTime? ReconcilePendingAt { get; set; }            // column reconcile_pending_at
  // shared/Data/AFHSyncDbContext.cs
  public DbSet<SyncRunTunnel> SyncRunTunnels
  ```
  Table `sync_run_tunnels`; indexes `idx_sync_run_tunnels_run (sync_run_id)` and `idx_sync_run_tunnels_tunnel_completed (tunnel_id, completed_at DESC)`; FK to `sync_runs` cascade, FK to `tunnels` SET NULL. `status` uses the existing `sync_status` Postgres enum.
- Consumes: nothing from earlier tasks.

- [ ] **Step 1: Extend the migration tests (failing)**

In `tests/AFHSync.Tests.Integration/MigrationTests.cs`, add after the `Phase2Migration_DeletesIdLessContactSyncStateRows` test:

```csharp
    [Fact]
    public void Phase3Migration_CreatesSyncRunTunnels_AndReconcileFlag()
    {
        var migration = new Phase3RunTunnels();

        var ops = migration.UpOperations;
        var table = Assert.Single(ops.OfType<CreateTableOperation>());
        Assert.Equal("sync_run_tunnels", table.Name);
        Assert.Equal(
            new[] { "completed_at", "contacts_created", "contacts_failed", "contacts_removed", "contacts_skipped", "contacts_updated",
                    "error_summary", "id", "started_at", "status", "sync_run_id", "targets_count", "tunnel_id", "tunnel_name" },
            table.Columns.Select(c => c.Name).OrderBy(n => n).ToArray());

        var addColumn = Assert.Single(ops.OfType<AddColumnOperation>());
        Assert.Equal("tunnel_mailbox_folders", addColumn.Table);
        Assert.Equal("reconcile_pending_at", addColumn.Name);
        Assert.True(addColumn.IsNullable);
    }
```

In the `[PostgresFact]` test, after the `Assert.Contains("(tunnel_id, target_mailbox_id)", unique);` line add:

```csharp

                var runTunnelColumns = await db.Database
                    .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'sync_run_tunnels'")
                    .ToListAsync();
                Assert.Equal(
                    new[] { "completed_at", "contacts_created", "contacts_failed", "contacts_removed", "contacts_skipped", "contacts_updated",
                            "error_summary", "id", "started_at", "status", "sync_run_id", "targets_count", "tunnel_id", "tunnel_name" },
                    runTunnelColumns.OrderBy(c => c).ToArray());

                var statusType = await db.Database
                    .SqlQueryRaw<string>("SELECT udt_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'sync_run_tunnels' AND column_name = 'status'")
                    .ToListAsync();
                Assert.Equal("sync_status", Assert.Single(statusType));

                var runTunnelIndexes = await db.Database
                    .SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'sync_run_tunnels'")
                    .ToListAsync();
                Assert.Contains("idx_sync_run_tunnels_run", runTunnelIndexes);
                Assert.Contains("idx_sync_run_tunnels_tunnel_completed", runTunnelIndexes);

                var reconcileColumn = await db.Database
                    .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'tunnel_mailbox_folders' AND column_name = 'reconcile_pending_at'")
                    .ToListAsync();
                Assert.Single(reconcileColumn);
```

Also update the existing `folderColumns` assertion in that test so the expected array includes the new column:

```csharp
                Assert.Equal(
                    new[] { "folder_name", "graph_folder_id", "id", "reconcile_pending_at", "target_mailbox_id", "tunnel_id", "updated_at" },
                    folderColumns.OrderBy(c => c).ToArray());
```

- [ ] **Step 2: Run to verify it fails to compile**

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | grep -E "error|Passed|Failed" | head -5`
Expected: build error `The type or namespace name 'Phase3RunTunnels' does not exist in the namespace 'AFHSync.Api.Migrations'`.

- [ ] **Step 3: Add the entity, the navigation and the flag**

Create `shared/Entities/SyncRunTunnel.cs`:

```csharp
using AFHSync.Shared.Enums;

namespace AFHSync.Shared.Entities;

/// <summary>
/// Phase 3 (§3.1): one row per tunnel per sync run, written by the worker when the tunnel
/// finishes (success, warning, failure, or cancellation). Run detail and the tunnels list read
/// these instead of re-deriving per-tunnel outcomes from sync_run_items.
/// </summary>
public class SyncRunTunnel
{
    public int Id { get; set; }
    public int SyncRunId { get; set; }

    /// <summary>Null after the tunnel is deleted (SET NULL); <see cref="TunnelName"/> keeps the name.</summary>
    public int? TunnelId { get; set; }
    public string TunnelName { get; set; } = string.Empty;
    public SyncStatus Status { get; set; }

    /// <summary>Target mailboxes the tunnel resolved to this run (after scope and unavailable filtering).</summary>
    public int TargetsCount { get; set; }
    public int ContactsCreated { get; set; }
    public int ContactsUpdated { get; set; }
    public int ContactsRemoved { get; set; }
    public int ContactsSkipped { get; set; }
    public int ContactsFailed { get; set; }
    public string? ErrorSummary { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }

    // Navigation properties
    public SyncRun SyncRun { get; set; } = null!;
    public Tunnel? Tunnel { get; set; }
}
```

In `shared/Entities/SyncRun.cs`, replace

```csharp
    // Navigation properties
    public ICollection<SyncRunItem> SyncRunItems { get; set; } = [];
```

with

```csharp
    // Navigation properties
    public ICollection<SyncRunItem> SyncRunItems { get; set; } = [];
    public ICollection<SyncRunTunnel> TunnelRecords { get; set; } = [];
```

In `shared/Entities/TunnelMailboxFolder.cs`, after `public DateTime UpdatedAt { get; set; }` add:

```csharp

    /// <summary>
    /// Phase 3 (§3.7): set by the worker before the first create batch for this (tunnel, mailbox)
    /// and cleared only after every chunk's state rows are persisted. A non-null value at the
    /// start of a run means a crash or shutdown may have left Graph contacts with no state row —
    /// the folder is reconciled before classification.
    /// </summary>
    public DateTime? ReconcilePendingAt { get; set; }
```

- [ ] **Step 4: Map the columns**

Create `shared/Data/Configurations/SyncRunTunnelConfiguration.cs`:

```csharp
using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFHSync.Shared.Data.Configurations;

public class SyncRunTunnelConfiguration : IEntityTypeConfiguration<SyncRunTunnel>
{
    public void Configure(EntityTypeBuilder<SyncRunTunnel> builder)
    {
        builder.ToTable("sync_run_tunnels");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.SyncRunId).HasColumnName("sync_run_id").IsRequired();
        builder.Property(e => e.TunnelId).HasColumnName("tunnel_id");
        builder.Property(e => e.TunnelName).HasColumnName("tunnel_name").HasMaxLength(100).IsRequired();
        builder.Property(e => e.Status).HasColumnName("status").IsRequired();
        builder.Property(e => e.TargetsCount).HasColumnName("targets_count").HasDefaultValue(0);
        builder.Property(e => e.ContactsCreated).HasColumnName("contacts_created").HasDefaultValue(0);
        builder.Property(e => e.ContactsUpdated).HasColumnName("contacts_updated").HasDefaultValue(0);
        builder.Property(e => e.ContactsRemoved).HasColumnName("contacts_removed").HasDefaultValue(0);
        builder.Property(e => e.ContactsSkipped).HasColumnName("contacts_skipped").HasDefaultValue(0);
        builder.Property(e => e.ContactsFailed).HasColumnName("contacts_failed").HasDefaultValue(0);
        builder.Property(e => e.ErrorSummary).HasColumnName("error_summary");
        builder.Property(e => e.StartedAt).HasColumnName("started_at").IsRequired();
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at").IsRequired();

        builder.HasOne(e => e.SyncRun)
            .WithMany(r => r.TunnelRecords)
            .HasForeignKey(e => e.SyncRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Tunnel)
            .WithMany()
            .HasForeignKey(e => e.TunnelId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(e => e.SyncRunId).HasDatabaseName("idx_sync_run_tunnels_run");
        builder.HasIndex(e => new { e.TunnelId, e.CompletedAt })
            .HasDatabaseName("idx_sync_run_tunnels_tunnel_completed")
            .IsDescending(false, true);
    }
}
```

In `shared/Data/Configurations/TunnelMailboxFolderConfiguration.cs`, after the `UpdatedAt` line add:

```csharp
        builder.Property(e => e.ReconcilePendingAt).HasColumnName("reconcile_pending_at");
```

In `shared/Data/AFHSyncDbContext.cs`, after the `TunnelMailboxFolders` DbSet add:

```csharp
    public DbSet<SyncRunTunnel> SyncRunTunnels => Set<SyncRunTunnel>();
```

- [ ] **Step 5: Generate the migration**

Run: `dotnet ef migrations add Phase3RunTunnels --project api --startup-project api 2>&1 | tail -3`
Expected: `Build succeeded.` then `Done. To undo this action, use 'ef migrations remove'`.

Run: `ls api/Migrations | grep Phase3RunTunnels`
Expected: `<timestamp>_Phase3RunTunnels.Designer.cs` and `<timestamp>_Phase3RunTunnels.cs`.

Open `api/Migrations/<timestamp>_Phase3RunTunnels.cs` and check `Up()` contains:
- `migrationBuilder.AddColumn<DateTime>(name: "reconcile_pending_at", table: "tunnel_mailbox_folders", type: "timestamp with time zone", nullable: true)`
- `migrationBuilder.CreateTable(name: "sync_run_tunnels", …)` with `status = table.Column<SyncStatus>(type: "sync_status", nullable: false)` (the enum mapping — if it says `integer` the `MapEnum` registration was not picked up; stop and check `api/Program.cs`), `tunnel_name` as `character varying(100)`, an FK to `sync_runs` with `onDelete: ReferentialAction.Cascade` and an FK to `tunnels` with `onDelete: ReferentialAction.SetNull`
- `CreateIndex(name: "idx_sync_run_tunnels_run", …)` and `CreateIndex(name: "idx_sync_run_tunnels_tunnel_completed", table: "sync_run_tunnels", columns: new[] { "tunnel_id", "completed_at" }, descending: new[] { false, true })`.

If anything is missing: `dotnet ef migrations remove --project api --startup-project api`, fix Steps 3–4, regenerate. No `Sql(...)` data fix-up is needed in this phase.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 36, Skipped: 1` without Postgres; `Passed: 37, Skipped: 0` via `run-integration.sh` (do this at least once — the enum column type is only proven against a real server).

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 270, Skipped: 1` (unchanged).

- [ ] **Step 7: Commit**

```bash
git add shared/Entities/SyncRunTunnel.cs shared/Entities/SyncRun.cs shared/Entities/TunnelMailboxFolder.cs shared/Data/Configurations/SyncRunTunnelConfiguration.cs shared/Data/Configurations/TunnelMailboxFolderConfiguration.cs shared/Data/AFHSyncDbContext.cs api/Migrations tests/AFHSync.Tests.Integration/MigrationTests.cs
git commit -m "feat(db): Phase 3 migration — sync_run_tunnels per-tunnel run records, reconcile_pending_at

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---
### Task 3: Per-tunnel run records in the worker (§3.1 — write side)

**Files:**
- Create: `worker/Services/TunnelOutcome.cs`
- Modify: `worker/Services/SyncEngine.cs` (`RunAsync` tunnel loop, `ProcessTunnelAsync` return type, new `RecordTunnelRunAsync`)
- Modify: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // worker/Services/TunnelOutcome.cs
  internal sealed record TunnelOutcome(int Created, int Updated, int Skipped, int Failed, int Removed, int TargetsCount)
  { public static readonly TunnelOutcome Empty = new(0, 0, 0, 0, 0, 0); }

  // SyncEngine (private)
  Task<TunnelOutcome> ProcessTunnelAsync(...)            // was Task<(int,int,int,int,int)>
  Task RecordTunnelRunAsync(int runId, Tunnel tunnel, SyncStatus status, TunnelOutcome outcome, IEnumerable<string> errors, DateTime startedAt)
  ```
  A `sync_run_tunnels` row per tunnel the loop entered: `Success` (no failures), `Warning` (`Failed > 0`), `Failed` (unhandled exception, `error_summary = "{tunnel.Name}: {ex.Message}"`), `Cancelled` (shutdown token mid-tunnel, zero counts, `error_summary = "worker shutting down"`).
- Consumes: `SyncRunTunnel` + `DbSet SyncRunTunnels` (Task 2).

- [ ] **Step 1: Write the failing tests**

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`, add a new section before the `// Stub implementations` banner:

```csharp
    // ==============================
    // Phase 3 (3.1): one sync_run_tunnels row per tunnel per run
    // ==============================

    [Fact]
    public async Task RunAsync_WritesOneRecordPerTunnel_WithCountsTargetsAndSuccess()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-1", Email = "one@contoso.com", IsActive = true },
            new TargetMailbox { Id = 2, EntraId = "mb-2", Email = "two@contoso.com", IsActive = true });
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]));

        var run = await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        await using var verifyCtx = MakeDbContext(dbName);
        var record = await verifyCtx.SyncRunTunnels.SingleAsync();
        Assert.Equal(run.Id, record.SyncRunId);
        Assert.Equal(1, record.TunnelId);
        Assert.Equal("Avail Tunnel", record.TunnelName);
        Assert.Equal(SyncStatus.Success, record.Status);
        Assert.Equal(2, record.TargetsCount);
        Assert.Equal(2, record.ContactsCreated);          // one create per mailbox
        Assert.Equal(0, record.ContactsFailed);
        Assert.Null(record.ErrorSummary);
        Assert.True(record.CompletedAt >= record.StartedAt);
    }

    [Fact]
    public async Task RunAsync_ZeroSourceMembers_StillWritesRecord()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-1", Email = "one@contoso.com", IsActive = true });
        var engine = CreateEngine(dbName, sourceResolver: new FakeSourceResolver([]));

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        await using var verifyCtx = MakeDbContext(dbName);
        var record = await verifyCtx.SyncRunTunnels.SingleAsync();
        Assert.Equal(SyncStatus.Success, record.Status);
        Assert.Equal(0, record.TargetsCount);              // the tunnel returned before resolving targets
        Assert.Equal(0, record.ContactsCreated);
    }

    [Fact]
    public async Task RunAsync_SourceFailure_WritesWarningRecordWithErrorSummary()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
        var resolver = new FakeSourceResolver(
            [new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }],
            [new SourceFailure(11, "Buckhead Staff", "Request_UnsupportedQuery")]);
        var engine = CreateEngine(dbName, sourceResolver: resolver);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        await using var verifyCtx = MakeDbContext(dbName);
        var record = await verifyCtx.SyncRunTunnels.SingleAsync();
        Assert.Equal(SyncStatus.Warning, record.Status);
        Assert.Equal(1, record.ContactsFailed);
        Assert.Equal(1, record.ContactsCreated);
        Assert.Equal(1, record.TargetsCount);
        Assert.Equal("Avail Tunnel: source 'Buckhead Staff' failed: Request_UnsupportedQuery", record.ErrorSummary);
    }

    [Fact]
    public async Task RunAsync_TunnelThrows_WritesFailedRecordWithMessage()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: new ThrowingSourceResolver(), runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(1, runLogger.FinalizedTunnelsFailed);
        await using var verifyCtx = MakeDbContext(dbName);
        var record = await verifyCtx.SyncRunTunnels.SingleAsync();
        Assert.Equal(SyncStatus.Failed, record.Status);
        Assert.Equal(0, record.TargetsCount);
        Assert.Equal("Avail Tunnel: resolver exploded", record.ErrorSummary);
    }

    [Fact]
    public async Task RunAsync_ShutdownMidTunnel_WritesCancelledRecord()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-1", Email = "one@contoso.com", IsActive = true });
        using var cts = new CancellationTokenSource();
        var folderManager = new FakeContactFolderManager { OnRequested = () => cts.Cancel() };
        folderManager.Failures["mb-1"] = new OperationCanceledException();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderManager: folderManager);

        var run = await engine.RunAsync(null, RunType.Manual, isDryRun: false, cts.Token);

        Assert.Equal(SyncStatus.Cancelled, run.Status);
        await using var verifyCtx = MakeDbContext(dbName);
        var record = await verifyCtx.SyncRunTunnels.SingleAsync();
        Assert.Equal(SyncStatus.Cancelled, record.Status);
        Assert.Equal(SyncEngine.WorkerShutdownReason, record.ErrorSummary);
        Assert.Equal(0, record.ContactsCreated);
    }
```

And add this stub next to `ThrowingStaleContactHandler` at the end of the file:

```csharp
    /// <summary>Source resolver that throws — simulates an unhandled tunnel-level failure.</summary>
    private sealed class ThrowingSourceResolver : ISourceResolver
    {
        public Task<SourceResolution> ResolveAsync(Tunnel tunnel, CancellationToken ct)
            => throw new InvalidOperationException("resolver exploded");
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~SyncEngineTests.RunAsync_WritesOneRecordPerTunnel|FullyQualifiedName~SyncEngineTests.RunAsync_ZeroSourceMembers_Still|FullyQualifiedName~SyncEngineTests.RunAsync_SourceFailure_WritesWarning|FullyQualifiedName~SyncEngineTests.RunAsync_TunnelThrows_Writes|FullyQualifiedName~SyncEngineTests.RunAsync_ShutdownMidTunnel" 2>&1 | tail -8`
Expected: 5 failures, each `Sequence contains no elements` from `SingleAsync()` (no record written yet).

- [ ] **Step 3: Add `TunnelOutcome`**

Create `worker/Services/TunnelOutcome.cs`:

```csharp
namespace AFHSync.Worker.Services;

/// <summary>
/// Phase 3 (§3.1): what one tunnel did in one run. <see cref="TargetsCount"/> is the number of
/// target mailboxes the tunnel resolved to (after scope filtering and unavailable exclusion);
/// it is 0 when the tunnel returned before resolving targets (no source members).
/// </summary>
internal sealed record TunnelOutcome(int Created, int Updated, int Skipped, int Failed, int Removed, int TargetsCount)
{
    public static readonly TunnelOutcome Empty = new(0, 0, 0, 0, 0, 0);
}
```

- [ ] **Step 4: Return `TunnelOutcome` from `ProcessTunnelAsync`**

In `worker/Services/SyncEngine.cs`, change the signature line

```csharp
    private async Task<(int created, int updated, int skipped, int failed, int removed)> ProcessTunnelAsync(
```

to

```csharp
    private async Task<TunnelOutcome> ProcessTunnelAsync(
```

Inside it, the first early return (just after `logger.LogWarning("Tunnel {TunnelName}: 0 source members resolved, skipping", tunnel.Name);`):

```csharp
            return (0, 0, 0, sourceFailures, 0);
```
becomes
```csharp
            return new TunnelOutcome(0, 0, 0, sourceFailures, 0, 0);
```

The second early return (just after `logger.LogWarning("Tunnel {TunnelName}: no phone lists configured, skipping", tunnel.Name);`):

```csharp
            return (0, 0, 0, sourceFailures, 0);
```
becomes
```csharp
            return new TunnelOutcome(0, 0, 0, sourceFailures, 0, targetMailboxes.Count);
```

The final return of the method:

```csharp
        return (created, updated, skipped, failed, removed);
```
becomes
```csharp
        return new TunnelOutcome(created, updated, skipped, failed, removed, targetMailboxes.Count);
```

- [ ] **Step 5: Record the outcome in the tunnel loop**

In `RunAsync`, replace

```csharp
                try
                {
                    var (created, updated, skipped, failed, removed) =
                        await ProcessTunnelAsync(tunnel, run, isDryRun,
                            totalCreated, totalUpdated, totalSkipped, totalFailed, totalRemoved,
                            totalPhotosUpdated, totalPhotosFailed,
                            tunnelsProcessed, tunnelsWarned, tunnelsFailed, tunnelErrors, ct);

                    totalCreated += created;
                    totalUpdated += updated;
                    totalSkipped += skipped;
                    totalFailed += failed;
                    totalRemoved += removed;

                    if (failed > 0)
                        tunnelsWarned++;
                    else
                        tunnelsProcessed++;
```

with

```csharp
                var tunnelStartedAt = DateTime.UtcNow;
                var errorsBefore = tunnelErrors.Count;
                try
                {
                    var outcome = await ProcessTunnelAsync(tunnel, run, isDryRun,
                            totalCreated, totalUpdated, totalSkipped, totalFailed, totalRemoved,
                            totalPhotosUpdated, totalPhotosFailed,
                            tunnelsProcessed, tunnelsWarned, tunnelsFailed, tunnelErrors, ct);
                    var (created, updated, skipped, failed, removed, _) = outcome;

                    totalCreated += created;
                    totalUpdated += updated;
                    totalSkipped += skipped;
                    totalFailed += failed;
                    totalRemoved += removed;

                    if (failed > 0)
                        tunnelsWarned++;
                    else
                        tunnelsProcessed++;

                    // Phase 3 (§3.1): one sync_run_tunnels row per tunnel. This tunnel's errors are
                    // the tunnelErrors entries appended since it started.
                    await RecordTunnelRunAsync(run.Id, tunnel,
                        failed > 0 ? SyncStatus.Warning : SyncStatus.Success,
                        outcome, tunnelErrors.Skip(errorsBefore), tunnelStartedAt);
```

Then replace the two catch blocks of that `try`:

```csharp
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    logger.LogInformation("Shutdown requested during tunnel {TunnelId} ({TunnelName}) — stopping", tunnel.Id, tunnel.Name);
                    wasCancelled = true;
                    cancelReason = WorkerShutdownReason;
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Tunnel {TunnelId} ({TunnelName}) failed with unhandled exception",
                        tunnel.Id, tunnel.Name);
                    tunnelsFailed++;
                    tunnelErrors.Add($"{tunnel.Name}: {ex.Message}");
                }
```

with

```csharp
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    logger.LogInformation("Shutdown requested during tunnel {TunnelId} ({TunnelName}) — stopping", tunnel.Id, tunnel.Name);
                    await RecordTunnelRunAsync(run.Id, tunnel, SyncStatus.Cancelled, TunnelOutcome.Empty,
                        [WorkerShutdownReason], tunnelStartedAt);
                    wasCancelled = true;
                    cancelReason = WorkerShutdownReason;
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Tunnel {TunnelId} ({TunnelName}) failed with unhandled exception",
                        tunnel.Id, tunnel.Name);
                    tunnelsFailed++;
                    tunnelErrors.Add($"{tunnel.Name}: {ex.Message}");
                    await RecordTunnelRunAsync(run.Id, tunnel, SyncStatus.Failed, TunnelOutcome.Empty,
                        tunnelErrors.Skip(errorsBefore), tunnelStartedAt);
                }
```

- [ ] **Step 6: Add `RecordTunnelRunAsync`**

In `SyncEngine.cs`, directly after the `UpdateRunProgressAsync` method add:

```csharp
    /// <summary>
    /// Phase 3 (§3.1): writes the tunnel's row in sync_run_tunnels. Best-effort bookkeeping with a
    /// fresh context and CancellationToken.None — a shutdown mid-run must not lose the record.
    /// </summary>
    private async Task RecordTunnelRunAsync(
        int runId,
        Tunnel tunnel,
        SyncStatus status,
        TunnelOutcome outcome,
        IEnumerable<string> errors,
        DateTime startedAt)
    {
        try
        {
            var summary = string.Join("; ", errors);
            await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            db.SyncRunTunnels.Add(new SyncRunTunnel
            {
                SyncRunId = runId,
                TunnelId = tunnel.Id,
                TunnelName = tunnel.Name,
                Status = status,
                TargetsCount = outcome.TargetsCount,
                ContactsCreated = outcome.Created,
                ContactsUpdated = outcome.Updated,
                ContactsRemoved = outcome.Removed,
                ContactsSkipped = outcome.Skipped,
                ContactsFailed = outcome.Failed,
                ErrorSummary = summary.Length == 0 ? null : summary,
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record per-tunnel result for RunId={RunId} tunnel {TunnelId}", runId, tunnel.Id);
        }
    }
```

- [ ] **Step 7: Run the unit tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 275, Skipped: 1` (270 + 5).

- [ ] **Step 8: Commit**

```bash
git add worker/Services/TunnelOutcome.cs worker/Services/SyncEngine.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -m "feat(worker): write a sync_run_tunnels record after every tunnel (success/warning/failed/cancelled)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Per-tunnel run records in the API and run detail (§3.1 — read side)

**Files:**
- Modify: `api/DTOs/TunnelRunSummaryDto.cs`
- Modify: `api/Controllers/SyncRunsController.cs` (`GetRun`)
- Modify: `api/Controllers/TunnelsController.cs` (`GetAll`)
- Modify: `tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs`
- Modify: `tests/AFHSync.Tests.Integration/Api/TunnelsControllerTests.cs`
- Modify: `frontend/src/types/sync-run.ts`
- Modify: `frontend/src/app/(app)/runs/[id]/page.tsx`

**Interfaces:**
- Produces:
  ```csharp
  public record TunnelRunSummaryDto(int? TunnelId, string TunnelName, int ContactsCreated, int ContactsUpdated, int ContactsRemoved,
      int ContactsSkipped, int ContactsFailed, int PhotosUpdated, int PhotosFailed, string[] Errors,
      string? Status = null, int? TargetsCount = null);
  // Status is the pg name ("success" | "warning" | "failed" | "cancelled") when the run has records, null on the items fallback.
  ```
  ```ts
  // frontend/src/types/sync-run.ts
  export interface TunnelRunSummaryDto { …existing…; status: SyncRunStatus | null; targetsCount: number | null; }
  ```
  `GET /api/tunnels`: `lastSync` and `estimatedTargetUsers` per tunnel come from that tunnel's latest `sync_run_tunnels` row (fallback: `lastSync = null`, `estimatedTargetUsers` = distinct mailboxes in `contact_sync_state`).
- Consumes: `SyncRunTunnel` (Task 2); records written by Task 3.

- [ ] **Step 1: Write the failing integration tests**

In `tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs`, add after `GetRun_Returns404_ForNonexistentRun`:

```csharp
    [Fact]
    public async Task GetRun_BuildsTunnelSummariesFromRecords_PhotosAndErrorsStillFromItems()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var tunnel = new Tunnel { Name = "Rec Tunnel", StalePolicy = StalePolicy.FlagHold, Status = TunnelStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Tunnels.Add(tunnel);
        var run = new SyncRun { RunType = RunType.Manual, Status = SyncStatus.Warning, StartedAt = DateTime.UtcNow.AddMinutes(-5), CompletedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow.AddMinutes(-5) };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync();
        db.SyncRunTunnels.Add(new SyncRunTunnel
        {
            SyncRunId = run.Id, TunnelId = tunnel.Id, TunnelName = "Rec Tunnel", Status = SyncStatus.Warning,
            TargetsCount = 12, ContactsCreated = 3, ContactsUpdated = 2, ContactsRemoved = 0, ContactsSkipped = 40, ContactsFailed = 1,
            ErrorSummary = "Rec Tunnel: something", StartedAt = DateTime.UtcNow.AddMinutes(-4), CompletedAt = DateTime.UtcNow.AddMinutes(-1)
        });
        db.SyncRunItems.AddRange(
            new SyncRunItem { SyncRunId = run.Id, TunnelId = tunnel.Id, Action = "failed", ErrorMessage = "Folder 'Rec Tunnel': boom", CreatedAt = DateTime.UtcNow },
            new SyncRunItem { SyncRunId = run.Id, TunnelId = tunnel.Id, Action = "photo_updated", CreatedAt = DateTime.UtcNow },
            new SyncRunItem { SyncRunId = run.Id, TunnelId = tunnel.Id, Action = "created", CreatedAt = DateTime.UtcNow });  // one item, but the record says 3
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync($"/api/sync-runs/{run.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var summary = Assert.Single(body.GetProperty("tunnelSummaries").EnumerateArray());
        Assert.Equal("Rec Tunnel", summary.GetProperty("tunnelName").GetString());
        Assert.Equal(3, summary.GetProperty("contactsCreated").GetInt32());     // from the record, not the single item
        Assert.Equal(1, summary.GetProperty("contactsFailed").GetInt32());
        Assert.Equal(40, summary.GetProperty("contactsSkipped").GetInt32());
        Assert.Equal(1, summary.GetProperty("photosUpdated").GetInt32());       // from items
        Assert.Equal("warning", summary.GetProperty("status").GetString());
        Assert.Equal(12, summary.GetProperty("targetsCount").GetInt32());
        var errors = summary.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Folder 'Rec Tunnel': boom", errors);
    }

    [Fact]
    public async Task GetRun_WithoutRecords_FallsBackToGroupingItems()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var tunnel = new Tunnel { Name = "Legacy Tunnel", StalePolicy = StalePolicy.FlagHold, Status = TunnelStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Tunnels.Add(tunnel);
        var run = new SyncRun { RunType = RunType.PhotoSync, Status = SyncStatus.Success, StartedAt = DateTime.UtcNow.AddMinutes(-5), CompletedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow.AddMinutes(-5) };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync();
        db.SyncRunItems.AddRange(
            new SyncRunItem { SyncRunId = run.Id, TunnelId = tunnel.Id, Action = "created", CreatedAt = DateTime.UtcNow },
            new SyncRunItem { SyncRunId = run.Id, TunnelId = tunnel.Id, Action = "created", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync($"/api/sync-runs/{run.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var summary = Assert.Single(body.GetProperty("tunnelSummaries").EnumerateArray());
        Assert.Equal("Legacy Tunnel", summary.GetProperty("tunnelName").GetString());
        Assert.Equal(2, summary.GetProperty("contactsCreated").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, summary.GetProperty("status").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, summary.GetProperty("targetsCount").ValueKind);
    }
```

In `tests/AFHSync.Tests.Integration/Api/TunnelsControllerTests.cs`, add after `GetTunnel_NotFound_Returns404`:

```csharp
    [Fact]
    public async Task GetAll_LastSyncAndTargetUsers_ComeFromLatestTunnelRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var tunnel = new Tunnel { Name = "Record Tunnel", StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, Status = TunnelStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Tunnels.Add(tunnel);
        var older = new SyncRun { RunType = RunType.Scheduled, Status = SyncStatus.Success, StartedAt = DateTime.UtcNow.AddHours(-8), CompletedAt = DateTime.UtcNow.AddHours(-7), CreatedAt = DateTime.UtcNow.AddHours(-8) };
        var newer = new SyncRun { RunType = RunType.Scheduled, Status = SyncStatus.Warning, StartedAt = DateTime.UtcNow.AddHours(-4), CompletedAt = DateTime.UtcNow.AddHours(-3), CreatedAt = DateTime.UtcNow.AddHours(-4) };
        db.SyncRuns.AddRange(older, newer);
        await db.SaveChangesAsync();
        var newerCompleted = DateTime.UtcNow.AddHours(-3);
        db.SyncRunTunnels.AddRange(
            new SyncRunTunnel { SyncRunId = older.Id, TunnelId = tunnel.Id, TunnelName = tunnel.Name, Status = SyncStatus.Success, TargetsCount = 5, ContactsUpdated = 1, StartedAt = DateTime.UtcNow.AddHours(-8), CompletedAt = DateTime.UtcNow.AddHours(-7) },
            new SyncRunTunnel { SyncRunId = newer.Id, TunnelId = tunnel.Id, TunnelName = tunnel.Name, Status = SyncStatus.Warning, TargetsCount = 9, ContactsUpdated = 7, StartedAt = DateTime.UtcNow.AddHours(-4), CompletedAt = newerCompleted });
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync("/api/tunnels");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tunnels = await response.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
        var dto = tunnels!.Single(t => t.GetProperty("id").GetInt32() == tunnel.Id);
        Assert.Equal(9, dto.GetProperty("estimatedTargetUsers").GetInt32());
        var lastSync = dto.GetProperty("lastSync");
        Assert.Equal("warning", lastSync.GetProperty("status").GetString());
        Assert.Equal(7, lastSync.GetProperty("contactsUpdated").GetInt32());
        Assert.Equal(newerCompleted, lastSync.GetProperty("completedAt").GetDateTime().ToUniversalTime(), TimeSpan.FromSeconds(1));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet --filter "FullyQualifiedName~GetRun_BuildsTunnelSummaries|FullyQualifiedName~GetRun_WithoutRecords|FullyQualifiedName~GetAll_LastSyncAndTargetUsers" 2>&1 | tail -8`
Expected: 3 failures — `The given key 'status' was not present` / count mismatch (`3` vs `1`) / `estimatedTargetUsers` `9` vs `0`.

- [ ] **Step 3: Extend the DTO**

Replace the contents of `api/DTOs/TunnelRunSummaryDto.cs` with:

```csharp
namespace AFHSync.Api.DTOs;

/// <summary>
/// Per-tunnel breakdown in run detail. Phase 3 (§3.1): contact counts come from
/// sync_run_tunnels when the run has records; <see cref="Status"/> and <see cref="TargetsCount"/>
/// are null on the items-only fallback (photo-sync runs and pre-Phase-3 history).
/// </summary>
public record TunnelRunSummaryDto(
    int? TunnelId,
    string TunnelName,
    int ContactsCreated,
    int ContactsUpdated,
    int ContactsRemoved,
    int ContactsSkipped,
    int ContactsFailed,
    int PhotosUpdated,
    int PhotosFailed,
    string[] Errors,
    string? Status = null,
    int? TargetsCount = null
);
```

- [ ] **Step 4: Build `tunnelSummaries` from records in `SyncRunsController.GetRun`**

In `api/Controllers/SyncRunsController.cs`, add `using AFHSync.Shared.Entities;` to the usings, then replace everything in `GetRun` from the comment `// Compute per-tunnel summaries from SyncRunItems grouped by TunnelId` down to (and including) the `)).ToArray();` that closes `summaryDtos` with:

```csharp
        // Photo counts and error strings come from run items (photo sync writes items only).
        var itemGroups = (await db.SyncRunItems
            .Where(i => i.SyncRunId == id)
            .GroupBy(i => i.TunnelId)
            .Select(g => new
            {
                TunnelId = g.Key,
                Created = g.Count(i => i.Action == "created"),
                Updated = g.Count(i => i.Action == "updated"),
                Removed = g.Count(i => i.Action == "removed"),
                Skipped = g.Count(i => i.Action == "skipped"),
                Failed = g.Count(i => i.Action == "failed"),
                Photos = g.Count(i => i.Action == "photo_updated"),
                PhotosFailed = g.Count(i => i.Action == "photo_failed"),
                Errors = g.Where(i => i.ErrorMessage != null)
                          .Select(i => i.ErrorMessage!)
                          .ToArray()
            })
            .ToListAsync())
            .Select(g => new ItemCounts(g.TunnelId, g.Created, g.Updated, g.Removed, g.Skipped, g.Failed, g.Photos, g.PhotosFailed, g.Errors))
            .ToList();

        // Resolve tunnel names for the items-only fallback
        var tunnelIds = itemGroups
            .Where(s => s.TunnelId.HasValue)
            .Select(s => s.TunnelId!.Value)
            .Distinct()
            .ToList();

        var tunnelNames = await db.Tunnels
            .Where(t => tunnelIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name);

        TunnelRunSummaryDto FromItems(ItemCounts s) => new(
            s.TunnelId,
            s.TunnelId.HasValue && tunnelNames.TryGetValue(s.TunnelId.Value, out var name) ? name : "Unknown",
            s.Created, s.Updated, s.Removed, s.Skipped, s.Failed, s.Photos, s.PhotosFailed, s.Errors);

        // Phase 3 (§3.1): contact counts and status come from the per-tunnel records when the run
        // has any; item groups for tunnels without a record (or with no tunnel id) keep the old shape.
        var records = await db.SyncRunTunnels
            .Where(t => t.SyncRunId == id)
            .OrderBy(t => t.StartedAt).ThenBy(t => t.Id)
            .AsNoTracking()
            .ToListAsync();

        TunnelRunSummaryDto[] summaryDtos;
        if (records.Count > 0)
        {
            var itemsByTunnel = itemGroups.Where(g => g.TunnelId.HasValue).ToDictionary(g => g.TunnelId!.Value);
            var covered = new HashSet<int>();
            var list = new List<TunnelRunSummaryDto>();
            foreach (var r in records)
            {
                ItemCounts? items = null;
                if (r.TunnelId.HasValue)
                {
                    covered.Add(r.TunnelId.Value);
                    itemsByTunnel.TryGetValue(r.TunnelId.Value, out items);
                }
                list.Add(new TunnelRunSummaryDto(
                    r.TunnelId,
                    r.TunnelName,
                    r.ContactsCreated,
                    r.ContactsUpdated,
                    r.ContactsRemoved,
                    r.ContactsSkipped,
                    r.ContactsFailed,
                    items?.Photos ?? 0,
                    items?.PhotosFailed ?? 0,
                    items?.Errors ?? [],
                    EnumHelpers.ToPgName(r.Status),
                    r.TargetsCount));
            }
            foreach (var g in itemGroups.Where(g => !g.TunnelId.HasValue || !covered.Contains(g.TunnelId.Value)))
                list.Add(FromItems(g));
            summaryDtos = list.ToArray();
        }
        else
        {
            summaryDtos = itemGroups.Select(FromItems).ToArray();
        }
```

And at the end of the class (after `GetRunItems`), add the private record used above:

```csharp
    /// <summary>Per-tunnel counts derived from sync_run_items (the pre-Phase-3 source of truth).</summary>
    private sealed record ItemCounts(
        int? TunnelId, int Created, int Updated, int Removed, int Skipped, int Failed,
        int Photos, int PhotosFailed, string[] Errors);
```

- [ ] **Step 5: Per-tunnel `LastSync` / `EstimatedTargetUsers` in `TunnelsController.GetAll`**

In `api/Controllers/TunnelsController.cs`, `GetAll`: delete the block

```csharp
        var lastRuns = await _db.SyncRuns
            .OrderByDescending(r => r.CompletedAt)
            .Take(50)
            .AsNoTracking()
            .ToListAsync();
```

and after the `syncStats` query add:

```csharp
        // Phase 3 (§3.1): last sync and target count per tunnel come from that tunnel's latest
        // sync_run_tunnels row. One small ordered query per tunnel (~10 tunnels) — portable
        // across Npgsql and the InMemory provider, unlike GroupBy(...).First().
        var latestByTunnel = new Dictionary<int, SyncRunTunnel>();
        foreach (var t in tunnels)
        {
            var latest = await _db.SyncRunTunnels
                .Where(r => r.TunnelId == t.Id)
                .OrderByDescending(r => r.CompletedAt)
                .ThenByDescending(r => r.Id)
                .AsNoTracking()
                .FirstOrDefaultAsync();
            if (latest is not null)
                latestByTunnel[t.Id] = latest;
        }
```

Then in the `foreach (var t in tunnels)` loop replace

```csharp
            // Last sync: most recent completed SyncRun (tunnel-level aggregates are stored at SyncRun level for now)
            var lastRun = lastRuns.FirstOrDefault(r => r.Status == SyncStatus.Success || r.Status == SyncStatus.Warning);
            TunnelLastSyncDto? lastSync = lastRun is not null
                ? new TunnelLastSyncDto(
                    EnumHelpers.ToPgName(lastRun.Status),
                    lastRun.CompletedAt,
                    lastRun.ContactsUpdated)
                : null;
```

with

```csharp
            latestByTunnel.TryGetValue(t.Id, out var latest);
            TunnelLastSyncDto? lastSync = latest is not null
                ? new TunnelLastSyncDto(
                    EnumHelpers.ToPgName(latest.Status),
                    latest.CompletedAt,
                    latest.ContactsUpdated)
                : null;
```

and replace the `EstimatedTargetUsers` argument

```csharp
                stats?.TargetUserCount ?? 0,
```

with

```csharp
                latest?.TargetsCount ?? stats?.TargetUserCount ?? 0,
```

- [ ] **Step 6: Run the integration tests**

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 39, Skipped: 1` (36 + 3).

- [ ] **Step 7: Frontend — surface status and mailbox count per tunnel**

In `frontend/src/types/sync-run.ts`, inside `TunnelRunSummaryDto` after `errors: string[];` add:

```ts
  /** Phase 3 (§3.1): from sync_run_tunnels; null when the run predates per-tunnel records. */
  status: SyncRunStatus | null;
  targetsCount: number | null;
```

In `frontend/src/app/(app)/runs/[id]/page.tsx`, in the per-tunnel breakdown replace

```tsx
                  <div className="flex items-center justify-between gap-2">
                    <h4 className="font-medium text-sm">{ts.tunnelName}</h4>
                    <div className="flex items-center gap-2">
```

with

```tsx
                  <div className="flex items-center justify-between gap-2">
                    <div className="flex items-center gap-2">
                      <h4 className="font-medium text-sm">{ts.tunnelName}</h4>
                      {ts.status && <StatusBadge status={ts.status} />}
                      {ts.targetsCount !== null && (
                        <span className="text-xs text-text-muted">
                          {ts.targetsCount} mailbox{ts.targetsCount === 1 ? '' : 'es'}
                        </span>
                      )}
                    </div>
                    <div className="flex items-center gap-2">
```

Run: `cd frontend && npm run build 2>&1 | tail -3; cd ..`
Expected: `✓ Compiled successfully`.

- [ ] **Step 8: Commit**

```bash
git add api/DTOs/TunnelRunSummaryDto.cs api/Controllers/SyncRunsController.cs api/Controllers/TunnelsController.cs tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs tests/AFHSync.Tests.Integration/Api/TunnelsControllerTests.cs frontend/src/types/sync-run.ts "frontend/src/app/(app)/runs/[id]/page.tsx"
git commit -m "feat(api): run detail and tunnels list read sync_run_tunnels (status, targets, per-tunnel last sync)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---
### Task 5: Pagination envelopes (§3.3)

**Files:**
- Create: `api/DTOs/PagedResult.cs`
- Create: `api/Services/Paging.cs`
- Modify: `api/Controllers/SyncRunsController.cs` (`GetRuns`, `GetRunItems`)
- Modify: `api/Controllers/PhoneListsController.cs` (`GetContacts`)
- Create: `tests/AFHSync.Tests.Unit/Api/PagingTests.cs`
- Modify: `tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs`, `tests/AFHSync.Tests.Integration/Api/PhoneListsControllerTests.cs`
- Modify: `frontend/src/types/common.ts`, `frontend/src/lib/api.ts`, `frontend/src/hooks/use-sync-runs.ts`, `frontend/src/hooks/use-phone-lists.ts`
- Modify: `frontend/src/app/(app)/runs/page.tsx`, `frontend/src/app/(app)/runs/[id]/page.tsx`, `frontend/src/app/(app)/lists/page.tsx`

**Interfaces:**
- Produces:
  ```csharp
  // api/DTOs/PagedResult.cs
  public record PagedResult<T>(IReadOnlyList<T> Items, bool HasMore, int? Total = null);   // Total omitted from JSON when null
  // api/Services/Paging.cs
  public static (int page, int pageSize) Paging.Clamp(int page, int pageSize, int defaultSize, int max);
  // GET /api/sync-runs?page&pageSize            → PagedResult<SyncRunDto>      (default 20, max 100)
  // GET /api/sync-runs/{id}/items?page&pageSize → PagedResult<SyncRunItemDto>  (default 50, max 200)
  // GET /api/phone-lists/{id}/contacts?page&pageSize → PagedResult<ContactDto> with Total (default 20, max 500)
  ```
  ```ts
  // frontend/src/types/common.ts
  export interface PagedResult<T> { items: T[]; hasMore: boolean; total?: number | null; }
  // api.syncRuns.list/getItems and api.phoneLists.getContacts return PagedResult<…>; hooks pass pageSize exactly.
  ```
- Consumes: nothing from earlier tasks.

- [ ] **Step 1: Write the failing unit test for `Paging.Clamp`**

Create `tests/AFHSync.Tests.Unit/Api/PagingTests.cs`:

```csharp
using AFHSync.Api.Services;

namespace AFHSync.Tests.Unit.Api;

public class PagingTests
{
    [Theory]
    [InlineData(0, 0, 20, 100, 1, 20)]      // below range ⇒ page 1, default size
    [InlineData(-5, -1, 50, 200, 1, 50)]
    [InlineData(3, 25, 20, 100, 3, 25)]     // in range ⇒ unchanged
    [InlineData(2, 5000, 20, 500, 2, 500)]  // above max ⇒ max
    [InlineData(1, 1, 20, 100, 1, 1)]       // lower bound is 1
    public void Clamp_NormalisesPageAndPageSize(int page, int pageSize, int defaultSize, int max, int expectedPage, int expectedSize)
    {
        var (p, s) = Paging.Clamp(page, pageSize, defaultSize, max);

        Assert.Equal(expectedPage, p);
        Assert.Equal(expectedSize, s);
    }
}
```

- [ ] **Step 2: Write the failing integration tests**

In `tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs`, replace the body of `GetRuns_ReturnsPaginatedList` after `await db.SaveChangesAsync();` with:

```csharp
        var response = await AuthenticatedGetAsync("/api/sync-runs?page=1&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(body.GetProperty("items").GetArrayLength() >= 3);
        Assert.Contains(body.GetProperty("hasMore").ValueKind, new[] { System.Text.Json.JsonValueKind.True, System.Text.Json.JsonValueKind.False });
        Assert.False(body.TryGetProperty("total", out _));     // runs carry no total
```

Then add two tests after it:

```csharp
    [Fact]
    public async Task GetRuns_HasMore_IsTrueWhenAnotherPageExists()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        for (int i = 0; i < 3; i++)
        {
            db.SyncRuns.Add(new SyncRun
            {
                RunType = RunType.Scheduled, Status = SyncStatus.Success, IsDryRun = false,
                StartedAt = DateTime.UtcNow.AddHours(-i), CompletedAt = DateTime.UtcNow.AddHours(-i).AddMinutes(5), CreatedAt = DateTime.UtcNow.AddHours(-i)
            });
        }
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync("/api/sync-runs?page=1&pageSize=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2, body.GetProperty("items").GetArrayLength());     // exactly pageSize, never pageSize + 1
        Assert.True(body.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task GetRunItems_ReturnsEnvelope_AndLastPageHasMoreFalse()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var run = new SyncRun { RunType = RunType.Manual, Status = SyncStatus.Success, StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync();
        for (int i = 0; i < 3; i++)
            db.SyncRunItems.Add(new SyncRunItem { SyncRunId = run.Id, Action = "created", CreatedAt = DateTime.UtcNow.AddSeconds(-i) });
        await db.SaveChangesAsync();

        var first = await (await AuthenticatedGetAsync($"/api/sync-runs/{run.Id}/items?page=1&pageSize=2")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var second = await (await AuthenticatedGetAsync($"/api/sync-runs/{run.Id}/items?page=2&pageSize=2")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.False(second.GetProperty("hasMore").GetBoolean());
    }
```

In `tests/AFHSync.Tests.Integration/Api/PhoneListsControllerTests.cs`, replace the test `GetContacts_ExistingList_Returns200WithEmptyArray` with:

```csharp
    [Fact]
    public async Task GetContacts_ExistingList_ReturnsEmptyEnvelopeWithTotal()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var list = new PhoneList { Name = "Empty List", ContactCount = 0, UserCount = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.PhoneLists.Add(list);
        await db.SaveChangesAsync();

        var response = await AuthenticatedGetAsync($"/api/phone-lists/{list.Id}/contacts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.False(body.GetProperty("hasMore").GetBoolean());
        Assert.Equal(0, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetContacts_PagesDistinctSourceUsers_WithTotal_AndDefaultsBadPageSize()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var list = new PhoneList { Name = "Paged List", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.PhoneLists.Add(list);
        var mailbox = new TargetMailbox { EntraId = "pl-mbx", Email = "pl-mbx@contoso.com", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.TargetMailboxes.Add(mailbox);
        var users = new[]
        {
            new SourceUser { EntraId = "pl-u1", DisplayName = "Cara", Email = "cara@contoso.com", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new SourceUser { EntraId = "pl-u2", DisplayName = "Abe", Email = "abe@contoso.com", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new SourceUser { EntraId = "pl-u3", DisplayName = "Bea", Email = "bea@contoso.com", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        };
        db.SourceUsers.AddRange(users);
        await db.SaveChangesAsync();
        foreach (var u in users)
            db.ContactSyncStates.Add(new ContactSyncState { SourceUserId = u.Id, PhoneListId = list.Id, TargetMailboxId = mailbox.Id, GraphContactId = $"g-{u.Id}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        // A second state row for the same user must not count twice.
        db.ContactSyncStates.Add(new ContactSyncState { SourceUserId = users[0].Id, PhoneListId = list.Id, TargetMailboxId = mailbox.Id, GraphContactId = "g-dup", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var first = await (await AuthenticatedGetAsync($"/api/phone-lists/{list.Id}/contacts?page=1&pageSize=2")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var second = await (await AuthenticatedGetAsync($"/api/phone-lists/{list.Id}/contacts?page=2&pageSize=2")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var defaulted = await (await AuthenticatedGetAsync($"/api/phone-lists/{list.Id}/contacts?page=1&pageSize=0")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.False(second.GetProperty("hasMore").GetBoolean());
        Assert.Equal(3, defaulted.GetProperty("items").GetArrayLength());    // pageSize 0 ⇒ default 20
        Assert.False(defaulted.GetProperty("hasMore").GetBoolean());
    }
```

Add `using AFHSync.Shared.Entities;` is already present in that file; nothing else to import.

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~PagingTests" 2>&1 | grep -E "error" | head -3`
Expected: build error `The type or namespace name 'Paging' does not exist`.

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet --filter "FullyQualifiedName~GetRuns_|FullyQualifiedName~GetRunItems_|FullyQualifiedName~GetContacts_" 2>&1 | tail -6`
Expected: failures with `The requested operation requires an element of type 'Object', but the target element has type 'Array'` (the endpoints still return bare arrays).

- [ ] **Step 4: Add the envelope and the clamp helper**

Create `api/DTOs/PagedResult.cs`:

```csharp
using System.Text.Json.Serialization;

namespace AFHSync.Api.DTOs;

/// <summary>
/// Phase 3 (§3.3): paged envelope. <see cref="HasMore"/> is computed server-side by fetching
/// <c>pageSize + 1</c> rows, so clients never have to over-fetch. <see cref="Total"/> is only set
/// by endpoints whose count is cheap (phone-list contacts) and is omitted from the JSON otherwise.
/// </summary>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    bool HasMore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Total = null);
```

Create `api/Services/Paging.cs`:

```csharp
namespace AFHSync.Api.Services;

/// <summary>Phase 3 (§3.3): one place for page/pageSize normalisation.</summary>
public static class Paging
{
    /// <summary>
    /// page &lt; 1 ⇒ 1; pageSize &lt; 1 ⇒ <paramref name="defaultSize"/>; pageSize &gt; <paramref name="max"/> ⇒ max.
    /// </summary>
    public static (int page, int pageSize) Clamp(int page, int pageSize, int defaultSize, int max)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = defaultSize;
        if (pageSize > max) pageSize = max;
        return (page, pageSize);
    }
}
```

- [ ] **Step 5: Wrap the three endpoints**

In `api/Controllers/SyncRunsController.cs`, add `using AFHSync.Api.Services;`. In `GetRuns`, replace

```csharp
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var runs = await db.SyncRuns
            .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
```

with

```csharp
        (page, pageSize) = Paging.Clamp(page, pageSize, defaultSize: 20, max: 100);

        // Fetch one extra row to know whether another page exists (§3.3).
        var runs = await db.SyncRuns
            .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
```

and replace its `return Ok(runs);` with

```csharp
        return Ok(new PagedResult<SyncRunDto>(runs.Take(pageSize).ToList(), runs.Count > pageSize));
```

In `GetRunItems`, replace

```csharp
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;
```

with

```csharp
        (page, pageSize) = Paging.Clamp(page, pageSize, defaultSize: 50, max: 200);
```

replace `.Take(pageSize)` in the `rawItems` query with `.Take(pageSize + 1)`, and replace

```csharp
        var items = rawItems.Select(i => new SyncRunItemDto(
```

with

```csharp
        var hasMore = rawItems.Count > pageSize;
        var items = rawItems.Take(pageSize).Select(i => new SyncRunItemDto(
```

and its `return Ok(items);` with

```csharp
        return Ok(new PagedResult<SyncRunItemDto>(items, hasMore));
```

In `api/Controllers/PhoneListsController.cs`, add `using AFHSync.Api.Services;`, and in `GetContacts` replace

```csharp
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;
```

with

```csharp
        // Phase 3 (§3.3): clamp to [1, 500]; the Targets page asks for 200 at a time.
        (page, pageSize) = Paging.Clamp(page, pageSize, defaultSize: 20, max: 500);
```

then replace the `distinctUserIds` query and the `contacts` query's tail so the method body after the existence check reads:

```csharp
        var distinctUsers = _db.ContactSyncStates
            .Where(c => c.PhoneListId == id)
            .Select(c => c.SourceUserId)
            .Distinct();

        var total = await distinctUsers.CountAsync();

        // Select distinct source user IDs for this phone list (one extra to compute hasMore), then join SourceUsers
        var distinctUserIds = await distinctUsers
            .OrderBy(userId => userId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync();

        var hasMore = distinctUserIds.Count > pageSize;
        var pageIds = distinctUserIds.Take(pageSize).ToList();

        var contacts = await _db.SourceUsers
            .Where(u => pageIds.Contains(u.Id))
            .OrderBy(u => u.DisplayName)
            .Select(u => new ContactDto(
                u.Id,
                u.DisplayName,
                u.Email,
                u.JobTitle,
                u.Department,
                u.OfficeLocation,
                u.BusinessPhone ?? u.MobilePhone,
                u.MobilePhone,
                u.CompanyName,
                u.StreetAddress,
                u.City,
                u.State,
                u.PostalCode,
                u.Country
            ))
            .AsNoTracking()
            .ToListAsync();

        return Ok(new PagedResult<ContactDto>(contacts, hasMore, total));
```

- [ ] **Step 6: Run the backend tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 280, Skipped: 1` (275 + 5 theory cases).

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 42, Skipped: 1` (39 + 3 new, one replaced).

- [ ] **Step 7: Frontend — types, api client, hooks**

In `frontend/src/types/common.ts` append:

```ts

/** Phase 3 (§3.3): paged envelope returned by /sync-runs, /sync-runs/{id}/items and /phone-lists/{id}/contacts. */
export interface PagedResult<T> {
  items: T[];
  hasMore: boolean;
  /** Only present on endpoints with a cheap count (phone-list contacts). */
  total?: number | null;
}
```

In `frontend/src/lib/api.ts`, add the import `import type { PagedResult } from '@/types/common';` after the existing type imports, and change the three calls:

```ts
  syncRuns: {
    list: (page: number, pageSize: number) =>
      fetchApi<PagedResult<SyncRunDto>>(`/sync-runs?page=${page}&pageSize=${pageSize}`),
    get: (id: number) => fetchApi<SyncRunDetailDto>(`/sync-runs/${id}`),
    getItems: (id: number, page: number, pageSize: number, action?: string) =>
      fetchApi<PagedResult<SyncRunItemDto>>(
        `/sync-runs/${id}/items?page=${page}&pageSize=${pageSize}${action ? `&action=${action}` : ''}`
      ),
```

and

```ts
    getContacts: (id: number, page: number, pageSize: number) =>
      fetchApi<PagedResult<ContactDto>>(`/phone-lists/${id}/contacts?page=${page}&pageSize=${pageSize}`),
```

In `frontend/src/hooks/use-sync-runs.ts` change `api.syncRuns.list(page, pageSize + 1)` to `api.syncRuns.list(page, pageSize)` and `api.syncRuns.getItems(id, page, pageSize + 1, action)` to `api.syncRuns.getItems(id, page, pageSize, action)`. In `frontend/src/hooks/use-phone-lists.ts` change `api.phoneLists.getContacts(id, page, pageSize + 1)` to `api.phoneLists.getContacts(id, page, pageSize)`.

- [ ] **Step 8: Frontend — pages consume the envelope**

In `frontend/src/app/(app)/runs/page.tsx` replace

```tsx
  const { data: rawData, isLoading } = useSyncRuns(page + 1, pageSize);

  const hasNextPage = (rawData?.length ?? 0) > pageSize;
  const data = rawData?.slice(0, pageSize) ?? [];
```

with

```tsx
  const { data: pageData, isLoading } = useSyncRuns(page + 1, pageSize);

  const hasNextPage = pageData?.hasMore ?? false;
  const data = pageData?.items ?? [];
```

In `frontend/src/app/(app)/runs/[id]/page.tsx` replace

```tsx
  const { data: rawItems, isLoading: itemsLoading } = useSyncRunItems(
    runId,
    itemPage + 1,
    pageSize,
    actionFilter,
  );

  const hasNextPage = (rawItems?.length ?? 0) > pageSize;
  const items = rawItems?.slice(0, pageSize) ?? [];
```

with

```tsx
  const { data: itemsPage, isLoading: itemsLoading } = useSyncRunItems(
    runId,
    itemPage + 1,
    pageSize,
    actionFilter,
  );

  const hasNextPage = itemsPage?.hasMore ?? false;
  const items = itemsPage?.items ?? [];
```

In `frontend/src/app/(app)/lists/page.tsx`:

1. Replace

```tsx
  const { data: contacts, isLoading: contactsLoading } =
    usePhoneListContacts(selectedListId ?? 0, contactPage + 1, 200);
```

with

```tsx
  const CONTACTS_PAGE_SIZE = 200;
  const { data: contactsPage, isLoading: contactsLoading } =
    usePhoneListContacts(selectedListId ?? 0, contactPage + 1, CONTACTS_PAGE_SIZE);
  const contacts = contactsPage?.items ?? [];
  const contactsTotal = contactsPage?.total ?? 0;
  const contactsShown = contactPage * CONTACTS_PAGE_SIZE + contacts.length;
```

2. Replace the phone-preview column wrapper

```tsx
          <div className="lg:w-[55%] flex justify-center lg:justify-start">
            <IPhoneFrame title={selectedList?.name ?? ''}>
```

with

```tsx
          <div className="lg:w-[55%] flex flex-col items-center lg:items-start">
            <IPhoneFrame title={selectedList?.name ?? ''}>
```

3. Replace `contacts={contacts ?? []}` in `<ContactList` with `contacts={contacts}`.

4. Directly after the closing `</IPhoneFrame>` add the "N of M" row (§3.3):

```tsx
            <div className="mt-3 flex items-center gap-4 text-xs text-text-muted">
              <span>Showing {contactsShown} of {contactsTotal}</span>
              <Button
                size="sm"
                variant="outline"
                disabled={contactPage === 0}
                onClick={() => setContactPage((p) => Math.max(0, p - 1))}
              >
                Previous
              </Button>
              <Button
                size="sm"
                variant="outline"
                disabled={!contactsPage?.hasMore}
                onClick={() => setContactPage((p) => p + 1)}
              >
                Next
              </Button>
            </div>
```

Run: `cd frontend && npm run build 2>&1 | tail -3 && npm test 2>&1 | tail -3; cd ..`
Expected: `✓ Compiled successfully`; `Tests  8 passed (8)`.

- [ ] **Step 9: Commit**

```bash
git add api/DTOs/PagedResult.cs api/Services/Paging.cs api/Controllers/SyncRunsController.cs api/Controllers/PhoneListsController.cs tests/AFHSync.Tests.Unit/Api/PagingTests.cs tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs tests/AFHSync.Tests.Integration/Api/PhoneListsControllerTests.cs frontend/src/types/common.ts frontend/src/lib/api.ts frontend/src/hooks/use-sync-runs.ts frontend/src/hooks/use-phone-lists.ts "frontend/src/app/(app)/runs/page.tsx" "frontend/src/app/(app)/runs/[id]/page.tsx" "frontend/src/app/(app)/lists/page.tsx"
git commit -m "feat(api,ui): paged { items, hasMore } envelopes for runs, run items and phone-list contacts (+ total, N of M)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: Target-scope validation on the edit page and the API (§3.2)

**Files:**
- Create: `api/Services/TargetScopeValidation.cs`
- Modify: `api/Controllers/TunnelsController.cs` (`Create`, `Update`)
- Create: `tests/AFHSync.Tests.Unit/Api/TargetScopeValidationTests.cs`
- Modify: `tests/AFHSync.Tests.Integration/Api/TunnelsControllerTests.cs`
- Create: `frontend/src/lib/target-scope.ts`, `frontend/src/lib/target-scope.test.ts`
- Modify: `frontend/src/components/wizard/StepTargets.tsx`, `frontend/src/components/TunnelWizard.tsx`, `frontend/src/app/(app)/tunnels/[id]/page.tsx`

**Interfaces:**
- Produces:
  ```csharp
  public static class TargetScopeValidation
  {
      public const string EmptyUsersMessage = "Select at least one user, or switch scope to All Users.";
      public const string EmptyGroupMessage = "Select a security group, or switch scope to All Users.";
      public const string BothScopesMessage = "A tunnel can be scoped to specific users or to a security group, not both.";
      public const string InvalidEmailsJsonMessage = "targetUserEmails must be a JSON array of email addresses.";
      public static string? Validate(string? targetUserEmails, string? targetGroupId);   // null = valid
  }
  // POST /api/tunnels and PUT /api/tunnels/{id} return 400 { message } when Validate(...) is non-null.
  ```
  ```ts
  // frontend/src/lib/target-scope.ts
  export type TargetScopeOption = 'all' | 'specific' | 'group';
  export function deriveTargetScope(targetGroupId, targetUserEmails): TargetScopeOption;
  export function parseTargetUserEmails(json): string[];
  export function validateTargetScope(targetGroupId, targetUserEmails): string | null;
  ```
- Consumes: nothing from earlier tasks.

- [ ] **Step 1: Write the failing unit tests (API)**

Create `tests/AFHSync.Tests.Unit/Api/TargetScopeValidationTests.cs`:

```csharp
using AFHSync.Api.Services;

namespace AFHSync.Tests.Unit.Api;

public class TargetScopeValidationTests
{
    [Fact]
    public void AllUsers_IsValid()
        => Assert.Null(TargetScopeValidation.Validate(null, null));

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"\", \"   \"]")]
    [InlineData("null")]
    public void EmptyEmails_IsRejected(string json)
        => Assert.Equal(TargetScopeValidation.EmptyUsersMessage, TargetScopeValidation.Validate(json, null));

    [Fact]
    public void NonEmptyEmails_IsValid()
        => Assert.Null(TargetScopeValidation.Validate("[\"a@contoso.com\"]", null));

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"emails\":[]}")]
    public void UnparseableEmails_IsRejected(string json)
        => Assert.Equal(TargetScopeValidation.InvalidEmailsJsonMessage, TargetScopeValidation.Validate(json, null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyGroupId_IsRejected(string groupId)
        => Assert.Equal(TargetScopeValidation.EmptyGroupMessage, TargetScopeValidation.Validate(null, groupId));

    [Fact]
    public void GroupId_IsValid()
        => Assert.Null(TargetScopeValidation.Validate(null, "11111111-2222-3333-4444-555555555555"));

    [Fact]
    public void BothScopes_IsRejected()
        => Assert.Equal(TargetScopeValidation.BothScopesMessage,
            TargetScopeValidation.Validate("[\"a@contoso.com\"]", "11111111-2222-3333-4444-555555555555"));
}
```

- [ ] **Step 2: Write the failing integration tests**

In `tests/AFHSync.Tests.Integration/Api/TunnelsControllerTests.cs`, add after `GetAll_LastSyncAndTargetUsers_ComeFromLatestTunnelRecord`:

```csharp
    [Fact]
    public async Task PostTunnel_EmptyTargetUserEmails_Returns400()
    {
        var createRequest = new
        {
            name = "Nobody Tunnel",
            sources = new[] { new { sourceType = "Ddg", sourceIdentifier = "startsWith(displayName, 'X')", sourceDisplayName = "X", sourceSmtpAddress = "x@atlantafinehomes.com", sourceFilterPlain = (string?)null } },
            targetListIds = Array.Empty<int>(),
            fieldProfileId = (int?)null,
            stalePolicy = "FlagHold",
            staleDays = 14,
            targetUserEmails = "[]"
        };

        var response = await AuthenticatedPostAsync("/api/tunnels", createRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Select at least one user, or switch scope to All Users.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PutTunnel_EmptyStringTargetGroupId_Returns400_AndLeavesTunnelUnchanged()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var tunnel = new Tunnel { Name = "Keep Me", StalePolicy = StalePolicy.FlagHold, StaleHoldDays = 14, Status = TunnelStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Tunnels.Add(tunnel);
        await db.SaveChangesAsync();

        var response = await AuthenticatedPutAsync($"/api/tunnels/{tunnel.Id}", new
        {
            name = "Renamed",
            sources = new[] { new { sourceType = "Ddg", sourceIdentifier = "startsWith(displayName, 'X')", sourceDisplayName = "X", sourceSmtpAddress = "x@atlantafinehomes.com", sourceFilterPlain = (string?)null } },
            targetListIds = Array.Empty<int>(),
            fieldProfileId = (int?)null,
            stalePolicy = "FlagHold",
            staleDays = 14,
            targetGroupId = ""
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Select a security group, or switch scope to All Users.", body.GetProperty("message").GetString());
        db.ChangeTracker.Clear();
        Assert.Equal("Keep Me", (await db.Tunnels.FindAsync(tunnel.Id))!.Name);
    }
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~TargetScopeValidationTests" 2>&1 | grep -E "error" | head -3`
Expected: build error `The name 'TargetScopeValidation' does not exist`.

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet --filter "FullyQualifiedName~PostTunnel_EmptyTargetUserEmails|FullyQualifiedName~PutTunnel_EmptyStringTargetGroupId" 2>&1 | tail -6`
Expected: 2 failures `Expected BadRequest, Actual Created` / `Actual OK`.

- [ ] **Step 4: Add the validator and wire it into `Create`/`Update`**

Create `api/Services/TargetScopeValidation.cs`:

```csharp
using System.Text.Json;

namespace AFHSync.Api.Services;

/// <summary>
/// Phase 3 (§3.2): the target-scope rules the wizard enforces, applied on the server so the edit
/// page (or any client) cannot save a tunnel that is scoped to nobody. Rules, in order:
/// a present-but-blank group id; both scopes at once; emails that are not a JSON string array;
/// an emails array with no non-blank entry.
/// </summary>
public static class TargetScopeValidation
{
    public const string EmptyUsersMessage = "Select at least one user, or switch scope to All Users.";
    public const string EmptyGroupMessage = "Select a security group, or switch scope to All Users.";
    public const string BothScopesMessage = "A tunnel can be scoped to specific users or to a security group, not both.";
    public const string InvalidEmailsJsonMessage = "targetUserEmails must be a JSON array of email addresses.";

    /// <summary>Returns the error message for an invalid combination, or null when it is valid.</summary>
    public static string? Validate(string? targetUserEmails, string? targetGroupId)
    {
        var hasGroup = targetGroupId is not null;
        var hasEmails = targetUserEmails is not null;

        if (hasGroup && string.IsNullOrWhiteSpace(targetGroupId))
            return EmptyGroupMessage;
        if (hasGroup && hasEmails)
            return BothScopesMessage;
        if (!hasEmails)
            return null;

        string?[] emails;
        try
        {
            emails = JsonSerializer.Deserialize<string?[]>(targetUserEmails!) ?? [];
        }
        catch (JsonException)
        {
            return InvalidEmailsJsonMessage;
        }

        return emails.All(string.IsNullOrWhiteSpace) ? EmptyUsersMessage : null;
    }
}
```

In `api/Controllers/TunnelsController.cs`, in `Create`, directly after the `StalePolicy` check

```csharp
        if (!EnumHelpers.TryFromPgName<StalePolicy>(request.StalePolicy, out var stalePolicy))
            return BadRequest(new { message = $"Invalid StalePolicy: {request.StalePolicy}" });
```

add

```csharp

        // Phase 3 (§3.2): same scope rules as the wizard, enforced server-side.
        var scopeError = TargetScopeValidation.Validate(request.TargetUserEmails, request.TargetGroupId);
        if (scopeError is not null)
            return BadRequest(new { message = scopeError });
```

and add the identical four lines in `Update`, directly after its own `StalePolicy` check (before `tunnel.Name = request.Name;`).

- [ ] **Step 5: Run the backend tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 291, Skipped: 1` (280 + 11 cases).

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 44, Skipped: 1`.

- [ ] **Step 6: Write the failing frontend tests**

Create `frontend/src/lib/target-scope.test.ts`:

```ts
import { describe, it, expect } from 'vitest';
import {
  deriveTargetScope,
  parseTargetUserEmails,
  validateTargetScope,
  TARGET_SCOPE_MESSAGES,
} from './target-scope';

describe('deriveTargetScope', () => {
  it('is "all" when neither scope is set', () => {
    expect(deriveTargetScope(null, null)).toBe('all');
    expect(deriveTargetScope(undefined, undefined)).toBe('all');
  });

  it('is "group" whenever a group id is present — including the "" picking sentinel', () => {
    expect(deriveTargetScope('', null)).toBe('group');
    expect(deriveTargetScope('abc', null)).toBe('group');
  });

  it('is "specific" whenever an emails JSON is present — including "[]"', () => {
    expect(deriveTargetScope(null, '[]')).toBe('specific');
    expect(deriveTargetScope(null, '["a@x.com"]')).toBe('specific');
  });
});

describe('parseTargetUserEmails', () => {
  it('returns the non-blank strings of a JSON array', () => {
    expect(parseTargetUserEmails('["a@x.com", "", "b@x.com"]')).toEqual(['a@x.com', 'b@x.com']);
  });

  it('returns [] for null, "[]", non-arrays and bad JSON', () => {
    expect(parseTargetUserEmails(null)).toEqual([]);
    expect(parseTargetUserEmails('[]')).toEqual([]);
    expect(parseTargetUserEmails('{"emails":["a@x.com"]}')).toEqual([]);
    expect(parseTargetUserEmails('nope')).toEqual([]);
  });
});

describe('validateTargetScope', () => {
  it('accepts All Users, a chosen group and a non-empty user list', () => {
    expect(validateTargetScope(null, null)).toBeNull();
    expect(validateTargetScope('group-1', null)).toBeNull();
    expect(validateTargetScope(null, '["a@x.com"]')).toBeNull();
  });

  it('rejects group mode with no group picked', () => {
    expect(validateTargetScope('', null)).toBe(TARGET_SCOPE_MESSAGES.emptyGroup);
  });

  it('rejects specific mode with no users', () => {
    expect(validateTargetScope(null, '[]')).toBe(TARGET_SCOPE_MESSAGES.emptyUsers);
    expect(validateTargetScope(null, '[""]')).toBe(TARGET_SCOPE_MESSAGES.emptyUsers);
  });
});
```

Run: `cd frontend && npm test 2>&1 | tail -5; cd ..`
Expected: FAIL — `Failed to resolve import "./target-scope"`.

- [ ] **Step 7: Add `target-scope.ts` and use it in the wizard and the edit page**

Create `frontend/src/lib/target-scope.ts`:

```ts
/**
 * Phase 3 (§3.2): one definition of the tunnel target scope the wizard and the edit page share.
 *
 * The form state encodes the scope in two nullable fields: `targetGroupId` (non-null ⇒ group
 * mode; '' is the "group mode, nothing picked yet" sentinel) and `targetUserEmails` (non-null
 * JSON array ⇒ specific-users mode; '[]' is "specific mode, nobody picked yet"). Deriving the
 * <Select> value from *presence* (not truthiness) is what keeps the dropdown on "Security Group"
 * while the user is still picking a group. Validation mirrors the API's TargetScopeValidation.
 */
export type TargetScopeOption = 'all' | 'specific' | 'group';

export const TARGET_SCOPE_MESSAGES = {
  emptyUsers: 'Select at least one user, or switch scope to All Users.',
  emptyGroup: 'Select a security group, or switch scope to All Users.',
} as const;

export function deriveTargetScope(
  targetGroupId: string | null | undefined,
  targetUserEmails: string | null | undefined,
): TargetScopeOption {
  if (targetGroupId !== null && targetGroupId !== undefined) return 'group';
  if (targetUserEmails !== null && targetUserEmails !== undefined) return 'specific';
  return 'all';
}

export function parseTargetUserEmails(json: string | null | undefined): string[] {
  if (!json) return [];
  try {
    const parsed: unknown = JSON.parse(json);
    return Array.isArray(parsed)
      ? parsed.filter((e): e is string => typeof e === 'string' && e.trim() !== '')
      : [];
  } catch {
    return [];
  }
}

/** Returns the error to show, or null when the scope is saveable. */
export function validateTargetScope(
  targetGroupId: string | null | undefined,
  targetUserEmails: string | null | undefined,
): string | null {
  switch (deriveTargetScope(targetGroupId, targetUserEmails)) {
    case 'group':
      return (targetGroupId ?? '').trim() === '' ? TARGET_SCOPE_MESSAGES.emptyGroup : null;
    case 'specific':
      return parseTargetUserEmails(targetUserEmails).length === 0 ? TARGET_SCOPE_MESSAGES.emptyUsers : null;
    default:
      return null;
  }
}
```

In `frontend/src/components/wizard/StepTargets.tsx`, add the import `import { deriveTargetScope } from '@/lib/target-scope';` and replace

```tsx
          value={targetGroupId !== null ? 'group' : targetUserEmails !== null ? 'specific' : 'all'}
```

with

```tsx
          value={deriveTargetScope(targetGroupId, targetUserEmails)}
```

In `frontend/src/components/TunnelWizard.tsx`, add the import `import { validateTargetScope } from '@/lib/target-scope';` and replace, inside `validateStep`,

```ts
        case 2:
          if (formData.targetListIds.length === 0) {
            newErrors.targets = 'Select at least one phone list above.';
          }
          if (formData.targetUserEmails !== null) {
            const emails: string[] = JSON.parse(formData.targetUserEmails || '[]');
            if (emails.length === 0) {
              newErrors.targets = 'Select at least one user, or switch scope to All Users.';
            }
          }
          if (formData.targetGroupId !== null && !formData.targetGroupId) {
            newErrors.targets = 'Select a security group, or switch scope to All Users.';
          }
          break;
```

with

```ts
        case 2: {
          if (formData.targetListIds.length === 0) {
            newErrors.targets = 'Select at least one phone list above.';
          }
          const scopeError = validateTargetScope(formData.targetGroupId, formData.targetUserEmails);
          if (scopeError) {
            newErrors.targets = scopeError;
          }
          break;
        }
```

In `frontend/src/app/(app)/tunnels/[id]/page.tsx`:

1. Add the import `import { deriveTargetScope, validateTargetScope } from '@/lib/target-scope';` after the `@/types/common` import.

2. In `handleSave`, replace

```tsx
  const handleSave = () => {
    if (!tunnel) return;
    if (isHighImpactChange(tunnel, editForm)) {
```

with

```tsx
  const handleSave = () => {
    if (!tunnel) return;
    // Phase 3 (§3.2): the same scope validation the wizard runs — the API rejects these too.
    const scopeError = validateTargetScope(editForm.targetGroupId, editForm.targetUserEmails);
    if (scopeError) {
      toast.error(scopeError);
      return;
    }
    if (isHighImpactChange(tunnel, editForm)) {
```

3. Replace the scope `Select` value

```tsx
                    value={editForm.targetGroupId ? 'group' : editForm.targetUserEmails ? 'specific' : 'all'}
```

with

```tsx
                    value={deriveTargetScope(editForm.targetGroupId, editForm.targetUserEmails)}
```

4. In `doSave`, replace

```tsx
    const saveData = {
      ...editForm,
      targetGroupId: editForm.targetGroupId || null,
      targetGroupName: editForm.targetGroupName || null,
    };
```

with

```tsx
    // handleSave has already rejected an empty group id; nothing to coerce here any more.
    const saveData = { ...editForm };
```

- [ ] **Step 8: Run the frontend gate**

Run: `cd frontend && npm test 2>&1 | tail -4 && npm run build 2>&1 | tail -3; cd ..`
Expected: `Tests  16 passed (16)` (8 + 8); `✓ Compiled successfully`.

- [ ] **Step 9: Commit**

```bash
git add api/Services/TargetScopeValidation.cs api/Controllers/TunnelsController.cs tests/AFHSync.Tests.Unit/Api/TargetScopeValidationTests.cs tests/AFHSync.Tests.Integration/Api/TunnelsControllerTests.cs frontend/src/lib/target-scope.ts frontend/src/lib/target-scope.test.ts frontend/src/components/wizard/StepTargets.tsx frontend/src/components/TunnelWizard.tsx "frontend/src/app/(app)/tunnels/[id]/page.tsx"
git commit -m "feat(tunnels): validate target scope on the edit page and in the API (empty users / empty group ⇒ 400)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---
### Task 7: Graph pickers — members paging, security groups, `'` escaping, preview counts (§3.4)

**Files:**
- Create: `api/Services/GraphQuery.cs`, `api/Services/PageWindow.cs`
- Modify: `api/Controllers/GraphController.cs` (`GetDdgMembers`, `ListSecurityGroups`, `SearchUsers`)
- Modify: `api/Controllers/TunnelsController.cs` (`Preview`)
- Create: `tests/AFHSync.Tests.Unit/Api/GraphQueryTests.cs`, `tests/AFHSync.Tests.Unit/Api/PageWindowTests.cs`
- Modify: `frontend/src/lib/api.ts`, `frontend/src/app/(app)/lists/page.tsx`

**Interfaces:**
- Produces:
  ```csharp
  // api/Services/GraphQuery.cs
  public static class GraphQuery { public const int SecurityGroupCap = 2000; public static string EscapeLiteral(string value); }
  // api/Services/PageWindow.cs — feeds a forward-only iterator into a (page, pageSize) window
  public sealed class PageWindow<T>
  {
      public PageWindow(int page, int pageSize, int maxPageSize);
      public int Page { get; } public int PageSize { get; }
      public IReadOnlyList<T> Items { get; } public bool HasMore { get; }
      public bool Accept(T item);   // returns false once the window is full and one extra item was seen ⇒ stop iterating
      public PagedResult<T> ToResult();
  }
  // GET /api/graph/ddgs/{id}/members?page=&pageSize=   → PagedResult<DdgMemberDto>  (default 50, max 999); `top` query param removed
  // GET /api/graph/security-groups                     → SecurityGroupDto[] paged fully, capped at 2000
  // GET /api/graph/users/search?q=                     → single quotes in q are escaped (O'Brien works)
  // POST /api/tunnels/{id}/preview                     → DDG counts from @odata.count; mailbox_contacts counts page the folder (ContactFolderId honoured)
  ```
  ```ts
  api.ddgs.getMembers(id, page, pageSize): Promise<PagedResult<DdgMemberDto>>
  ```
- Consumes: `PagedResult<T>` (Task 5).

- [ ] **Step 1: Write the failing unit tests**

Create `tests/AFHSync.Tests.Unit/Api/GraphQueryTests.cs`:

```csharp
using AFHSync.Api.Services;

namespace AFHSync.Tests.Unit.Api;

public class GraphQueryTests
{
    [Theory]
    [InlineData("O'Brien", "O''Brien")]
    [InlineData("plain", "plain")]
    [InlineData("it''s", "it''''s")]
    [InlineData("", "")]
    public void EscapeLiteral_DoublesSingleQuotes(string input, string expected)
        => Assert.Equal(expected, GraphQuery.EscapeLiteral(input));
}
```

Create `tests/AFHSync.Tests.Unit/Api/PageWindowTests.cs`:

```csharp
using AFHSync.Api.Services;

namespace AFHSync.Tests.Unit.Api;

public class PageWindowTests
{
    private static (PageWindow<int> window, bool stoppedEarly) Feed(int page, int pageSize, int available)
    {
        var window = new PageWindow<int>(page, pageSize, maxPageSize: 999);
        for (var i = 1; i <= available; i++)
        {
            if (!window.Accept(i))
                return (window, true);
        }
        return (window, false);
    }

    [Fact]
    public void FirstPage_TakesPageSizeItems_AndStopsAfterOneExtra()
    {
        var (w, stopped) = Feed(page: 1, pageSize: 3, available: 10);

        Assert.Equal(new[] { 1, 2, 3 }, w.Items);
        Assert.True(w.HasMore);
        Assert.True(stopped);                  // iteration stopped at the 4th item, not the 10th
    }

    [Fact]
    public void LaterPage_SkipsEarlierItems()
    {
        var (w, _) = Feed(page: 3, pageSize: 3, available: 10);

        Assert.Equal(new[] { 7, 8, 9 }, w.Items);
        Assert.True(w.HasMore);
    }

    [Fact]
    public void LastPage_HasMoreFalse_WhenNothingFollows()
    {
        var (w, stopped) = Feed(page: 2, pageSize: 3, available: 6);

        Assert.Equal(new[] { 4, 5, 6 }, w.Items);
        Assert.False(w.HasMore);
        Assert.False(stopped);
    }

    [Fact]
    public void PageBeyondEnd_IsEmpty()
    {
        var (w, _) = Feed(page: 5, pageSize: 3, available: 6);

        Assert.Empty(w.Items);
        Assert.False(w.HasMore);
    }

    [Fact]
    public void Constructor_ClampsPageAndPageSize()
    {
        var w = new PageWindow<int>(page: 0, pageSize: 5000, maxPageSize: 999);
        Assert.Equal(1, w.Page);
        Assert.Equal(999, w.PageSize);

        var w2 = new PageWindow<int>(page: 2, pageSize: 0, maxPageSize: 999);
        Assert.Equal(1, w2.PageSize);
    }

    [Fact]
    public void ToResult_CarriesItemsAndHasMore()
    {
        var (w, _) = Feed(page: 1, pageSize: 2, available: 3);

        var result = w.ToResult();

        Assert.Equal(new[] { 1, 2 }, result.Items);
        Assert.True(result.HasMore);
        Assert.Null(result.Total);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~GraphQueryTests|FullyQualifiedName~PageWindowTests" 2>&1 | grep -E "error" | head -3`
Expected: build errors `'GraphQuery' does not exist` / `'PageWindow<>' could not be found`.

- [ ] **Step 3: Add the helpers**

Create `api/Services/GraphQuery.cs`:

```csharp
namespace AFHSync.Api.Services;

/// <summary>Phase 3 (§3.4): small helpers for building Graph OData queries safely.</summary>
public static class GraphQuery
{
    /// <summary>Upper bound on security groups listed by GET /api/graph/security-groups.</summary>
    public const int SecurityGroupCap = 2000;

    /// <summary>Escapes a value for use inside an OData string literal ('...'): a single quote becomes two.</summary>
    public static string EscapeLiteral(string value) => value.Replace("'", "''");
}
```

Create `api/Services/PageWindow.cs`:

```csharp
using AFHSync.Api.DTOs;

namespace AFHSync.Api.Services;

/// <summary>
/// Phase 3 (§3.4): serves a (page, pageSize) window from a forward-only source such as a Graph
/// PageIterator — Graph /users has no $skip, so the window skips (page-1)*pageSize items, keeps
/// pageSize, and asks the caller to stop as soon as it has seen one more (that extra item is
/// what makes <see cref="HasMore"/> true).
/// </summary>
public sealed class PageWindow<T>
{
    private readonly List<T> _items = [];
    private int _seen;

    public PageWindow(int page, int pageSize, int maxPageSize)
    {
        (Page, PageSize) = Paging.Clamp(page, pageSize, defaultSize: 1, max: maxPageSize);
    }

    public int Page { get; }
    public int PageSize { get; }
    public IReadOnlyList<T> Items => _items;
    public bool HasMore { get; private set; }

    private int Skip => (Page - 1) * PageSize;

    /// <summary>
    /// Offers the next item. Returns true to continue iterating; false once the window is full
    /// and one extra item has been seen (the caller should stop fetching pages).
    /// </summary>
    public bool Accept(T item)
    {
        _seen++;
        if (_seen <= Skip)
            return true;
        if (_items.Count < PageSize)
        {
            _items.Add(item);
            return true;
        }
        HasMore = true;
        return false;
    }

    public PagedResult<T> ToResult() => new(_items, HasMore);
}
```

- [ ] **Step 4: Page DDG members, page security groups fully, escape the user search**

In `api/Controllers/GraphController.cs`, replace the whole `GetDdgMembers` action with:

```csharp
    /// <summary>
    /// GET /api/graph/ddgs/{id}/members?page=1&amp;pageSize=50 - Members of a DDG via Graph.
    /// Uses the converted $filter to query users from Microsoft Graph, paging through Graph
    /// (which has no $skip for /users) with a PageWindow (§3.4). Max pageSize 999.
    /// </summary>
    [HttpGet("ddgs/{id}/members")]
    public async Task<ActionResult<PagedResult<DdgMemberDto>>> GetDdgMembers(
        string id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var ddg = await _ddgResolver.GetDdgAsync(id, ct);
        if (ddg == null)
            return NotFound(new { message = $"DDG not found: {id}" });

        var window = new PageWindow<DdgMemberDto>(page, pageSize, maxPageSize: 999);

        var conversion = _filterConverter.Convert(ddg.RecipientFilter);
        if (!conversion.Success || string.IsNullOrWhiteSpace(conversion.Filter))
        {
            Response.Headers.Append("X-Filter-Warning",
                conversion.Warning ?? "Filter conversion failed");
            return Ok(window.ToResult());
        }

        try
        {
            var response = await _graphClient.Users.GetAsync(config =>
            {
                config.QueryParameters.Filter = conversion.Filter;
                config.QueryParameters.Top = 999;
                config.QueryParameters.Select =
                    ["id", "displayName", "mail", "jobTitle", "department", "officeLocation"];
                config.QueryParameters.Orderby = ["displayName"];
                config.Headers.Add("ConsistencyLevel", "eventual");
                config.QueryParameters.Count = true;
            }, ct);

            if (response?.Value != null)
            {
                var iterator = PageIterator<User, UserCollectionResponse>
                    .CreatePageIterator(_graphClient, response, u => window.Accept(new DdgMemberDto(
                        Id: u.Id ?? string.Empty,
                        DisplayName: u.DisplayName ?? string.Empty,
                        Email: u.Mail,
                        JobTitle: u.JobTitle,
                        Department: u.Department,
                        OfficeLocation: u.OfficeLocation)),
                    req =>
                    {
                        req.Headers.Add("ConsistencyLevel", "eventual");
                        return req;
                    });
                await iterator.IterateAsync(ct);
            }

            return Ok(window.ToResult());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Graph member query failed for DDG {DdgId} with filter: {Filter}",
                id, conversion.Filter);
            Response.Headers.Append("X-Filter-Warning",
                $"Graph query failed: {ex.Message}");
            return Ok(new PagedResult<DdgMemberDto>([], false));
        }
    }
```

Replace the body of the `try` in `ListSecurityGroups` (everything between `try` `{` and the matching `}` before `catch`) with:

```csharp
            var result = new List<SecurityGroupDto>();
            var response = await _graphClient.Groups.GetAsync(config =>
            {
                config.QueryParameters.Filter = "securityEnabled eq true and mailEnabled eq false";
                config.QueryParameters.Select = ["id", "displayName", "description", "membershipRule"];
                config.QueryParameters.Top = 999;
                config.QueryParameters.Orderby = ["displayName"];
                config.QueryParameters.Count = true;
                config.Headers.Add("ConsistencyLevel", "eventual");
            }, ct);

            // Phase 3 (§3.4): page through every group instead of returning the first 200,
            // capped at GraphQuery.SecurityGroupCap so a huge tenant can't stall the picker.
            if (response?.Value != null)
            {
                var iterator = PageIterator<Group, GroupCollectionResponse>
                    .CreatePageIterator(_graphClient, response, g =>
                    {
                        result.Add(new SecurityGroupDto(
                            g.Id ?? string.Empty,
                            g.DisplayName ?? string.Empty,
                            g.Description,
                            g.MembershipRule));
                        return result.Count < GraphQuery.SecurityGroupCap;
                    },
                    req =>
                    {
                        req.Headers.Add("ConsistencyLevel", "eventual");
                        return req;
                    });
                await iterator.IterateAsync(ct);
                if (result.Count >= GraphQuery.SecurityGroupCap)
                    _logger.LogWarning("Security group listing hit the cap of {Cap}; the picker is truncated", GraphQuery.SecurityGroupCap);
            }

            return Ok(result.ToArray());
```

In `SearchUsers`, replace

```csharp
                config.QueryParameters.Filter =
                    $"startsWith(displayName,'{q}') or startsWith(mail,'{q}')";
```

with

```csharp
                var escaped = GraphQuery.EscapeLiteral(q);
                config.QueryParameters.Filter =
                    $"startsWith(displayName,'{escaped}') or startsWith(mail,'{escaped}')";
```

- [ ] **Step 5: Accurate impact-preview counts**

In `api/Controllers/TunnelsController.cs`, `Preview`, replace the `mailbox_contacts` branch

```csharp
                    if (src.SourceType == "mailbox_contacts")
                    {
                        // Count contacts in the shared mailbox
                        var contactsPage = await _graphClient.Users[src.SourceIdentifier].Contacts.GetAsync(cfg =>
                        {
                            cfg.QueryParameters.Select = ["id"];
                            cfg.QueryParameters.Top = 999;
                        });
                        totalNewCount += contactsPage?.Value?.Count ?? 0;
                    }
```

with

```csharp
                    if (src.SourceType == "mailbox_contacts")
                    {
                        // Phase 3 (§3.4): count every page of the configured folder (root when none).
                        totalNewCount += await CountMailboxContactsAsync(src.SourceIdentifier, src.ContactFolderId, HttpContext.RequestAborted);
                    }
```

and the DDG branch's

```csharp
                        totalNewCount += usersPage?.Value?.Count ?? 0;
```

with

```csharp
                        // Phase 3 (§3.4): @odata.count is the tenant-wide match count; Value.Count was one page.
                        totalNewCount += (int?)usersPage?.OdataCount ?? usersPage?.Value?.Count ?? 0;
```

Then add this private method to the controller (after `Preview`):

```csharp
    /// <summary>
    /// Counts the contacts in a mailbox's contact folder (root Contacts when <paramref name="contactFolderId"/>
    /// is null) by paging Graph — personal contacts do not support $count.
    /// </summary>
    private async Task<int> CountMailboxContactsAsync(string mailbox, string? contactFolderId, CancellationToken ct)
    {
        Microsoft.Graph.Models.ContactCollectionResponse? response;
        if (!string.IsNullOrEmpty(contactFolderId))
        {
            response = await _graphClient.Users[mailbox].ContactFolders[contactFolderId].Contacts.GetAsync(cfg =>
            {
                cfg.QueryParameters.Select = ["id"];
                cfg.QueryParameters.Top = 999;
            }, ct);
        }
        else
        {
            response = await _graphClient.Users[mailbox].Contacts.GetAsync(cfg =>
            {
                cfg.QueryParameters.Select = ["id"];
                cfg.QueryParameters.Top = 999;
            }, ct);
        }

        if (response?.Value is null)
            return 0;

        var count = 0;
        var iterator = PageIterator<Microsoft.Graph.Models.Contact, Microsoft.Graph.Models.ContactCollectionResponse>
            .CreatePageIterator(_graphClient, response, _ => { count++; return true; });
        await iterator.IterateAsync(ct);
        return count;
    }
```

- [ ] **Step 6: Run the backend tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 301, Skipped: 1` (291 + 4 + 6).

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 44, Skipped: 1` (unchanged — these controllers hit Graph, which the test host cannot reach).

- [ ] **Step 7: Frontend — members envelope**

In `frontend/src/lib/api.ts` change

```ts
    getMembers: (id: string, page: number, pageSize: number) =>
      fetchApi<DdgMemberDto[]>(`/graph/ddgs/${id}/members?page=${page}&pageSize=${pageSize}`),
```

to

```ts
    getMembers: (id: string, page: number, pageSize: number) =>
      fetchApi<PagedResult<DdgMemberDto>>(`/graph/ddgs/${id}/members?page=${page}&pageSize=${pageSize}`),
```

In `frontend/src/app/(app)/lists/page.tsx`, in `EmailSearchPicker.handleAddGroup`, replace

```tsx
      const members = await api.ddgs.getMembers(groupEmail, 1, 999);
      const newEmails = members
        .map((m) => m.email)
```

with

```tsx
      // Phase 3 (§3.4): the endpoint pages; walk every page (a DDG can exceed one Graph page).
      const members: DdgMemberDto[] = [];
      let page = 1;
      let hasMore = true;
      while (hasMore && page <= 20) {
        const result = await api.ddgs.getMembers(groupEmail, page, 999);
        members.push(...result.items);
        hasMore = result.hasMore;
        page += 1;
      }
      const newEmails = members
        .map((m) => m.email)
```

and add `import type { DdgMemberDto } from '@/types/ddg';` to the file's imports.

Run: `cd frontend && npm run build 2>&1 | tail -3; cd ..`
Expected: `✓ Compiled successfully`.

- [ ] **Step 8: Commit**

```bash
git add api/Services/GraphQuery.cs api/Services/PageWindow.cs api/Controllers/GraphController.cs api/Controllers/TunnelsController.cs tests/AFHSync.Tests.Unit/Api/GraphQueryTests.cs tests/AFHSync.Tests.Unit/Api/PageWindowTests.cs frontend/src/lib/api.ts "frontend/src/app/(app)/lists/page.tsx"
git commit -m "feat(graph): page DDG members and security groups, escape quotes in user search, count previews via @odata.count and folder paging

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: Contact Filters — folder-aware mailbox resolution and atomic de-duplicated replace (§3.5)

**Files:**
- Modify: `api/Controllers/ContactExclusionsController.cs`
- Create: `tests/AFHSync.Tests.Integration/Api/ContactExclusionsControllerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // private: ResolveMailboxContactsAsync(string mailboxEmail, string? contactFolderId, CancellationToken ct)  — pages via PageIterator
  // PUT /api/tunnels/{tunnelId}/contact-exclusions: replaces all rows in ONE SaveChangesAsync (one transaction),
  //   de-duplicated by EntraId (case-insensitive, blank ids dropped); message "Saved {n} exclusion(s)." reports the de-duplicated count.
  ```
- Consumes: nothing from earlier tasks.

- [ ] **Step 1: Write the failing integration test**

Create `tests/AFHSync.Tests.Integration/Api/ContactExclusionsControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AFHSync.Tests.Integration.Api;

/// <summary>Phase 3 (§3.5): the exclusion replace is atomic and de-duplicated.</summary>
[Trait("Category", "Integration")]
public class ContactExclusionsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ContactExclusionsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private async Task<string> GetAuthCookieAsync()
    {
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        loginResponse.EnsureSuccessStatusCode();
        return loginResponse.Headers.GetValues("Set-Cookie").First().Split(';')[0];
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Cookie", await GetAuthCookieAsync());
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task PutExclusions_DedupesByEntraIdCaseInsensitively_AndReplacesPreviousSet()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        var tunnel = new Tunnel { Name = "Exclusions Tunnel", StalePolicy = StalePolicy.FlagHold, Status = TunnelStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Tunnels.Add(tunnel);
        await db.SaveChangesAsync();

        var first = await SendAsync(HttpMethod.Put, $"/api/tunnels/{tunnel.Id}/contact-exclusions", new
        {
            exclusions = new[]
            {
                new { entraId = "AAA-1", displayName = "Alice", email = "alice@contoso.com" },
                new { entraId = "aaa-1", displayName = "Alice again", email = "alice@contoso.com" },   // duplicate, different case
                new { entraId = "BBB-2", displayName = "Bob", email = "bob@contoso.com" },
                new { entraId = "", displayName = "Blank", email = (string?)null },                    // dropped
            }
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Saved 2 exclusion(s).", firstBody.GetProperty("message").GetString());

        var afterFirst = await (await SendAsync(HttpMethod.Get, $"/api/tunnels/{tunnel.Id}/contact-exclusions")).Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
        Assert.Equal(2, afterFirst!.Count);

        var second = await SendAsync(HttpMethod.Put, $"/api/tunnels/{tunnel.Id}/contact-exclusions", new
        {
            exclusions = new[] { new { entraId = "CCC-3", displayName = "Cara", email = "cara@contoso.com" } }
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var afterSecond = await (await SendAsync(HttpMethod.Get, $"/api/tunnels/{tunnel.Id}/contact-exclusions")).Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
        var only = Assert.Single(afterSecond!);
        Assert.Equal("CCC-3", only.GetProperty("entraId").GetString());
    }

    [Fact]
    public async Task PutExclusions_UnknownTunnel_Returns404()
    {
        var response = await SendAsync(HttpMethod.Put, "/api/tunnels/99999/contact-exclusions", new { exclusions = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet --filter "FullyQualifiedName~ContactExclusionsControllerTests" 2>&1 | tail -6`
Expected: `PutExclusions_DedupesByEntraId…` fails — the current `ExecuteDeleteAsync` is not supported by the InMemory provider (`Expected OK, Actual InternalServerError`) or, if it were, `Saved 4 exclusion(s).`; `PutExclusions_UnknownTunnel_Returns404` passes already.

- [ ] **Step 3: Make the replace atomic and de-duplicated; honour the contact folder**

In `api/Controllers/ContactExclusionsController.cs`, replace the body of `UpdateExclusions` after the 404 check with:

```csharp
        // Phase 3 (§3.5): delete + insert in ONE SaveChangesAsync (one transaction on Postgres),
        // de-duplicated by EntraId so a doubled-up request can't insert the same contact twice.
        var existing = await _db.TunnelContactExclusions
            .Where(e => e.TunnelId == tunnelId)
            .ToListAsync();
        _db.TunnelContactExclusions.RemoveRange(existing);

        var distinct = (request.Exclusions ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.EntraId))
            .DistinctBy(e => e.EntraId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var now = DateTime.UtcNow;
        foreach (var exclusion in distinct)
        {
            _db.TunnelContactExclusions.Add(new TunnelContactExclusion
            {
                TunnelId = tunnelId,
                EntraId = exclusion.EntraId.Trim(),
                DisplayName = exclusion.DisplayName,
                Email = exclusion.Email,
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync();

        return Ok(new { message = $"Saved {distinct.Count} exclusion(s)." });
```

In `ResolveContacts`, change the `MailboxContacts` case's call

```csharp
                        var contacts = await ResolveMailboxContactsAsync(source.SourceIdentifier, ct);
```

to

```csharp
                        var contacts = await ResolveMailboxContactsAsync(source.SourceIdentifier, source.ContactFolderId, ct);
```

and replace the whole `ResolveMailboxContactsAsync` helper with:

```csharp
    /// <summary>
    /// Phase 3 (§3.5): reads the configured contact folder (root Contacts when none) and pages
    /// through it, so a Contact Filters list for a subfolder source shows that subfolder.
    /// </summary>
    private async Task<List<(string entraId, string? displayName, string? email, string? companyName, string? jobTitle)>>
        ResolveMailboxContactsAsync(string mailboxEmail, string? contactFolderId, CancellationToken ct)
    {
        var results = new List<(string, string?, string?, string?, string?)>();
        var select = new[] { "id", "displayName", "emailAddresses", "companyName", "jobTitle" };

        ContactCollectionResponse? response;
        if (!string.IsNullOrEmpty(contactFolderId))
        {
            response = await _graphClient.Users[mailboxEmail].ContactFolders[contactFolderId].Contacts.GetAsync(config =>
            {
                config.QueryParameters.Select = select;
                config.QueryParameters.Top = 999;
            }, ct);
        }
        else
        {
            response = await _graphClient.Users[mailboxEmail].Contacts.GetAsync(config =>
            {
                config.QueryParameters.Select = select;
                config.QueryParameters.Top = 999;
            }, ct);
        }

        if (response?.Value != null)
        {
            var pageIterator = PageIterator<Contact, ContactCollectionResponse>
                .CreatePageIterator(_graphClient, response, contact =>
                {
                    var email = contact.EmailAddresses?.FirstOrDefault()?.Address;
                    results.Add((contact.Id ?? "", contact.DisplayName, email, contact.CompanyName, contact.JobTitle));
                    return true;
                });
            await pageIterator.IterateAsync(ct);
        }
        return results;
    }
```

(`Contact` and `ContactCollectionResponse` come from the already-imported `Microsoft.Graph.Models`.)

- [ ] **Step 4: Run the integration tests**

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 46, Skipped: 1` (44 + 2).

- [ ] **Step 5: Commit**

```bash
git add api/Controllers/ContactExclusionsController.cs tests/AFHSync.Tests.Integration/Api/ContactExclusionsControllerTests.cs
git commit -m "fix(api): Contact Filters resolve the configured folder with paging; exclusion replace is atomic and de-duplicated

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---
### Task 9: Lifetimes and hygiene — Exchange session, cleanup retry attribute, target refresh memo, shared lock key (§3.6, §3.8 lock)

**Files:**
- Rewrite: `api/Services/DDGResolver.cs`
- Modify: `api/Program.cs`, `worker/Program.cs` (`IDDGResolver` → Singleton)
- Modify: `shared/Services/ICleanupJobRunner.cs`, `worker/Services/CleanupJobRunner.cs` (attribute moves to the interface)
- Create: `shared/Services/RunLocks.cs`
- Modify: `api/Controllers/SyncRunsController.cs` (`TriggerSync` lock SQL), `worker/Services/RunClaimService.cs`
- Modify: `worker/Services/SyncEngine.cs` (`RunAsync`, `LoadTargetMailboxesAsync`, new `EnsureTargetMailboxesRefreshedAsync`)
- Modify: `tests/AFHSync.Tests.Unit/Api/DDGResolverTests.cs`, `tests/AFHSync.Tests.Unit/Sync/CleanupJobRunnerTests.cs`, `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`
- Create: `tests/AFHSync.Tests.Unit/Sync/RunLocksTests.cs`, `tests/AFHSync.Tests.Integration/DiLifetimeTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // api/Services/DDGResolver.cs
  public static bool DDGResolver.IsSessionError(string? errors, Exception? exception = null);
  // shared/Services/RunLocks.cs
  public static class RunLocks { public const int RunStartAdvisoryKey = 1; public const string AcquireRunStartLockSql = "SELECT pg_advisory_xact_lock(1)"; }
  // shared/Services/ICleanupJobRunner.cs — [AutomaticRetry(Attempts = 0)] on RunAsync (the interface method Hangfire dispatches)
  // worker SyncEngine
  internal int TargetMailboxRefreshAttempts { get; }     // test hook: how many times the run tried the tenant enumeration (memoised ⇒ ≤ 1)
  ```
  DI: `IDDGResolver` is a Singleton in both hosts.
- Consumes: nothing from earlier tasks.

- [ ] **Step 1: Write the failing unit tests**

In `tests/AFHSync.Tests.Unit/Api/DDGResolverTests.cs`, add after `DdgInfo_Record_HasRequiredProperties`:

```csharp
    [Theory]
    [InlineData("The session has been closed by the server.")]
    [InlineData("Access token has expired or is not yet valid")]
    [InlineData("Cannot find the PSSession Exchange.Runspace")]
    public void IsSessionError_RecognisesSessionAndTokenFailures(string errors)
        => Assert.True(DDGResolver.IsSessionError(errors));

    [Fact]
    public void IsSessionError_RecognisesUnauthorizedAccessException()
        => Assert.True(DDGResolver.IsSessionError(null, new UnauthorizedAccessException("denied")));

    [Theory]
    [InlineData("The operation couldn't be performed because object 'nope' couldn't be found on 'DM6PR...'.")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSessionError_IgnoresOrdinaryFailures(string? errors)
        => Assert.False(DDGResolver.IsSessionError(errors));
```

In `tests/AFHSync.Tests.Unit/Sync/CleanupJobRunnerTests.cs`, add `using System.Reflection;` to the usings and this test at the end of the class:

```csharp
    // ============================================================
    // Phase 3 (3.6): Hangfire dispatches ICleanupJobRunner.RunAsync — the attribute must be there
    // ============================================================

    [Fact]
    public void RunAsync_InterfaceMethod_DisablesHangfireAutomaticRetry()
    {
        var method = typeof(ICleanupJobRunner).GetMethod(nameof(ICleanupJobRunner.RunAsync))!;

        var attr = method.GetCustomAttribute<Hangfire.AutomaticRetryAttribute>();

        Assert.NotNull(attr);
        Assert.Equal(0, attr!.Attempts);
    }
```

Create `tests/AFHSync.Tests.Unit/Sync/RunLocksTests.cs`:

```csharp
using AFHSync.Shared.Services;

namespace AFHSync.Tests.Unit.Sync;

public class RunLocksTests
{
    [Fact]
    public void AcquireRunStartLockSql_UsesTheSharedKey()
        => Assert.Equal($"SELECT pg_advisory_xact_lock({RunLocks.RunStartAdvisoryKey})", RunLocks.AcquireRunStartLockSql);
}
```

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`, add before the `// Stub implementations` banner:

```csharp
    // ==============================
    // Phase 3 (3.6): the tenant enumeration runs once per run, and group scope uses it too
    // ==============================

    [Fact]
    public async Task RunAsync_TwoAllUsersTunnels_RefreshTargetMailboxesOnce()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
            var t1 = new Tunnel { Id = 1, Name = "T1", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
            var t2 = new Tunnel { Id = 2, Name = "T2", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
            var tpl1 = new TunnelPhoneList { TunnelId = 1, PhoneListId = 1, Tunnel = t1, PhoneList = phoneList };
            var tpl2 = new TunnelPhoneList { TunnelId = 2, PhoneListId = 1, Tunnel = t2, PhoneList = phoneList };
            t1.TunnelPhoneLists.Add(tpl1);
            t2.TunnelPhoneLists.Add(tpl2);
            seedCtx.Tunnels.AddRange(t1, t2);
            seedCtx.PhoneLists.Add(phoneList);
            seedCtx.TunnelPhoneLists.AddRange(tpl1, tpl2);
            seedCtx.TargetMailboxes.Add(new TargetMailbox { Id = 1, EntraId = "mb-1", Email = "one@contoso.com", IsActive = true });
            await seedCtx.SaveChangesAsync();
        }
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]));

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(1, engine.TargetMailboxRefreshAttempts);
    }

    [Fact]
    public async Task RunAsync_GroupScopedTunnel_AlsoRefreshesTargetMailboxes()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
            var tunnel = new Tunnel { Id = 1, Name = "Group Tunnel", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove,
                TargetGroupId = "group-1", TargetGroupName = "Buckhead Agents" };
            var tpl = new TunnelPhoneList { TunnelId = 1, PhoneListId = 1, Tunnel = tunnel, PhoneList = phoneList };
            tunnel.TunnelPhoneLists.Add(tpl);
            seedCtx.Tunnels.Add(tunnel);
            seedCtx.PhoneLists.Add(phoneList);
            seedCtx.TunnelPhoneLists.Add(tpl);
            await seedCtx.SaveChangesAsync();
        }
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]));

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(1, engine.TargetMailboxRefreshAttempts);   // was 0: group scope never enumerated the tenant
    }
```

Create `tests/AFHSync.Tests.Integration/DiLifetimeTests.cs`:

```csharp
using AFHSync.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AFHSync.Tests.Integration;

/// <summary>Phase 3 (§3.6): one Exchange Online session per process, not one per request.</summary>
[Trait("Category", "Integration")]
public class DiLifetimeTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public DiLifetimeTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void DdgResolver_IsASingleton()
    {
        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();

        var a = scope1.ServiceProvider.GetRequiredService<IDDGResolver>();
        var b = scope2.ServiceProvider.GetRequiredService<IDDGResolver>();

        Assert.Same(a, b);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~DDGResolverTests|FullyQualifiedName~RunLocksTests|FullyQualifiedName~CleanupJobRunnerTests.RunAsync_InterfaceMethod|FullyQualifiedName~RefreshTargetMailboxesOnce|FullyQualifiedName~AlsoRefreshesTargetMailboxes" 2>&1 | grep -E "error" | head -4`
Expected: build errors for `IsSessionError`, `RunLocks` and `TargetMailboxRefreshAttempts`.

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet --filter "FullyQualifiedName~DiLifetimeTests" 2>&1 | tail -4`
Expected: FAIL `Assert.Same() Failure` (Scoped today).

- [ ] **Step 3: Rewrite `DDGResolver`**

Replace the whole of `api/Services/DDGResolver.cs` with:

```csharp
namespace AFHSync.Api.Services;

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

/// <summary>
/// Resolves Dynamic Distribution Groups from Exchange Online via a PowerShell runspace.
/// Per D-01: System.Management.Automation invokes Exchange Online PowerShell.
/// Per D-02: certificate-based app-only auth (Exchange.ManageAsApp role).
///
/// Phase 3 (§3.6): registered as a Singleton in both api and worker — one Exchange session per
/// process instead of one connect per request. A failed Connect-ExchangeOnline disposes the
/// runspace so the next call reconnects from scratch; a command that fails with a session/auth
/// error tears the runspace down and retries exactly once; Dispose runs Disconnect-ExchangeOnline.
/// All Exchange calls are serialised by <see cref="_lock"/>.
/// </summary>
public class DDGResolver : IDDGResolver, IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<DDGResolver> _logger;
    private Runspace? _runspace;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DDGResolver(IConfiguration config, ILogger<DDGResolver> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DdgInfo>> ListDdgsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _logger.LogInformation("Listing all Dynamic Distribution Groups from Exchange Online");
            var (results, errors) = await InvokeWithSessionRetryAsync(ps =>
            {
                ps.AddCommand("Get-DynamicDistributionGroup");
                ps.AddParameter("ResultSize", "Unlimited");
            }, ct);

            if (errors is not null)
            {
                _logger.LogError("Exchange DDG listing failed: {Errors}", errors);
                throw new InvalidOperationException($"Exchange DDG listing failed: {errors}");
            }

            var ddgs = results.Select(ExtractDdgInfo).ToList();
            _logger.LogInformation("Retrieved {Count} Dynamic Distribution Groups", ddgs.Count);
            return ddgs;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DdgInfo?> GetDdgAsync(string identity, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _logger.LogInformation("Getting DDG details for: {Identity}", identity);
            var (results, errors) = await InvokeWithSessionRetryAsync(ps =>
            {
                ps.AddCommand("Get-DynamicDistributionGroup");
                ps.AddParameter("Identity", identity);
            }, ct);

            if (errors is not null)
            {
                // Check if this is a "not found" error
                if (errors.Contains("couldn't be found", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("DDG not found: {Identity}", identity);
                    return null;
                }

                _logger.LogError("Exchange DDG lookup failed for {Identity}: {Errors}", identity, errors);
                throw new InvalidOperationException($"Exchange DDG lookup failed: {errors}");
            }

            var result = results.FirstOrDefault();
            if (result == null)
            {
                _logger.LogWarning("DDG not found: {Identity}", identity);
                return null;
            }

            return ExtractDdgInfo(result);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Phase 3 (§3.6): the failures Exchange reports when the remote session was torn down or the
    /// app token expired — the runspace is useless and must be rebuilt.
    /// </summary>
    public static bool IsSessionError(string? errors, Exception? exception = null)
    {
        if (exception is UnauthorizedAccessException)
            return true;

        var text = errors ?? exception?.Message;
        if (string.IsNullOrEmpty(text))
            return false;

        return text.Contains("session", StringComparison.OrdinalIgnoreCase)
            || text.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs one command on the shared runspace. If it fails with a session/auth error the runspace
    /// is disposed and the command retried exactly once on a fresh connection. Caller holds _lock.
    /// Returns the results and, when the pipeline reported errors, their joined text.
    /// </summary>
    private async Task<(Collection<PSObject> Results, string? Errors)> InvokeWithSessionRetryAsync(
        Action<PowerShell> build, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var ps = GetOrCreatePowerShell();
            build(ps);

            Collection<PSObject> results;
            string? errors = null;
            try
            {
                results = await Task.Run(() => ps.Invoke(), ct);
                if (ps.HadErrors)
                    errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt == 1 && IsSessionError(null, ex))
            {
                _logger.LogWarning(ex, "Exchange Online session error — resetting the runspace and retrying once");
                ResetRunspace();
                continue;
            }

            if (errors is not null && attempt == 1 && IsSessionError(errors))
            {
                _logger.LogWarning("Exchange Online session error ({Errors}) — resetting the runspace and retrying once", errors);
                ResetRunspace();
                continue;
            }

            return (results, errors);
        }
    }

    /// <summary>
    /// Creates or reuses a PowerShell runspace connected to Exchange Online.
    /// Uses certificate-based auth with Exchange.ManageAsApp application role.
    /// </summary>
    private PowerShell GetOrCreatePowerShell()
    {
        if (_runspace == null || _runspace.RunspaceStateInfo.State != RunspaceState.Opened)
        {
            _logger.LogInformation("Creating new Exchange Online PowerShell runspace");

            var iss = InitialSessionState.CreateDefault();
            iss.ImportPSModule(["ExchangeOnlineManagement"]);
            if (OperatingSystem.IsWindows())
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.RemoteSigned;

            var runspace = RunspaceFactory.CreateRunspace(iss);
            try
            {
                runspace.Open();
                ConnectExchangeOnline(runspace);
            }
            catch
            {
                // Phase 3 (§3.6): never keep an opened-but-unconnected runspace — every later
                // command would run against it and fail. Dispose so the next call reconnects.
                runspace.Dispose();
                throw;
            }

            _runspace?.Dispose();
            _runspace = runspace;
            _logger.LogInformation("Connected to Exchange Online successfully");
        }

        var ps = PowerShell.Create(_runspace);
        ps.Commands.Clear();
        return ps;
    }

    private void ConnectExchangeOnline(Runspace runspace)
    {
        using var connectPs = PowerShell.Create(runspace);
        var connectCmd = connectPs.AddCommand("Connect-ExchangeOnline");

        var certPath = _config["Exchange:CertificatePath"];
        var certThumbprint = _config["Exchange:CertificateThumbprint"];

        if (!string.IsNullOrEmpty(certPath))
        {
            connectCmd.AddParameter("CertificateFilePath", certPath);
        }
        else if (!string.IsNullOrEmpty(certThumbprint))
        {
            connectCmd.AddParameter("CertificateThumbprint", certThumbprint);
        }
        else
        {
            throw new InvalidOperationException(
                "Exchange:CertificatePath or Exchange:CertificateThumbprint must be configured");
        }

        connectCmd.AddParameter("AppID", _config["Exchange:AppId"]);
        connectCmd.AddParameter("Organization", _config["Exchange:Organization"]);
        connectCmd.AddParameter("ShowBanner", false);

        connectPs.Invoke();

        if (connectPs.HadErrors)
        {
            var errors = string.Join("; ", connectPs.Streams.Error.Select(e => e.ToString()));
            _logger.LogError("Exchange Online connection failed: {Errors}", errors);
            throw new InvalidOperationException($"Exchange Online connection failed: {errors}");
        }
    }

    private void ResetRunspace()
    {
        DisconnectQuietly();
        _runspace?.Dispose();
        _runspace = null;
    }

    /// <summary>Best-effort Disconnect-ExchangeOnline so the tenant-side session is released.</summary>
    private void DisconnectQuietly()
    {
        if (_runspace is null || _runspace.RunspaceStateInfo.State != RunspaceState.Opened)
            return;
        try
        {
            using var ps = PowerShell.Create(_runspace);
            ps.AddCommand("Disconnect-ExchangeOnline").AddParameter("Confirm", false);
            ps.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disconnect-ExchangeOnline failed (ignored)");
        }
    }

    /// <summary>
    /// Extracts DDG info from a PowerShell PSObject result.
    /// </summary>
    private static DdgInfo ExtractDdgInfo(PSObject result) => new(
        Id: result.Properties["Guid"]?.Value?.ToString() ?? string.Empty,
        DisplayName: result.Properties["DisplayName"]?.Value?.ToString() ?? string.Empty,
        PrimarySmtpAddress: result.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? string.Empty,
        RecipientFilter: result.Properties["RecipientFilter"]?.Value?.ToString() ?? string.Empty
    );

    public void Dispose()
    {
        DisconnectQuietly();
        _runspace?.Dispose();
        _runspace = null;
        _lock.Dispose();
    }
}
```

- [ ] **Step 4: Singleton registrations**

In `api/Program.cs` replace

```csharp
builder.Services.AddScoped<IDDGResolver, DDGResolver>();
```

with

```csharp
// Phase 3 (§3.6): one Exchange Online session per process (the resolver serialises its own calls).
builder.Services.AddSingleton<IDDGResolver, DDGResolver>();
```

In `worker/Program.cs` replace

```csharp
    // Lifetimes mirror api/Program.cs:109-110 — Scoped resolver, Singleton converter.
    services.AddScoped<AFHSync.Api.Services.IDDGResolver, AFHSync.Api.Services.DDGResolver>();
```

with

```csharp
    // Lifetimes mirror api/Program.cs — Singleton resolver (one Exchange session per process,
    // Phase 3 §3.6) and Singleton converter.
    services.AddSingleton<AFHSync.Api.Services.IDDGResolver, AFHSync.Api.Services.DDGResolver>();
```

- [ ] **Step 5: Move `[AutomaticRetry]` to the interface**

In `shared/Services/ICleanupJobRunner.cs`, add `using Hangfire;` at the top and put the attribute on the interface method:

```csharp
    [AutomaticRetry(Attempts = 0)]
    Task RunAsync(Guid jobId, CleanupJobItem[] items, CancellationToken ct);
```

with the doc comment gaining the line `/// Phase 3 (§3.6): Hangfire dispatches the INTERFACE method (the API enqueues via Enqueue&lt;ICleanupJobRunner&gt;), so the retry-off attribute lives here, not on the class.`

In `worker/Services/CleanupJobRunner.cs`, delete the line `    [AutomaticRetry(Attempts = 0)]` above `public async Task RunAsync(` and delete the now-unused `using Hangfire;`.

- [ ] **Step 6: One advisory-lock key**

Create `shared/Services/RunLocks.cs`:

```csharp
namespace AFHSync.Shared.Services;

/// <summary>
/// Phase 3 (§3.8): the ONE Postgres advisory-lock key that serialises "may a run start?". The
/// API's trigger guard (check for Pending/Running, insert Pending) and the worker's
/// RunClaimService (claim or create) must take the same transaction-scoped lock — with different
/// keys a cron claim could slip between the API's check and its insert.
/// </summary>
public static class RunLocks
{
    public const int RunStartAdvisoryKey = 1;
    public const string AcquireRunStartLockSql = "SELECT pg_advisory_xact_lock(1)";
}
```

In `api/Controllers/SyncRunsController.cs`, `TriggerSync`, replace

```csharp
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(2)");
```

with

```csharp
            await db.Database.ExecuteSqlRawAsync(RunLocks.AcquireRunStartLockSql);
```

In `worker/Services/RunClaimService.cs`, add `using AFHSync.Shared.Services;`, replace the comment line `// Advisory lock key 1 = sync run start serialisation. Postgres-specific and` with `// RunLocks.RunStartAdvisoryKey serialises run start with the API's trigger guard. Postgres-specific and`, and replace

```csharp
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1)", ct);
```

with

```csharp
            await db.Database.ExecuteSqlRawAsync(RunLocks.AcquireRunStartLockSql, ct);
```

- [ ] **Step 7: Memoise the target-mailbox refresh and use it for group scope**

In `worker/Services/SyncEngine.cs`, after `internal const string WorkerShutdownReason = "worker shutting down";` add:

```csharp

    /// <summary>Phase 3 (§3.6): the tenant enumeration (Graph /users) runs at most once per run.</summary>
    private bool _targetMailboxesRefreshed;

    /// <summary>Test hook: how many times this run attempted the tenant enumeration (memoised ⇒ 0 or 1).</summary>
    internal int TargetMailboxRefreshAttempts { get; private set; }
```

In `RunAsync`, directly after `contactFolderManager.ResetCache();` add:

```csharp
        _targetMailboxesRefreshed = false;
```

In `LoadTargetMailboxesAsync`, replace the group-scope block

```csharp
        if (!string.IsNullOrEmpty(tunnel.TargetGroupId))
        {
            var groupMemberIds = await ResolveGroupMemberIdsAsync(tunnel.TargetGroupId, ct);
            var filtered = allMailboxes.Where(m => groupMemberIds.Contains(m.EntraId)).ToList();
            logger.LogInformation(
                "Tunnel {TunnelName}: scoped to group {GroupName} ({GroupId}) — {Filtered}/{Total} mailboxes matched",
                tunnel.Name, tunnel.TargetGroupName, tunnel.TargetGroupId, filtered.Count, allMailboxes.Count);
            return filtered;
        }
```

with

```csharp
        if (!string.IsNullOrEmpty(tunnel.TargetGroupId))
        {
            // Phase 3 (§3.6): a group member who was never auto-provisioned is not in the cache
            // table — enumerate the tenant (once per run) before filtering, exactly like AllUsers.
            await EnsureTargetMailboxesRefreshedAsync(ct);
            await using var groupDb = await dbContextFactory.CreateDbContextAsync(ct);
            var groupCandidates = await AvailableActiveMailboxes(groupDb, reprobeCutoff).ToListAsync(ct);
            var groupMemberIds = await ResolveGroupMemberIdsAsync(tunnel.TargetGroupId, ct);
            var filtered = groupCandidates.Where(m => groupMemberIds.Contains(m.EntraId)).ToList();
            logger.LogInformation(
                "Tunnel {TunnelName}: scoped to group {GroupName} ({GroupId}) — {Filtered}/{Total} mailboxes matched",
                tunnel.Name, tunnel.TargetGroupName, tunnel.TargetGroupId, filtered.Count, groupCandidates.Count);
            return filtered;
        }
```

and in the AllUsers block replace

```csharp
        await RefreshTargetMailboxesAsync(ct);
```

with

```csharp
        await EnsureTargetMailboxesRefreshedAsync(ct);
```

Then directly above `private async Task RefreshTargetMailboxesAsync(CancellationToken ct)` add:

```csharp
    /// <summary>
    /// Phase 3 (§3.6): runs <see cref="RefreshTargetMailboxesAsync"/> at most once per run, whichever
    /// scope asks first. The flag is set before the attempt so a Graph failure is not retried by
    /// every following tunnel (the refresh already swallows and logs its own failures).
    /// </summary>
    private async Task EnsureTargetMailboxesRefreshedAsync(CancellationToken ct)
    {
        if (_targetMailboxesRefreshed)
            return;
        _targetMailboxesRefreshed = true;
        TargetMailboxRefreshAttempts++;
        await RefreshTargetMailboxesAsync(ct);
    }
```

- [ ] **Step 8: Run the tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 312, Skipped: 1` (301 + 7 DDGResolver cases + 1 cleanup + 1 locks + 2 engine).

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 47, Skipped: 1`.

- [ ] **Step 9: Commit**

```bash
git add api/Services/DDGResolver.cs api/Program.cs worker/Program.cs shared/Services/ICleanupJobRunner.cs worker/Services/CleanupJobRunner.cs shared/Services/RunLocks.cs api/Controllers/SyncRunsController.cs worker/Services/RunClaimService.cs worker/Services/SyncEngine.cs tests/AFHSync.Tests.Unit/Api/DDGResolverTests.cs tests/AFHSync.Tests.Unit/Sync/CleanupJobRunnerTests.cs tests/AFHSync.Tests.Unit/Sync/RunLocksTests.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs tests/AFHSync.Tests.Integration/DiLifetimeTests.cs
git commit -m "fix(lifetimes): singleton Exchange session with reset-and-retry, cleanup retry-off on the interface, one target refresh per run, shared run-start lock key

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 10: Orphaned Graph contacts — `FolderReconciler` and the reconcile flag (§3.7)

**Files:**
- Modify: `worker/Services/IContactWriter.cs` (`BatchOperationResult.OutcomeUnknown`), `worker/Services/ContactWriter.cs`
- Create: `worker/Services/IFolderReconciler.cs`, `worker/Services/FolderReconciler.cs`
- Modify: `worker/Services/SyncEngine.cs` (constructor, `ProcessMailboxAsync`, three new private helpers), `worker/Program.cs`
- Create: `tests/AFHSync.Tests.Unit/Sync/FolderReconcilerTests.cs`
- Modify: `tests/AFHSync.Tests.Unit/Sync/ContactWriterTests.cs`, `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public record BatchOperationResult(bool Success, string? GraphContactId = null, string? Error = null, bool NotFound = false, bool OutcomeUnknown = false);
  // OutcomeUnknown = true when the $batch POST threw or returned no response — Graph may or may not have applied the chunk.

  public sealed record FolderReconcileResult(int Examined, int Adopted, int Removed);
  public interface IFolderReconciler
  {
      Task<FolderReconcileResult> ReconcileAsync(Tunnel tunnel, TargetMailbox mailbox, string folderId, int canonicalPhoneListId,
          IReadOnlyList<SourceUser> sourceUsers, CancellationToken ct);
  }
  public sealed record GraphContactStub(string Id, string? DisplayName, string? Email);
  public class FolderReconciler : IFolderReconciler
  {
      public FolderReconciler(GraphClientFactory graphClientFactory, IDbContextFactory<AFHSyncDbContext> dbContextFactory, IContactWriter contactWriter, ILogger<FolderReconciler> logger);
      public const string AdoptedResult = "adopted";
      public static string? ContactKey(string? email, string? displayName);
      protected virtual Task<List<GraphContactStub>> ListFolderContactsAsync(string mailboxEntraId, string folderId, CancellationToken ct);
  }
  // SyncEngine constructor gains `IFolderReconciler folderReconciler` directly after `IContactFolderManager contactFolderManager`.
  ```
- Consumes: `TunnelMailboxFolder.ReconcilePendingAt` (Task 2).

- [ ] **Step 1: Write the failing `ContactWriter` test**

In `tests/AFHSync.Tests.Unit/Sync/ContactWriterTests.cs`, change the fake transport so it can throw. Replace

```csharp
    private static (ContactWriter writer, FakeBatchHandler handler) BuildWriterWithFakeGraphTransport(
        Action? onBatchHandled = null)
    {
        var handler = new FakeBatchHandler(onBatchHandled);
```

with

```csharp
    private static (ContactWriter writer, FakeBatchHandler handler) BuildWriterWithFakeGraphTransport(
        Action? onBatchHandled = null, Exception? throwOnSend = null)
    {
        var handler = new FakeBatchHandler(onBatchHandled, throwOnSend);
```

replace `private sealed class FakeBatchHandler(Action? onBatchHandled) : HttpMessageHandler` with `private sealed class FakeBatchHandler(Action? onBatchHandled, Exception? throwOnSend = null) : HttpMessageHandler`, and directly after `CallCount++;` in its `SendAsync` add:

```csharp
            if (throwOnSend is not null)
                throw throwOnSend;
```

Then add this test before the `// ── Fake Graph SDK transport` banner:

```csharp
    // ── Phase 3 (3.7): a transport failure means Graph MAY have applied the chunk ──────────────

    [Fact]
    public async Task CreateContactsBatchAsync_TransportThrows_MarksEveryKeyOutcomeUnknown()
    {
        var (writer, handler) = BuildWriterWithFakeGraphTransport(throwOnSend: new HttpRequestException("connection reset"));
        var ops = new List<(string key, SortedDictionary<string, string> payload)>
        {
            ("k1", new SortedDictionary<string, string> { ["DisplayName"] = "A" }),
            ("k2", new SortedDictionary<string, string> { ["DisplayName"] = "B" }),
        };

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", ops, onChunkCompleted: null, CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(2, results.Count);
        Assert.All(results.Values, r =>
        {
            Assert.False(r.Success);
            Assert.True(r.OutcomeUnknown);
            Assert.Contains("connection reset", r.Error);
        });
    }

    [Fact]
    public async Task CreateContactsBatchAsync_StepFailsWithStatus_IsNotOutcomeUnknown()
    {
        var (writer, _) = BuildWriterWithFakeGraphTransport();
        var ops = new List<(string key, SortedDictionary<string, string> payload)>
        {
            ("k1", new SortedDictionary<string, string> { ["DisplayName"] = "A" }),
        };

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", ops, onChunkCompleted: null, CancellationToken.None);

        Assert.True(results["k1"].Success);
        Assert.False(results["k1"].OutcomeUnknown);   // a definite answer from Graph is never "unknown"
    }
```

- [ ] **Step 2: Write the failing `FolderReconciler` tests**

Create `tests/AFHSync.Tests.Unit/Sync/FolderReconcilerTests.cs`:

```csharp
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Phase 3 (§3.7): strays (Graph contacts in the tunnel folder with no state row) are adopted when a
/// current source user matches by deterministic key and has no state row, and removed otherwise.
/// A subclass intercepts the Graph listing seam; a per-file fake writer records deletes.
/// </summary>
public class FolderReconcilerTests
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

    private sealed class RecordingContactWriter : IContactWriter
    {
        public List<string> DeletedContactIds { get; } = [];

        public Task<string> CreateContactAsync(string mailboxEntraId, string folderId, SortedDictionary<string, string> payload, CancellationToken ct)
            => throw new NotSupportedException();
        public Task UpdateContactAsync(string mailboxEntraId, string graphContactId, SortedDictionary<string, string> payload, CancellationToken ct)
            => throw new NotSupportedException();
        public Task DeleteContactAsync(string mailboxEntraId, string graphContactId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<Dictionary<string, BatchOperationResult>> CreateContactsBatchAsync(string mailboxEntraId, string folderId,
            List<(string key, SortedDictionary<string, string> payload)> operations,
            Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<Dictionary<string, BatchOperationResult>> UpdateContactsBatchAsync(string mailboxEntraId,
            List<(string key, string graphContactId, SortedDictionary<string, string> payload)> operations,
            Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Dictionary<string, BatchOperationResult>> DeleteContactsBatchAsync(string mailboxEntraId,
            List<(string key, string graphContactId)> operations, CancellationToken ct)
        {
            var results = new Dictionary<string, BatchOperationResult>();
            foreach (var (key, graphContactId) in operations)
            {
                DeletedContactIds.Add(graphContactId);
                results[key] = new BatchOperationResult(true);
            }
            return Task.FromResult(results);
        }
    }

    private sealed class FakeFolderReconciler : FolderReconciler
    {
        public List<GraphContactStub> FolderContacts { get; } = [];

        public FakeFolderReconciler(string dbName, RecordingContactWriter writer)
            : base(null!, new TestDbContextFactory(dbName), writer, NullLogger<FolderReconciler>.Instance) { }

        protected override Task<List<GraphContactStub>> ListFolderContactsAsync(string mailboxEntraId, string folderId, CancellationToken ct)
            => Task.FromResult(FolderContacts.ToList());
    }

    private static readonly Tunnel Tunnel = new() { Id = 1, Name = "Buckhead" };
    private static readonly TargetMailbox Mailbox = new() { Id = 7, EntraId = "mbx-7", Email = "seven@contoso.com" };

    private static async Task SeedStateAsync(string dbName, int sourceUserId, string graphContactId)
    {
        using var ctx = MakeDbContext(dbName);
        ctx.ContactSyncStates.Add(new ContactSyncState
        {
            SourceUserId = sourceUserId, PhoneListId = 1, TargetMailboxId = Mailbox.Id, TunnelId = Tunnel.Id,
            GraphContactId = graphContactId, DataHash = "h", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    [Theory]
    [InlineData("Alice@Contoso.com", "Alice", "alice@contoso.com")]
    [InlineData("  bob@contoso.com ", null, "bob@contoso.com")]
    [InlineData(null, "  Cara Lee ", "cara lee")]
    [InlineData("", "Dan", "dan")]
    [InlineData(null, "   ", null)]
    public void ContactKey_PrefersEmail_ThenDisplayName_LowerCased(string? email, string? displayName, string? expected)
        => Assert.Equal(expected, FolderReconciler.ContactKey(email, displayName));

    [Fact]
    public async Task Stray_MatchingSourceUserWithoutState_IsAdopted_WithNullHash()
    {
        var dbName = Guid.NewGuid().ToString();
        var writer = new RecordingContactWriter();
        var reconciler = new FakeFolderReconciler(dbName, writer);
        reconciler.FolderContacts.Add(new GraphContactStub("g-alice", "Alice", "alice@contoso.com"));
        var users = new List<SourceUser> { new() { Id = 1, EntraId = "u1", DisplayName = "Alice", Email = "ALICE@contoso.com" } };

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", canonicalPhoneListId: 1, users, CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(Examined: 1, Adopted: 1, Removed: 0), result);
        Assert.Empty(writer.DeletedContactIds);
        await using var verifyCtx = MakeDbContext(dbName);
        var state = await verifyCtx.ContactSyncStates.SingleAsync();
        Assert.Equal(1, state.SourceUserId);
        Assert.Equal("g-alice", state.GraphContactId);
        Assert.Equal(1, state.PhoneListId);
        Assert.Equal(Tunnel.Id, state.TunnelId);
        Assert.Equal(Mailbox.Id, state.TargetMailboxId);
        Assert.Null(state.DataHash);                                   // next classification PATCHes it
        Assert.Equal(FolderReconciler.AdoptedResult, state.LastResult);
    }

    [Fact]
    public async Task Stray_MatchingSourceUserThatAlreadyHasState_IsRemoved()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedStateAsync(dbName, sourceUserId: 1, graphContactId: "g-alice-real");
        var writer = new RecordingContactWriter();
        var reconciler = new FakeFolderReconciler(dbName, writer);
        reconciler.FolderContacts.Add(new GraphContactStub("g-alice-real", "Alice", "alice@contoso.com"));
        reconciler.FolderContacts.Add(new GraphContactStub("g-alice-dupe", "Alice", "alice@contoso.com"));
        var users = new List<SourceUser> { new() { Id = 1, EntraId = "u1", DisplayName = "Alice", Email = "alice@contoso.com" } };

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", 1, users, CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(2, 0, 1), result);
        Assert.Equal(new[] { "g-alice-dupe" }, writer.DeletedContactIds);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(1, await verifyCtx.ContactSyncStates.CountAsync());   // no second row
    }

    [Fact]
    public async Task Stray_MatchingNobody_IsRemoved()
    {
        var dbName = Guid.NewGuid().ToString();
        var writer = new RecordingContactWriter();
        var reconciler = new FakeFolderReconciler(dbName, writer);
        reconciler.FolderContacts.Add(new GraphContactStub("g-ghost", "Ghost", "ghost@contoso.com"));
        var users = new List<SourceUser> { new() { Id = 1, EntraId = "u1", DisplayName = "Alice", Email = "alice@contoso.com" } };

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", 1, users, CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(1, 0, 1), result);
        Assert.Equal(new[] { "g-ghost" }, writer.DeletedContactIds);
    }

    [Fact]
    public async Task KnownContacts_AreLeftAlone_EvenWhenTheirUserLeftTheSource()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedStateAsync(dbName, sourceUserId: 9, graphContactId: "g-stale");   // stale handler's job, not ours
        var writer = new RecordingContactWriter();
        var reconciler = new FakeFolderReconciler(dbName, writer);
        reconciler.FolderContacts.Add(new GraphContactStub("g-stale", "Old Timer", "old@contoso.com"));

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", 1, [], CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(1, 0, 0), result);
        Assert.Empty(writer.DeletedContactIds);
    }

    [Fact]
    public async Task TwoStraysForOneUser_AdoptsTheFirst_RemovesTheSecond()
    {
        var dbName = Guid.NewGuid().ToString();
        var writer = new RecordingContactWriter();
        var reconciler = new FakeFolderReconciler(dbName, writer);
        reconciler.FolderContacts.Add(new GraphContactStub("g-1", "Alice", "alice@contoso.com"));
        reconciler.FolderContacts.Add(new GraphContactStub("g-2", "Alice", "alice@contoso.com"));
        var users = new List<SourceUser> { new() { Id = 1, EntraId = "u1", DisplayName = "Alice", Email = "alice@contoso.com" } };

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", 1, users, CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(2, 1, 1), result);
        Assert.Equal(new[] { "g-2" }, writer.DeletedContactIds);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal("g-1", (await verifyCtx.ContactSyncStates.SingleAsync()).GraphContactId);
    }
}
```

- [ ] **Step 3: Write the failing `SyncEngine` tests**

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`:

1. Extend `CreateEngine` — add the parameter `FakeFolderReconciler? folderReconciler = null,` directly after `FakeContactFolderManager? folderManager = null,` and pass `folderReconciler ?? new FakeFolderReconciler(),` directly after `folderManager ?? new FakeContactFolderManager(),` in the `new SyncEngine(` call.

2. Extend `FakeContactWriter` with a property (next to `CreateReturnsNoId`):

```csharp
        /// <summary>When true, batch creates report every key as OutcomeUnknown (transport failure).</summary>
        public bool CreateOutcomeUnknown { get; init; }
```

and in its `CreateContactsBatchAsync`, directly after the `if (CreateReturnsNoId) { … continue; }` block add:

```csharp
                    if (CreateOutcomeUnknown)
                    {
                        chunkResults[key] = new BatchOperationResult(false, Error: "connection reset", OutcomeUnknown: true);
                        continue;
                    }
```

3. Add this fake next to `FakeContactFolderManager`:

```csharp
    private sealed class FakeFolderReconciler : IFolderReconciler
    {
        public List<(int TunnelId, int MailboxId, string FolderId)> Calls { get; } = [];

        public Task<FolderReconcileResult> ReconcileAsync(Tunnel tunnel, TargetMailbox mailbox, string folderId,
            int canonicalPhoneListId, IReadOnlyList<SourceUser> sourceUsers, CancellationToken ct)
        {
            Calls.Add((tunnel.Id, mailbox.Id, folderId));
            return Task.FromResult(new FolderReconcileResult(0, 0, 0));
        }
    }
```

4. Add the tests before the `// Stub implementations` banner:

```csharp
    // ==============================
    // Phase 3 (3.7): orphaned Graph contacts are reconciled
    // ==============================

    private static async Task SeedKnownFolderAsync(string dbName, int tunnelId, int mailboxId, DateTime? reconcilePendingAt)
    {
        using var ctx = MakeDbContext(dbName);
        ctx.TunnelMailboxFolders.Add(new TunnelMailboxFolder
        {
            TunnelId = tunnelId, TargetMailboxId = mailboxId, GraphFolderId = "fake-folder-id", FolderName = "Avail Tunnel",
            UpdatedAt = DateTime.UtcNow, ReconcilePendingAt = reconcilePendingAt
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task RunAsync_CreateBatchOutcomeUnknown_ReconcilesTheFolderInRun()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-1", Email = "one@contoso.com", IsActive = true });
        var reconciler = new FakeFolderReconciler();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            contactWriter: new FakeContactWriter { CreateOutcomeUnknown = true },
            folderReconciler: reconciler);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        var call = Assert.Single(reconciler.Calls);
        Assert.Equal((1, 1, "fake-folder-id"), call);
    }

    [Fact]
    public async Task RunAsync_CreateBatchSucceeds_DoesNotReconcile_AndClearsTheFlag()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-1", Email = "one@contoso.com", IsActive = true });
        await SeedKnownFolderAsync(dbName, tunnelId: 1, mailboxId: 1, reconcilePendingAt: null);
        var reconciler = new FakeFolderReconciler();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderReconciler: reconciler);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Empty(reconciler.Calls);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Null((await verifyCtx.TunnelMailboxFolders.SingleAsync()).ReconcilePendingAt);   // set before the batch, cleared after
    }

    [Fact]
    public async Task RunAsync_ReconcilePendingFromPreviousRun_ReconcilesBeforeClassification_AndClearsTheFlag()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-1", Email = "one@contoso.com", IsActive = true });
        await SeedKnownFolderAsync(dbName, 1, 1, reconcilePendingAt: DateTime.UtcNow.AddHours(-5));   // a crash left it set
        var reconciler = new FakeFolderReconciler();
        // Zero source members would skip the tunnel before any mailbox runs, so give it one user
        // whose contact already exists (the skip path) — the pending reconcile must still fire.
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.ContactSyncStates.Add(new ContactSyncState
            {
                SourceUserId = 1, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1,
                GraphContactId = "g-existing", DataHash = "new-hash", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await seedCtx.SaveChangesAsync();
        }
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderReconciler: reconciler);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Single(reconciler.Calls);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Null((await verifyCtx.TunnelMailboxFolders.SingleAsync()).ReconcilePendingAt);
    }

    [Fact]
    public async Task RunAsync_DryRun_NeverReconciles_EvenWithFlagSet()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mb-1", Email = "one@contoso.com", IsActive = true });
        await SeedKnownFolderAsync(dbName, 1, 1, reconcilePendingAt: DateTime.UtcNow.AddHours(-5));
        var reconciler = new FakeFolderReconciler();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            folderReconciler: reconciler);

        await engine.RunAsync(null, RunType.DryRun, isDryRun: true, CancellationToken.None);

        Assert.Empty(reconciler.Calls);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.NotNull((await verifyCtx.TunnelMailboxFolders.SingleAsync()).ReconcilePendingAt);   // untouched by a dry run
    }
```

- [ ] **Step 4: Run to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~FolderReconcilerTests|FullyQualifiedName~OutcomeUnknown|FullyQualifiedName~Reconcile" 2>&1 | grep -E "error" | head -4`
Expected: build errors for `OutcomeUnknown`, `FolderReconciler`, `IFolderReconciler`.

- [ ] **Step 5: `OutcomeUnknown` on `BatchOperationResult`**

In `worker/Services/IContactWriter.cs` replace the record with:

```csharp
/// <summary>
/// Result of a single operation within a batch request. Phase 3 (§3.7): <see cref="OutcomeUnknown"/>
/// is true when the $batch POST itself threw or returned no response — Graph may or may not have
/// applied the step, so the caller reconciles the folder instead of trusting <see cref="Success"/>.
/// </summary>
public record BatchOperationResult(
    bool Success,
    string? GraphContactId = null,
    string? Error = null,
    bool NotFound = false,
    bool OutcomeUnknown = false);
```

In `worker/Services/ContactWriter.cs`, `ExecuteBatchWithRetryAsync`, replace

```csharp
            foreach (var key in stepIdToKey.Values)
                results[key] = new BatchOperationResult(false, Error: ex.Message);
            return;
        }

        if (response == null)
        {
            foreach (var key in stepIdToKey.Values)
                results[key] = new BatchOperationResult(false, Error: "Null batch response");
            return;
        }
```

with

```csharp
            // Phase 3 (§3.7): the request may have reached Graph — the caller reconciles the folder.
            foreach (var key in stepIdToKey.Values)
                results[key] = new BatchOperationResult(false, Error: ex.Message, OutcomeUnknown: true);
            return;
        }

        if (response == null)
        {
            foreach (var key in stepIdToKey.Values)
                results[key] = new BatchOperationResult(false, Error: "Null batch response", OutcomeUnknown: true);
            return;
        }
```

- [ ] **Step 6: Add the reconciler**

Create `worker/Services/IFolderReconciler.cs`:

```csharp
using AFHSync.Shared.Entities;

namespace AFHSync.Worker.Services;

/// <param name="Examined">Graph contacts found in the folder.</param>
/// <param name="Adopted">Strays matched to a current source user and given a state row.</param>
/// <param name="Removed">Strays deleted from Graph.</param>
public sealed record FolderReconcileResult(int Examined, int Adopted, int Removed);

/// <summary>
/// Phase 3 (§3.7): reconciles a tunnel's contact folder in one mailbox against contact_sync_state.
/// A "stray" is a Graph contact whose id no state row references — the residue of a create chunk
/// whose outcome was lost (transport failure, crash, shutdown between the POST and the persist).
/// </summary>
public interface IFolderReconciler
{
    Task<FolderReconcileResult> ReconcileAsync(
        Tunnel tunnel,
        TargetMailbox mailbox,
        string folderId,
        int canonicalPhoneListId,
        IReadOnlyList<SourceUser> sourceUsers,
        CancellationToken ct);
}
```

Create `worker/Services/FolderReconciler.cs`:

```csharp
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Worker.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph.Models;

namespace AFHSync.Worker.Services;

/// <summary>A Graph contact as seen by the reconciler's Graph seam.</summary>
public sealed record GraphContactStub(string Id, string? DisplayName, string? Email);

/// <summary>
/// Phase 3 (§3.7). For every stray in the folder: compute the deterministic key (primary email
/// lower-cased, else display name lower-cased); if a current source user has that key and no
/// state row yet, ADOPT the contact (state row with the Graph id, data_hash NULL so the next
/// classification PATCHes it into shape); otherwise REMOVE it. Known contacts — including stale
/// ones — are never touched; that is the stale handler's job.
///
/// Graph listing is a <c>protected virtual</c> seam so unit tests can subclass this class.
/// </summary>
public class FolderReconciler : IFolderReconciler
{
    public const string AdoptedResult = "adopted";

    private readonly GraphClientFactory? _graphClientFactory;
    private readonly IDbContextFactory<AFHSyncDbContext> _dbContextFactory;
    private readonly IContactWriter _contactWriter;
    private readonly ILogger<FolderReconciler> _logger;

    public FolderReconciler(
        GraphClientFactory graphClientFactory,
        IDbContextFactory<AFHSyncDbContext> dbContextFactory,
        IContactWriter contactWriter,
        ILogger<FolderReconciler> logger)
    {
        _graphClientFactory = graphClientFactory;
        _dbContextFactory = dbContextFactory;
        _contactWriter = contactWriter;
        _logger = logger;
    }

    /// <summary>Deterministic identity shared by SourceUser and Graph Contact: email, else display name; trimmed, lower-cased; null when neither is set.</summary>
    public static string? ContactKey(string? email, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(email))
            return email.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName.Trim().ToLowerInvariant();
        return null;
    }

    /// <inheritdoc />
    public async Task<FolderReconcileResult> ReconcileAsync(
        Tunnel tunnel,
        TargetMailbox mailbox,
        string folderId,
        int canonicalPhoneListId,
        IReadOnlyList<SourceUser> sourceUsers,
        CancellationToken ct)
    {
        var graphContacts = await ListFolderContactsAsync(mailbox.EntraId, folderId, ct);

        // Bookkeeping writes use CancellationToken.None: an adopted row must not be lost to a shutdown.
        await using var db = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        var states = await db.ContactSyncStates
            .Where(s => s.TunnelId == tunnel.Id && s.TargetMailboxId == mailbox.Id)
            .ToListAsync(CancellationToken.None);
        var knownIds = states
            .Where(s => !string.IsNullOrEmpty(s.GraphContactId))
            .Select(s => s.GraphContactId!)
            .ToHashSet(StringComparer.Ordinal);
        var usersWithState = states.Select(s => s.SourceUserId).ToHashSet();

        var usersByKey = new Dictionary<string, SourceUser>(StringComparer.Ordinal);
        foreach (var user in sourceUsers)
        {
            var key = ContactKey(user.Email, user.DisplayName);
            if (key is not null)
                usersByKey.TryAdd(key, user);
        }

        var toRemove = new List<(string key, string graphContactId)>();
        var adopted = 0;
        var now = DateTime.UtcNow;
        foreach (var contact in graphContacts)
        {
            if (knownIds.Contains(contact.Id))
                continue;

            var key = ContactKey(contact.Email, contact.DisplayName);
            if (key is not null && usersByKey.TryGetValue(key, out var user) && !usersWithState.Contains(user.Id))
            {
                db.ContactSyncStates.Add(new ContactSyncState
                {
                    SourceUserId = user.Id,
                    PhoneListId = canonicalPhoneListId,
                    TargetMailboxId = mailbox.Id,
                    TunnelId = tunnel.Id,
                    GraphContactId = contact.Id,
                    DataHash = null,
                    LastSyncedAt = now,
                    LastResult = AdoptedResult,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                usersWithState.Add(user.Id);
                adopted++;
                _logger.LogInformation(
                    "Reconcile: adopted stray Graph contact {ContactId} ({Key}) for SourceUserId={SourceUserId} in mailbox {Email}",
                    contact.Id, key, user.Id, mailbox.Email);
            }
            else
            {
                toRemove.Add((contact.Id, contact.Id));
            }
        }

        if (adopted > 0)
            await db.SaveChangesAsync(CancellationToken.None);

        var removed = 0;
        if (toRemove.Count > 0)
        {
            var results = await _contactWriter.DeleteContactsBatchAsync(mailbox.EntraId, toRemove, ct);
            foreach (var (key, _) in toRemove)
            {
                if (results.TryGetValue(key, out var r) && (r.Success || r.NotFound))
                    removed++;
                else
                    _logger.LogWarning("Reconcile: could not remove stray Graph contact {ContactId} in mailbox {Email}: {Error}",
                        key, mailbox.Email, r?.Error ?? "no result");
            }
        }

        _logger.LogInformation(
            "Reconcile: tunnel {TunnelName} / mailbox {Email}: {Examined} Graph contact(s), {Adopted} adopted, {Removed} removed",
            tunnel.Name, mailbox.Email, graphContacts.Count, adopted, removed);

        return new FolderReconcileResult(graphContacts.Count, adopted, removed);
    }

    /// <summary>GET /users/{mailbox}/contactFolders/{id}/contacts (id, displayName, emailAddresses), all pages.</summary>
    protected virtual async Task<List<GraphContactStub>> ListFolderContactsAsync(string mailboxEntraId, string folderId, CancellationToken ct)
    {
        var client = _graphClientFactory?.Client
            ?? throw new InvalidOperationException("GraphClientFactory is required for Graph operations");

        var contacts = new List<GraphContactStub>();
        var response = await client.Users[mailboxEntraId].ContactFolders[folderId].Contacts.GetAsync(config =>
        {
            config.QueryParameters.Select = ["id", "displayName", "emailAddresses"];
            config.QueryParameters.Top = 999;
        }, ct);

        if (response?.Value is null)
            return contacts;

        var iterator = Microsoft.Graph.PageIterator<Contact, ContactCollectionResponse>
            .CreatePageIterator(client, response, c =>
            {
                if (c.Id is not null)
                    contacts.Add(new GraphContactStub(c.Id, c.DisplayName, c.EmailAddresses?.FirstOrDefault()?.Address));
                return true;
            });
        await iterator.IterateAsync(ct);
        return contacts;
    }
}
```

Register it in `worker/Program.cs` directly after `services.AddScoped<IContactFolderManager, ContactFolderManager>();`:

```csharp
    services.AddScoped<IFolderReconciler, FolderReconciler>();
```

- [ ] **Step 7: Wire the engine — constructor, flag helpers, triggers**

In `worker/Services/SyncEngine.cs`:

1. Constructor: after `    IContactFolderManager contactFolderManager,` add `    IFolderReconciler folderReconciler,`.

2. In `ProcessMailboxAsync`, directly after the block

```csharp
        // Phase 2 (§2.1): the first successful folder lookup after an unavailable stamp clears it.
        if (mailbox.MailboxUnavailableAt is not null)
            await ClearMailboxUnavailableAsync(mailbox.Id);
```

add

```csharp

        // Phase 3 (§3.7): a flag left by a previous run (crash/shutdown between a create batch and
        // its bookkeeping) means Graph may hold contacts with no state row — reconcile BEFORE
        // classification so they are adopted (and PATCHed below) instead of created twice.
        if (!isDryRun && folderId is not null && await IsReconcilePendingAsync(tunnel.Id, mailbox.Id))
        {
            logger.LogInformation("Reconcile pending for tunnel {TunnelId} in mailbox {Email} from a previous run", tunnel.Id, mailbox.Email);
            var pendingResult = await folderReconciler.ReconcileAsync(tunnel, mailbox, folderId, canonicalPhoneList.Id, sourceUsers, ct);
            removed += pendingResult.Removed;
            await SetReconcilePendingAsync(tunnel.Id, mailbox.Id, pending: false);
        }
```

3. Still in `ProcessMailboxAsync`, in the `if (!isDryRun && pendingCreates.Count > 0)` block, replace

```csharp
            await contactWriter.CreateContactsBatchAsync(
                mailbox.EntraId, targetFolderId, batchOps, OnCreateChunkCompleted, ct);
```

with

```csharp
            // Phase 3 (§3.7): flag the folder before the first chunk goes out; only a clean finish
            // (every chunk answered and persisted) clears it. Anything in between — a crash, a
            // shutdown, an exception — leaves it for the next run to reconcile.
            await SetReconcilePendingAsync(tunnel.Id, mailbox.Id, pending: true);
            var createOutcomeUnknown = false;
            var createResults = await contactWriter.CreateContactsBatchAsync(
                mailbox.EntraId, targetFolderId, batchOps, OnCreateChunkCompleted, ct);
            createOutcomeUnknown = createResults.Values.Any(r => r.OutcomeUnknown);

            if (createOutcomeUnknown)
            {
                logger.LogWarning("Create batch had an unknown outcome for tunnel {TunnelId} in mailbox {Email} — reconciling the folder", tunnel.Id, mailbox.Email);
                var reconcile = await folderReconciler.ReconcileAsync(tunnel, mailbox, targetFolderId, canonicalPhoneList.Id, sourceUsers, ct);
                removed += reconcile.Removed;
            }
            await SetReconcilePendingAsync(tunnel.Id, mailbox.Id, pending: false);
```

4. After `ClearMailboxUnavailableAsync` add the two flag helpers:

```csharp
    /// <summary>Phase 3 (§3.7): reads tunnel_mailbox_folders.reconcile_pending_at for the pair (false when no row).</summary>
    private async Task<bool> IsReconcilePendingAsync(int tunnelId, int mailboxId)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            return await db.TunnelMailboxFolders
                .AnyAsync(f => f.TunnelId == tunnelId && f.TargetMailboxId == mailboxId && f.ReconcilePendingAt != null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read the reconcile flag for tunnel {TunnelId} mailbox {MailboxId}", tunnelId, mailboxId);
            return false;
        }
    }

    /// <summary>
    /// Phase 3 (§3.7): sets or clears reconcile_pending_at. No-op when the folder row does not exist
    /// yet (it is upserted by ContactFolderManager before any contact write in production). Fresh
    /// context + CancellationToken.None — the flag must outlive a cancel.
    /// </summary>
    private async Task SetReconcilePendingAsync(int tunnelId, int mailboxId, bool pending)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            var row = await db.TunnelMailboxFolders
                .FirstOrDefaultAsync(f => f.TunnelId == tunnelId && f.TargetMailboxId == mailboxId, CancellationToken.None);
            if (row is null)
                return;
            var value = pending ? DateTime.UtcNow : (DateTime?)null;
            if (row.ReconcilePendingAt == value || (!pending && row.ReconcilePendingAt is null))
                return;
            row.ReconcilePendingAt = value;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to {Action} the reconcile flag for tunnel {TunnelId} mailbox {MailboxId}",
                pending ? "set" : "clear", tunnelId, mailboxId);
        }
    }
```

- [ ] **Step 8: Run the unit tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 328, Skipped: 1` (312 + 2 writer + 5 key cases + 5 reconciler + 4 engine — one `[Theory]` with 5 rows counts 5).

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 47, Skipped: 1` (unchanged).

- [ ] **Step 9: Commit**

```bash
git add worker/Services/IContactWriter.cs worker/Services/ContactWriter.cs worker/Services/IFolderReconciler.cs worker/Services/FolderReconciler.cs worker/Services/SyncEngine.cs worker/Program.cs tests/AFHSync.Tests.Unit/Sync/FolderReconcilerTests.cs tests/AFHSync.Tests.Unit/Sync/ContactWriterTests.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -m "feat(worker): reconcile orphaned Graph contacts after an unknown-outcome batch or a crash (adopt/remove strays by deterministic key)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---
### Task 11: `RecordFailedItem` and the `ProcessMailboxAsync` split (§3.8 — behaviour-preserving)

**Files:**
- Modify: `worker/Services/SyncEngine.cs`

**Interfaces:**
- Produces (all private to `SyncEngine`; signatures fixed so reviewers can check the split):
  ```csharp
  private sealed class MailboxCounters { public int Created, Updated, Skipped, Failed, Removed; public (int created, int updated, int skipped, int failed, int removed) ToTuple(); }
  private void RecordFailedItem(SyncRun run, Tunnel tunnel, int? phoneListId, int? mailboxId, int? sourceUserId, string message);
  private Task<(string? folderId, bool wasCreated)?> ResolveMailboxFolderAsync(Tunnel tunnel, PhoneList canonicalPhoneList, TargetMailbox mailbox, SyncRun run, bool isDryRun, MailboxCounters counters, CancellationToken ct);
  private Task ReconcileIfPendingAsync(Tunnel tunnel, PhoneList canonicalPhoneList, TargetMailbox mailbox, string? folderId, List<SourceUser> sourceUsers, bool isDryRun, MailboxCounters counters, CancellationToken ct);
  private Task<Dictionary<int, ContactSyncState>> LoadExistingStatesAsync(Tunnel tunnel, PhoneList canonicalPhoneList, List<int> allPhoneListIds, TargetMailbox mailbox, string? folderId, bool folderWasCreated, bool isDryRun, MailboxCounters counters, CancellationToken ct);
  private (List<(string key, int sourceUserId, SortedDictionary<string, string> payload, string dataHash)> pendingCreates,
           List<(string key, int sourceUserId, string graphContactId, int stateId, SortedDictionary<string, string> payload, string dataHash, string? previousHash)> pendingUpdates)
      ClassifyContacts(Tunnel tunnel, PhoneList canonicalPhoneList, TargetMailbox mailbox, SyncRun run, List<SourceUser> sourceUsers, List<FieldProfileField> fieldSettings, Dictionary<int, ContactSyncState> existingStates, MailboxCounters counters);
  private Task ExecuteCreatesAsync(Tunnel tunnel, PhoneList canonicalPhoneList, TargetMailbox mailbox, SyncRun run, string? folderId, List<SourceUser> sourceUsers, List<(string key, int sourceUserId, SortedDictionary<string, string> payload, string dataHash)> pendingCreates, bool isDryRun, MailboxCounters counters, CancellationToken ct);
  private Task<List<int>> ExecuteUpdatesAsync(Tunnel tunnel, PhoneList canonicalPhoneList, TargetMailbox mailbox, SyncRun run, List<(string key, int sourceUserId, string graphContactId, int stateId, SortedDictionary<string, string> payload, string dataHash, string? previousHash)> pendingUpdates, bool isDryRun, MailboxCounters counters, CancellationToken ct);
  private Task HealDeadStatesAsync(List<int> statesToHeal, bool isDryRun);
  private Task HandleStaleContactsAsync(Tunnel tunnel, List<int> allPhoneListIds, TargetMailbox mailbox, SyncRun run, List<SourceUser> sourceUsers, bool isDryRun, bool skipStale, MailboxCounters counters, CancellationToken ct);
  ```
  `ProcessMailboxAsync` keeps its signature and return type. No test changes; the full unit suite must pass unchanged.
- Consumes: the `ProcessMailboxAsync` body as left by Task 10.

- [ ] **Step 1: Confirm the starting point**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3 && grep -c 'Action = "failed"' worker/Services/SyncEngine.cs`
Expected: `Passed: 328, Skipped: 1`; `9` (the nine failed-item blocks).

- [ ] **Step 2: Add `RecordFailedItem` and replace the nine blocks**

In `worker/Services/SyncEngine.cs`, directly after `RecordTunnelRunAsync` add:

```csharp
    /// <summary>Phase 3 (§3.8): the one way a "failed" run item is recorded.</summary>
    private void RecordFailedItem(SyncRun run, Tunnel tunnel, int? phoneListId, int? mailboxId, int? sourceUserId, string message)
    {
        runLogger.AddItem(new SyncRunItem
        {
            SyncRunId = run.Id,
            TunnelId = tunnel.Id,
            PhoneListId = phoneListId,
            TargetMailboxId = mailboxId,
            SourceUserId = sourceUserId,
            Action = "failed",
            ErrorMessage = message,
            CreatedAt = DateTime.UtcNow
        });
    }
```

Then replace every `runLogger.AddItem(new SyncRunItem { … Action = "failed", … });` statement (find them with `grep -n 'Action = "failed"'`) with the matching one-liner — the `ErrorMessage` expression identifies each site:

| Site (ErrorMessage today) | Replacement |
|---|---|
| `$"Source '{failure.DisplayName}': {failure.Reason}"` (ProcessTunnelAsync, source failures) | `RecordFailedItem(run, tunnel, null, null, null, $"Source '{failure.DisplayName}': {failure.Reason}");` |
| `$"DDG target '{ddgName}': {failure.Reason}"` (ProcessTunnelAsync, DDG target failures) | `RecordFailedItem(run, tunnel, canonicalPl.Id, null, null, $"DDG target '{ddgName}': {failure.Reason}");` |
| `$"Mailbox '{mailbox.Email}': {ex.Message}"` (mailbox lambda catch) | `RecordFailedItem(run, tunnel, canonicalPhoneList.Id, mailbox.Id, null, $"Mailbox '{mailbox.Email}': {ex.Message}");` |
| `$"Folder '{tunnel.Name}': {ex.Message}"` (folder catch) | `RecordFailedItem(run, tunnel, canonicalPhoneList.Id, mailbox.Id, null, $"Folder '{tunnel.Name}': {ex.Message}");` |
| `ex.Message` (payload build catch) | `RecordFailedItem(run, tunnel, canonicalPhoneList.Id, mailbox.Id, sourceUser.Id, ex.Message);` |
| `error` (create chunk callback, else branch) | `RecordFailedItem(run, tunnel, canonicalPhoneList.Id, mailbox.Id, pending.sourceUserId, error);` |
| `"No batch result returned"` (after the create batch) | `RecordFailedItem(run, tunnel, canonicalPhoneList.Id, mailbox.Id, pending.sourceUserId, "No batch result returned");` |
| `error` (update chunk callback, else branch) | `RecordFailedItem(run, tunnel, canonicalPhoneList.Id, mailbox.Id, pending.sourceUserId, error);` |
| `"No batch result returned"` (after the update batch) | `RecordFailedItem(run, tunnel, canonicalPhoneList.Id, mailbox.Id, pending.sourceUserId, "No batch result returned");` |

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3 && grep -c 'Action = "failed"' worker/Services/SyncEngine.cs`
Expected: `Passed: 328, Skipped: 1`; `1` (only the helper).

Commit this half on its own:

```bash
git add worker/Services/SyncEngine.cs
git commit -m "refactor(worker): RecordFailedItem replaces nine copies of the failed run-item block

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

- [ ] **Step 3: Add `MailboxCounters` and the orchestrator**

Add this nested class at the end of `SyncEngine` (before the closing brace of the class):

```csharp
    /// <summary>
    /// Phase 3 (§3.8): one mailbox's tallies. A class (not a tuple) because the batch chunk callbacks
    /// close over it and increment from inside ContactWriter's chunk loop.
    /// </summary>
    private sealed class MailboxCounters
    {
        public int Created, Updated, Skipped, Failed, Removed;

        public (int created, int updated, int skipped, int failed, int removed) ToTuple()
            => (Created, Updated, Skipped, Failed, Removed);
    }
```

Replace the entire body of `ProcessMailboxAsync` (keep its signature and XML doc) with:

```csharp
    {
        var counters = new MailboxCounters();

        // A. Contact folder (null ⇒ the mailbox is done: unavailable, or failed and recorded).
        var folder = await ResolveMailboxFolderAsync(tunnel, canonicalPhoneList, mailbox, run, isDryRun, counters, ct);
        if (folder is null)
            return counters.ToTuple();
        var (folderId, folderWasCreated) = folder.Value;

        // B. A reconcile left pending by a previous run (§3.7).
        await ReconcileIfPendingAsync(tunnel, canonicalPhoneList, mailbox, folderId, sourceUsers, isDryRun, counters, ct);

        // C. Existing sync state for this (tunnel, mailbox), de-duplicated; duplicates cleaned up.
        var existingStates = await LoadExistingStatesAsync(tunnel, canonicalPhoneList, allPhoneListIds, mailbox,
            folderId, folderWasCreated, isDryRun, counters, ct);

        // D. Classify every source user as create / update / skip — no Graph calls.
        var (pendingCreates, pendingUpdates) = ClassifyContacts(tunnel, canonicalPhoneList, mailbox, run,
            sourceUsers, fieldSettings, existingStates, counters);

        // E/F. Graph writes (or dry-run reporting), persisted per chunk (§2.6a).
        await ExecuteCreatesAsync(tunnel, canonicalPhoneList, mailbox, run, folderId, sourceUsers, pendingCreates, isDryRun, counters, ct);
        var statesToHeal = await ExecuteUpdatesAsync(tunnel, canonicalPhoneList, mailbox, run, pendingUpdates, isDryRun, counters, ct);

        // Note: live progress for the dashboard is updated in the per-tunnel loop
        // (ProcessTunnelAsync caller) which has access to the overall totals.

        // G. Drop states whose contact 404'd on update; H. stale pass (skipped when a source failed, §2.3).
        await HealDeadStatesAsync(statesToHeal, isDryRun);
        await HandleStaleContactsAsync(tunnel, allPhoneListIds, mailbox, run, sourceUsers, isDryRun, skipStale, counters, ct);

        return counters.ToTuple();
    }
```

- [ ] **Step 4: Extract the eight methods — move the old body verbatim, block by block**

Cut the OLD body (saved from before Step 3 — do this with the file open in an editor, or from `git show HEAD:worker/Services/SyncEngine.cs`) into the following private methods, placed directly after `ProcessMailboxAsync`. Rules for every block: move the code **unchanged** except (a) the counter renames `created++` → `counters.Created++`, `updated++` → `counters.Updated++`, `skipped++` → `counters.Skipped++`, `failed++` / `failed += 1` → `counters.Failed++`, `removed += X` → `counters.Removed += X`; (b) the explicit returns listed below; (c) delete the old `int created = 0, updated = 0, skipped = 0, failed = 0, removed = 0;` line — it is replaced by `MailboxCounters`.

**A.** `private async Task<(string? folderId, bool wasCreated)?> ResolveMailboxFolderAsync(Tunnel tunnel, PhoneList canonicalPhoneList, TargetMailbox mailbox, SyncRun run, bool isDryRun, MailboxCounters counters, CancellationToken ct)`
— from the comment `// Get or create the contact folder (looked up only, never created, in a dry run).` through the statement `await ClearMailboxUnavailableAsync(mailbox.Id);` (inclusive of its `if`). The two `return (created, updated, skipped, failed, removed);` inside the catch become `return null;`. End the method with `return (folderId, folderWasCreated);`.

**B.** `private async Task ReconcileIfPendingAsync(Tunnel tunnel, PhoneList canonicalPhoneList, TargetMailbox mailbox, string? folderId, List<SourceUser> sourceUsers, bool isDryRun, MailboxCounters counters, CancellationToken ct)`
— the Task 10 block starting at the comment `// Phase 3 (§3.7): a flag left by a previous run` through its closing `}` (`removed += pendingResult.Removed;` → `counters.Removed += pendingResult.Removed;`).

**C.** `private async Task<Dictionary<int, ContactSyncState>> LoadExistingStatesAsync(Tunnel tunnel, PhoneList canonicalPhoneList, List<int> allPhoneListIds, TargetMailbox mailbox, string? folderId, bool folderWasCreated, bool isDryRun, MailboxCounters counters, CancellationToken ct)`
— from the comment `// If the folder was just created, any existing sync state is stale (contacts were deleted).` through the end of the duplicate-cleanup block (the `}` after `removed += duplicateStates.Count;`, which becomes `counters.Removed += duplicateStates.Count;`). End with `return existingStates;`.

**D.** `private (List<(string key, int sourceUserId, SortedDictionary<string, string> payload, string dataHash)> pendingCreates, List<(string key, int sourceUserId, string graphContactId, int stateId, SortedDictionary<string, string> payload, string dataHash, string? previousHash)> pendingUpdates) ClassifyContacts(Tunnel tunnel, PhoneList canonicalPhoneList, TargetMailbox mailbox, SyncRun run, List<SourceUser> sourceUsers, List<FieldProfileField> fieldSettings, Dictionary<int, ContactSyncState> existingStates, MailboxCounters counters)`
— from the comment `// Phase 1: Compute payloads and classify each source user as create, update, or skip.` through the end of its `foreach (var sourceUser in sourceUsers)` loop. End with `return (pendingCreates, pendingUpdates);`. (Synchronous — no `async`.)

**E.** `private async Task ExecuteCreatesAsync(Tunnel tunnel, PhoneList canonicalPhoneList, TargetMailbox mailbox, SyncRun run, string? folderId, List<SourceUser> sourceUsers, List<(string key, int sourceUserId, SortedDictionary<string, string> payload, string dataHash)> pendingCreates, bool isDryRun, MailboxCounters counters, CancellationToken ct)`
— the `if (!isDryRun && pendingCreates.Count > 0) { … } else if (isDryRun) { … }` pair for CREATES (the first such pair; its dry-run branch carries the comment `// Dry-run: report creates without Graph calls and without state rows (§2.2).`). The comment `// Phase 2: Execute Graph writes using batching (up to 20 per HTTP call).` and the `var statesToHeal = new List<int>();` declaration that precede it do NOT move here (see F). Task 10's `removed += reconcile.Removed;` → `counters.Removed += reconcile.Removed;`.

**F.** `private async Task<List<int>> ExecuteUpdatesAsync(Tunnel tunnel, PhoneList canonicalPhoneList, TargetMailbox mailbox, SyncRun run, List<(string key, int sourceUserId, string graphContactId, int stateId, SortedDictionary<string, string> payload, string dataHash, string? previousHash)> pendingUpdates, bool isDryRun, MailboxCounters counters, CancellationToken ct)`
— starts with the two comment lines beginning `// Sync-state IDs whose contact 404'd on update` and `var statesToHeal = new List<int>();`, then the `if (!isDryRun && pendingUpdates.Count > 0) { … } else if (isDryRun) { … }` pair for UPDATES (dry-run branch comment `// Dry-run: report updates without Graph calls and without state rows (§2.2).`). End with `return statesToHeal;`.

**G.** `private async Task HealDeadStatesAsync(List<int> statesToHeal, bool isDryRun)`
— the block from the comment `// Heals only: a contact that 404'd on update — drop the dead state so the next run` through the closing `}` of `if (!isDryRun && statesToHeal.Count > 0)`.

**H.** `private async Task HandleStaleContactsAsync(Tunnel tunnel, List<int> allPhoneListIds, TargetMailbox mailbox, SyncRun run, List<SourceUser> sourceUsers, bool isDryRun, bool skipStale, MailboxCounters counters, CancellationToken ct)`
— from the comment `// Handle stale contacts after processing all source users.` through the closing `}` of `if (!isDryRun && !skipStale)` (`removed += staleResult.Removed;` → `counters.Removed += staleResult.Removed;`).

After the move, nothing from the old body may remain outside these eight methods and the orchestrator. Sanity checks:

Run: `grep -n "created++\|updated++\|skipped++\|failed++\|failed += 1\|removed +=" worker/Services/SyncEngine.cs | grep -v "counters\." | grep -v "totalCreated\|totalUpdated\|totalSkipped\|totalFailed\|totalRemoved\|priorCreated" `
Expected: no output (every per-mailbox counter goes through `counters`; the tunnel-level `created += c;` etc. in `ProcessTunnelAsync` are untouched and are filtered out by the last grep only if you didn't rename them — they must still read `created += c;`).

Run: `awk '/private async Task<\(int created, int updated, int skipped, int failed, int removed\)> ProcessMailboxAsync/,/^    }$/' worker/Services/SyncEngine.cs | wc -l`
Expected: fewer than 60 lines.

- [ ] **Step 5: Run the full unit suite (unchanged tests)**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 328, Skipped: 1` — identical to Step 1. Any failure means a block moved with a behaviour change; diff against `git show HEAD:worker/Services/SyncEngine.cs`.

Run: `dotnet build worker --nologo -v quiet 2>&1 | grep -E "warning CS" | grep SyncEngine.cs`
Expected: no new warnings (an unused parameter or variable would show here).

- [ ] **Step 6: Commit**

```bash
git add worker/Services/SyncEngine.cs
git commit -m "refactor(worker): split ProcessMailboxAsync into folder/reconcile/state/classify/create/update/heal/stale steps (no behaviour change)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 12: Full verification and PR

**Files:** none new.

- [ ] **Step 1: Full backend test run**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 328, Skipped: 1` (baseline 270 + 58 new).

Run: `docker compose up -d postgres && sleep 3 && .superpowers/sdd/2026-08-26-sync-reliability-phase-2/run-integration.sh 2>&1 | tail -3`
Expected: `Passed!  - Failed: 0, Passed: 48, Skipped: 0` (baseline 36 with Postgres + 12 new, one replaced). Without Postgres: `Passed: 47, Skipped: 1`.

- [ ] **Step 2: Frontend build + vitest**

Run: `cd frontend && npm run build 2>&1 | tail -3 && npm test 2>&1 | tail -4; cd ..`
Expected: `✓ Compiled successfully`; `Tests  16 passed (16)`.

- [ ] **Step 3: One migration, clean tree, one commit per task**

Run: `ls api/Migrations | grep -c "_Phase3" ; git status --short ; git log --oneline main..HEAD | cat`
Expected: `2`; empty status; 13 commits (spec, migration, worker records, api records, pagination, scope, graph pickers, contact filters, lifetimes, reconcile, RecordFailedItem, split — plus this plan if it was committed on the branch).

- [ ] **Step 4: Push and open the PR**

```bash
git push -u origin sync-reliability/phase-3
gh pr create --base main --title "Sync reliability Phase 3: API/UI correctness" --body "$(cat <<'PRBODY'
## Why
Run detail derived per-tunnel outcomes from item counts (so a tunnel that resolved zero targets simply
vanished), the tunnels list showed one global "last run" for every tunnel, every paged endpoint made the
UI over-fetch to guess "next page", the edit page could save a tunnel scoped to nobody, the DDG member
picker returned 10 rows, security groups stopped at 200, a `'` in a user search broke the OData filter,
Contact Filters ignored the configured subfolder, every API request opened a new Exchange Online
session, the tenant enumeration ran once per tunnel (and never for group scope), and a create batch
whose outcome was lost left Graph contacts that the next run duplicated.
Spec: docs/superpowers/specs/2026-08-25-sync-reliability-design.md (Phase 3).

## What
- One migration (`Phase3RunTunnels`): `sync_run_tunnels` (one row per tunnel per run) and
  `tunnel_mailbox_folders.reconcile_pending_at`
- §3.1 `SyncEngine` writes a per-tunnel record (success/warning/failed/cancelled); run detail builds
  `tunnelSummaries` from records (photos/errors still from items; runs without records fall back);
  the tunnels list shows each tunnel's own last run and its resolved target count
- §3.3 `/sync-runs`, `/sync-runs/{id}/items`, `/phone-lists/{id}/contacts` return `{ items, hasMore }`
  (contacts add `total`, clamp `[1,500]`); hooks request `pageSize` exactly; Targets page shows "Showing N of M"
- §3.2 `TargetScopeValidation` (400 on empty users / empty group / both / bad JSON); the edit page derives the
  scope `Select` from presence, not truthiness, and validates like the wizard (`lib/target-scope.ts`)
- §3.4 DDG members `{ items, hasMore }` paged through Graph (`PageWindow`); security groups paged to 2000;
  `'` escaped in user search; preview counts use `@odata.count` and page the configured contact folder
- §3.5 Contact Filters read the configured subfolder with paging; exclusion replace is one transaction,
  de-duplicated by EntraId
- §3.6 `DDGResolver` singleton: failed connect disposes the runspace, session/token errors reset + retry once,
  `Disconnect-ExchangeOnline` on dispose; `[AutomaticRetry(Attempts = 0)]` on `ICleanupJobRunner.RunAsync`;
  target-mailbox refresh once per run and used by group scope
- §3.7 `FolderReconciler`: after an unknown-outcome batch (or, via `reconcile_pending_at`, on the next run after
  a crash) strays are adopted by deterministic key or removed
- §3.8 `RecordFailedItem`; `ProcessMailboxAsync` split into eight steps; API guard and worker claim share
  `RunLocks.AcquireRunStartLockSql`

## Tests
- Unit: 328 passed / 1 pre-existing skip (was 270): per-tunnel records ×5, refresh memo ×2, reconcile ×4 (+5
  reconciler, +5 key cases, +2 writer), scope validation ×11, paging clamp ×5, page window ×6, escape ×4,
  session-error ×7, retry attribute, lock key.
- Integration: 48 passed with Postgres (was 36): Phase 3 schema, summaries from records + fallback, per-tunnel
  last sync, envelopes ×4, scope 400s ×2, exclusions ×2, DI lifetime.
- Frontend: `npm run build` clean, vitest 16/16 (`target-scope.test.ts`).

## Deploy (spec §3.10)
1. Phase 2 must be live first (this branch is based on the Phase 2 merge).
2. Start with no run in progress, then `./deploy.sh` (no manual `git pull` first). `shared/`, `api/`, `worker/`
   and `frontend/` changed, so everything rebuilds. The migration is additive (no data fix-up).
3. After: the first run's detail shows a per-tunnel breakdown with `Status · N mailboxes` for every tunnel;
   the Tunnels page shows per-tunnel Last Run; `curl -b cookies '/api/sync-runs?page=1&pageSize=2'` returns
   `{"items":[…],"hasMore":true}`; Targets page shows `Showing N of M`; editing a tunnel to Security Group
   without picking one is refused; `docker logs afh-worker | grep "Target mailbox refresh complete"` appears once
   per run; `/api/graph/ddgs` still works after > 1 h idle (session reset path logs
   `Exchange Online session error … retrying once` if the session had expired).
4. Older runs (and photo-sync runs) keep the items-derived breakdown; only new runs have records.
5. Rollback: `Down()` drops `sync_run_tunnels` and `reconcile_pending_at`.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
PRBODY
)"
```

---

## Self-review

### 1. Spec coverage (Phase 3 → task)

| Spec bullet | Task |
|---|---|
| §3.1 `sync_run_tunnels` table (+ `tunnel_name`, SET NULL FK, indexes) | 2 |
| §3.1 written by `SyncEngine` after every tunnel incl. zero-activity, skipped-for-error, cancelled | 3 |
| §3.1 `SyncRunsController` builds `tunnelSummaries` from records; photos/errors from items; fallback | 4 |
| §3.1 `TunnelsController` per-tunnel `LastSync`; `EstimatedTargetUsers` from `targets_count` (fallback to states) | 4 |
| §3.2 edit page: same validation as the wizard; `Select` from the scope enum | 6 |
| §3.2 `Create`/`Update` reject empty `targetUserEmails` array and empty-string `targetGroupId` (400) | 6 |
| §3.3 `/sync-runs`, `/items`, `/contacts` → `{ items, hasMore }` (+`total`); hooks request `pageSize` exactly | 5 |
| §3.3 `PhoneListsController` clamps `[1,500]`; lists page "N of M" | 5 |
| §3.4 `GET /graph/ddgs/{id}/members` `page,pageSize` via PageIterator | 7 |
| §3.4 security groups paged fully (cap 2000) | 7 |
| §3.4 `users/search` escapes `'` | 7 |
| §3.4 preview counts use `@odata.count` (and page mailbox folders) | 7 |
| §3.5 `ResolveMailboxContactsAsync` honours `ContactFolderId` and pages | 8 |
| §3.5 exclusion replace in one transaction with `DistinctBy(EntraId)` | 8 |
| §3.6 `DDGResolver` Singleton (api + worker) | 9 |
| §3.6 failed connect disposes the runspace; session/auth error resets + retries once; `Disconnect-ExchangeOnline` on dispose | 9 |
| §3.6 `[AutomaticRetry(Attempts = 0)]` on `ICleanupJobRunner.RunAsync` | 9 |
| §3.6 `RefreshTargetMailboxesAsync` once per run; group scope uses it | 9 |
| §3.7 reconcile after an unknown-outcome batch; deterministic key; adopt/remove | 10 |
| §3.7 (amended) `reconcile_pending_at` set before the first chunk, cleared after a clean finish; next-run reconcile; never in a dry run | 2 (column), 10 |
| §3.8 `RecordFailedItem`; `ProcessMailboxAsync` split | 11 |
| §3.8 shared advisory-lock key | 9 |
| §3.9 tests | every task; 12 |
| §3.10 deploy verification | 12 (PR body) |
| Phase 2 review backlog: `AddFailedItem` helper, `ProcessMailboxAsync` split, 3.7, shared advisory key | 11, 11, 10, 9 |

### 2. Placeholder scan

No "TBD", "TODO", "similar to Task N" or "add error handling" steps. Every code step carries the code; every run step carries the command and the expected output. Task 11 is the one task that moves existing code rather than restating it: each moved block is identified by its opening comment and closing statement, the renames are enumerated, and two grep/awk checks plus the unchanged 326-test suite gate it. Expected counts derive from the 270/35(+1)/8 baseline confirmed on 2026-08-27 (unit ends at 328 = 270 + 58; integration at 47 without Postgres / 48 with; vitest at 16); if an executor's count differs by the tests they actually added, the invariant is `Failed: 0`.

### 3. Type consistency

- `TunnelOutcome(Created, Updated, Skipped, Failed, Removed, TargetsCount)` (Task 3) is what `ProcessTunnelAsync` returns and `RecordTunnelRunAsync(int runId, Tunnel, SyncStatus, TunnelOutcome, IEnumerable<string>, DateTime)` consumes; the loop deconstructs it positionally.
- `SyncRunTunnel` column/property names in Task 2 (`TunnelName`, `TargetsCount`, `Contacts*`, `ErrorSummary`, `StartedAt`, `CompletedAt`) are the ones Task 3 writes and Task 4 reads (`r.TunnelName`, `r.TargetsCount`, `latest.CompletedAt`, `latest.ContactsUpdated`).
- `TunnelRunSummaryDto(…, string[] Errors, string? Status = null, int? TargetsCount = null)` (Task 4) — the frontend type adds `status`/`targetsCount` with the same nullability.
- `PagedResult<T>(Items, HasMore, Total = null)` (Task 5) is returned by `PageWindow<T>.ToResult()` (Task 7) and consumed by the frontend `PagedResult<T>` type; `Paging.Clamp` (Task 5) is what `PageWindow`'s constructor calls.
- `TargetScopeValidation.Validate(string? targetUserEmails, string? targetGroupId)` — argument order matches both controller call sites; the frontend `validateTargetScope(targetGroupId, targetUserEmails)` is deliberately the other order (it mirrors `deriveTargetScope`) and both files are self-contained.
- `BatchOperationResult(…, bool OutcomeUnknown = false)` (Task 10): `FakeContactWriter.CreateOutcomeUnknown` produces it; `SyncEngine` reads `createResults.Values.Any(r => r.OutcomeUnknown)`.
- `IFolderReconciler.ReconcileAsync(Tunnel, TargetMailbox, string folderId, int canonicalPhoneListId, IReadOnlyList<SourceUser>, CancellationToken)` — `SyncEngine` passes `List<SourceUser> sourceUsers` (implicitly `IReadOnlyList<SourceUser>`) and `canonicalPhoneList.Id`; `FakeFolderReconciler` records `(tunnel.Id, mailbox.Id, folderId)` and the engine test asserts `(1, 1, "fake-folder-id")` — `"fake-folder-id"` is what `FakeContactFolderManager` returns for a present folder.
- `SyncEngine` constructor order after Task 10: `(dbContextFactory, sourceResolver, contactPayloadBuilder, contactWriter, contactFolderManager, folderReconciler, staleContactHandler, runLogger, runClaimService, throttleCounter, photoSyncService, graphClientFactory, configuration, logger, ddgResolver, filterConverter)`; `CreateEngine` in `SyncEngineTests` passes them positionally in that order. `PhotoSyncServiceTests` does not construct `SyncEngine`.
- `RunLocks.AcquireRunStartLockSql` (Task 9) replaces the literal in both `SyncRunsController.TriggerSync` and `RunClaimService.ClaimAsync`.
- Task 11's extracted signatures reuse the exact tuple element names (`key, sourceUserId, payload, dataHash` / `key, sourceUserId, graphContactId, stateId, payload, dataHash, previousHash`) the moved code already dereferences, so no body edits beyond the enumerated counter renames are needed.
