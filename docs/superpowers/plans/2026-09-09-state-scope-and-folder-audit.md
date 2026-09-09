# State Scope and Folder Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Contact sync state is scoped by tunnel and mailbox (rows under a retired phone list are deduped, re-pointed or stale-removed instead of lingering), and a daily folder audit drops rows whose Graph contact is gone so the run recreates them.

**Architecture:** `SyncEngine.LoadExistingStatesAsync` and `StaleContactHandler` stop filtering `contact_sync_state` by the tunnel's attached phone lists. `FolderReconciler` reconciles both directions (strays and missing rows) and the engine's step B runs it when the Phase 3 pending flag is set, when the run row asks for an audit, or when a scheduled run has not audited that folder today (`tunnel_mailbox_folders.last_audited_at`). The API stores an `audit_folders` flag on the run row; the dashboard exposes it as a checkbox.

**Tech Stack:** .NET 10 / ASP.NET Core 10, EF Core 10 + Npgsql (migration in `api/Migrations`), xUnit with the EF InMemory provider (unit) and Postgres (integration), Next.js 15 + TypeScript + vitest.

**Spec:** `docs/superpowers/specs/2026-09-09-state-scope-and-folder-audit-design.md` (§5.0–§5.5). Read it first; every task cites its section.

## Global Constraints

- Branch `sync-reliability/phase-5` (already created; the spec commit 726c111 is on it). Commit after every task with `type(scope): summary`, body explaining why with a `§` reference, and the trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- TDD for every code change: write the failing test, run it, see it fail, implement, run it, see it pass.
- Unit tests run on the EF **InMemory** provider, which cannot execute `ExecuteDeleteAsync` / `ExecuteUpdateAsync`. Every per-mailbox bookkeeping path this plan touches uses tracked operations (`ToListAsync` + `RemoveRange` / property set + `SaveChangesAsync`); the rows involved are per (tunnel, mailbox) and small. This is the spec's `ExecuteDeleteAsync` / `ExecuteUpdateAsync` wording implemented with tracked operations; behaviour is identical.
- Bookkeeping writes that must survive a shutdown use a fresh context and `CancellationToken.None`, as the existing §2.6a and §3.7 helpers do.
- Never write `contact_sync_state`, Graph, or the audit stamp in a dry run.
- Run item action names are lower-case snake_case strings; the new one is exactly `audit_missing`.
- Verification commands (from `CLAUDE.md`), run from the repo root:
  - `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
  - `docker compose up -d postgres && dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -1`
  - `cd frontend && npm run build 2>&1 | tail -2 && npm test 2>&1 | grep "Tests "; cd ..`
- Baseline before this plan: unit 352 passed / 1 skipped; integration 49 passed with Postgres; frontend build clean, 19 tests.

---

### Task 1: Schema — `audit_folders` and `last_audited_at` (§5.0)

**Files:**
- Modify: `shared/Entities/SyncRun.cs` (after `IsDryRun`)
- Modify: `shared/Entities/TunnelMailboxFolder.cs` (after `ReconcilePendingAt`)
- Modify: `shared/Data/Configurations/SyncRunConfiguration.cs:17`
- Modify: `shared/Data/Configurations/TunnelMailboxFolderConfiguration.cs:20`
- Create: `api/Migrations/<timestamp>_Phase5FolderAudit.cs` (+ Designer, generated)
- Modify: `api/Migrations/AFHSyncDbContextModelSnapshot.cs` (generated)
- Test: `tests/AFHSync.Tests.Integration/MigrationTests.cs`

**Interfaces:**
- Produces: `SyncRun.AuditFolders` (bool, default false), `TunnelMailboxFolder.LastAuditedAt` (DateTime?, null). Tasks 5 and 6 read and write them.

- [ ] **Step 1: Write the failing migration-operations test**

Add to `tests/AFHSync.Tests.Integration/MigrationTests.cs` after `Phase3Migration_CreatesSyncRunTunnels_AndReconcileFlag`:

```csharp
    [Fact]
    public void Phase5Migration_AddsAuditFoldersFlag_AndLastAuditedAt()
    {
        var migration = new Phase5FolderAudit();

        var adds = migration.UpOperations.OfType<AddColumnOperation>().OrderBy(o => o.Name).ToList();
        Assert.Equal(2, adds.Count);

        Assert.Equal("sync_runs", adds[0].Table);
        Assert.Equal("audit_folders", adds[0].Name);
        Assert.False(adds[0].IsNullable);
        Assert.Equal(false, adds[0].DefaultValue);

        Assert.Equal("tunnel_mailbox_folders", adds[1].Table);
        Assert.Equal("last_audited_at", adds[1].Name);
        Assert.True(adds[1].IsNullable);

        Assert.Empty(migration.UpOperations.OfType<SqlOperation>());   // no data fix-up: the first run cleans up (§5.0)
    }
```

In the same file, in `MigrateAsync_CreatesPhase2Columns_Table_And_UniqueIndex`, change the exact folder-columns assertion to include the new column and add the run-column check:

```csharp
                Assert.Contains("requested_tunnel_ids", runColumns);
                Assert.Contains("audit_folders", runColumns);
```

```csharp
                Assert.Equal(
                    new[] { "folder_name", "graph_folder_id", "id", "last_audited_at", "reconcile_pending_at", "target_mailbox_id", "tunnel_id", "updated_at" },
                    folderColumns.OrderBy(c => c).ToArray());
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet --filter "FullyQualifiedName~MigrationTests" 2>&1 | tail -3`
Expected: build error `The type or namespace name 'Phase5FolderAudit' could not be found`.

- [ ] **Step 3: Add the entity properties and column mappings**

`shared/Entities/SyncRun.cs`, directly after `public bool IsDryRun { get; set; }`:

```csharp
    /// <summary>§5.2: this run reconciles every folder it touches (strays and missing rows), not only flagged ones.</summary>
    public bool AuditFolders { get; set; }
```

`shared/Entities/TunnelMailboxFolder.cs`, directly after `public DateTime? ReconcilePendingAt { get; set; }`:

```csharp
    /// <summary>§5.2: when this folder was last reconciled against Graph; a scheduled run audits it once per UTC day.</summary>
    public DateTime? LastAuditedAt { get; set; }
```

`shared/Data/Configurations/SyncRunConfiguration.cs`, after the `IsDryRun` line (line 17):

```csharp
        builder.Property(e => e.AuditFolders).HasColumnName("audit_folders").HasDefaultValue(false);
```

`shared/Data/Configurations/TunnelMailboxFolderConfiguration.cs`, after the `ReconcilePendingAt` line (line 20):

```csharp
        builder.Property(e => e.LastAuditedAt).HasColumnName("last_audited_at");
```

- [ ] **Step 4: Generate the migration**

Run: `dotnet ef migrations add Phase5FolderAudit --project api --startup-project api 2>&1 | tail -3`
Expected: `Done. To undo this action, use 'ef migrations remove'`.

Open the generated `api/Migrations/<timestamp>_Phase5FolderAudit.cs` and confirm `Up` contains exactly these two operations (order may differ) and nothing else:

```csharp
            migrationBuilder.AddColumn<bool>(
                name: "audit_folders",
                table: "sync_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_audited_at",
                table: "tunnel_mailbox_folders",
                type: "timestamp with time zone",
                nullable: true);
```

If `Up` contains anything else, the model snapshot had drifted; stop and report rather than committing extra operations.

- [ ] **Step 5: Run the migration tests to verify they pass**

Run: `docker compose up -d postgres 2>&1 | tail -1 && dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet --filter "FullyQualifiedName~MigrationTests" 2>&1 | tail -1`
Expected: `Passed! - Failed: 0, Passed: 4, ...`

- [ ] **Step 6: Run the whole solution build and unit tests to be sure nothing else broke**

Run: `dotnet build AFHSync.slnx --nologo -v quiet 2>&1 | tail -2 && dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: build succeeds; `Passed! - Failed: 0, Passed: 352, Skipped: 1`.

- [ ] **Step 7: Commit**

```bash
git add shared/Entities/SyncRun.cs shared/Entities/TunnelMailboxFolder.cs shared/Data/Configurations/SyncRunConfiguration.cs shared/Data/Configurations/TunnelMailboxFolderConfiguration.cs api/Migrations tests/AFHSync.Tests.Integration/MigrationTests.cs
git commit -F - <<'EOF'
feat(shared): audit_folders on sync_runs, last_audited_at on tunnel_mailbox_folders

§5.0. The run row carries the audit request (a run's parameters come
from the row, §2.7) and the folder row remembers its last audit so a
scheduled run audits each folder once per UTC day. No data fix-up: the
first real run cleans the retired phone-list rows itself.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 2: Stale pass scoped by tunnel and mailbox (§5.1)

**Files:**
- Modify: `worker/Services/IStaleContactHandler.cs`
- Modify: `worker/Services/StaleContactHandler.cs:19-36`
- Modify: `worker/Services/SyncEngine.cs:701` (call site) and `:1222-1270` (`HandleStaleContactsAsync`)
- Test: `tests/AFHSync.Tests.Unit/Sync/StaleContactHandlerTests.cs`
- Test infra: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` (`FakeStaleContactHandler`, `RecordingStaleContactHandler`)

**Interfaces:**
- Produces: `Task<StaleResult> IStaleContactHandler.HandleStaleAsync(Tunnel tunnel, int targetMailboxId, string mailboxEntraId, HashSet<int> currentSourceUserIds, CancellationToken ct)` — no `phoneListId`. Task 3's engine test with the real handler relies on it.

- [ ] **Step 1: Write the failing handler tests**

In `tests/AFHSync.Tests.Unit/Sync/StaleContactHandlerTests.cs`, first remove `phoneListId: 1, ` from all six existing `HandleStaleAsync(` calls (the parameter is going away):

```bash
sed -i '' 's/HandleStaleAsync(tunnel, phoneListId: 1, /HandleStaleAsync(tunnel, /' tests/AFHSync.Tests.Unit/Sync/StaleContactHandlerTests.cs
grep -c "phoneListId:" tests/AFHSync.Tests.Unit/Sync/StaleContactHandlerTests.cs   # expect 0
```

Then add these two tests before the file's `FakeContactWriter` class (line ~410):

```csharp
    // ==============================
    // §5.1: rows are scoped by tunnel + mailbox, whatever phone list created them
    // ==============================

    [Fact]
    public async Task HandleStaleAsync_AutoRemove_RemovesRowsUnderAnyPhoneList_ButOnlyThisTunnel()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedCtx = MakeDbContext(dbName);
        seedCtx.ContactSyncStates.AddRange(
            CreateState(1, sourceUserId: 1, tunnelId: 1, phoneListId: 13, targetMailboxId: 1, graphContactId: "g-current"),
            CreateState(2, sourceUserId: 2, tunnelId: 1, phoneListId: 10, targetMailboxId: 1, graphContactId: "g-retired"),      // list 10 is no longer attached to tunnel 1
            CreateState(3, sourceUserId: 2, tunnelId: 2, phoneListId: 10, targetMailboxId: 1, graphContactId: "g-other-tunnel"));
        await seedCtx.SaveChangesAsync();

        var writer = new FakeContactWriter();
        var handler = new StaleContactHandler(CreateFactory(dbName), writer, NullLogger<StaleContactHandler>.Instance);

        var result = await handler.HandleStaleAsync(CreateTunnel(1, StalePolicy.AutoRemove), targetMailboxId: 1,
            mailboxEntraId: "mailbox@contoso.com", currentSourceUserIds: new HashSet<int> { 1 }, ct: CancellationToken.None);

        Assert.Equal(1, result.Removed);
        Assert.Equal(new[] { "g-retired" }, writer.DeletedContactIds);
        using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(new[] { 1, 3 }, (await verifyCtx.ContactSyncStates.OrderBy(s => s.Id).Select(s => s.Id).ToListAsync()).ToArray());
    }

    [Fact]
    public async Task HandleStaleAsync_FlagHold_FlagsRowUnderRetiredPhoneList()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedCtx = MakeDbContext(dbName);
        seedCtx.ContactSyncStates.Add(
            CreateState(2, sourceUserId: 2, tunnelId: 1, phoneListId: 10, targetMailboxId: 1, graphContactId: "g-retired"));
        await seedCtx.SaveChangesAsync();

        var writer = new FakeContactWriter();
        var handler = new StaleContactHandler(CreateFactory(dbName), writer, NullLogger<StaleContactHandler>.Instance);

        var result = await handler.HandleStaleAsync(CreateTunnel(1, StalePolicy.FlagHold), targetMailboxId: 1,
            mailboxEntraId: "mailbox@contoso.com", currentSourceUserIds: new HashSet<int>(), ct: CancellationToken.None);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.StaleDetected);
        Assert.Empty(writer.DeletedContactIds);
        using var verifyCtx = MakeDbContext(dbName);
        var row = await verifyCtx.ContactSyncStates.SingleAsync();
        Assert.True(row.IsStale);
        Assert.NotNull(row.StaleDetectedAt);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~StaleContactHandlerTests" 2>&1 | tail -3`
Expected: build error — the existing signature still has `int phoneListId` (`There is no argument given that corresponds to the required parameter 'targetMailboxId'`, or the named-argument mismatch).

- [ ] **Step 3: Change the interface and the handler**

Replace `worker/Services/IStaleContactHandler.cs` with:

```csharp
using AFHSync.Shared.Entities;

namespace AFHSync.Worker.Services;

public interface IStaleContactHandler
{
    /// <summary>
    /// §5.1: considers every contact_sync_state row of the (tunnel, mailbox) pair, whatever phone list
    /// it was created under, and applies the tunnel's stale policy to rows whose source user is not in
    /// <paramref name="currentSourceUserIds"/>.
    /// </summary>
    Task<StaleResult> HandleStaleAsync(
        Tunnel tunnel,
        int targetMailboxId,
        string mailboxEntraId,
        HashSet<int> currentSourceUserIds,
        CancellationToken ct);
}

public record StaleResult(int Removed, int StaleDetected);
```

In `worker/Services/StaleContactHandler.cs` change the method header and the query (lines 19–36) to:

```csharp
    public async Task<StaleResult> HandleStaleAsync(
        Tunnel tunnel,
        int targetMailboxId,
        string mailboxEntraId,
        HashSet<int> currentSourceUserIds,
        CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        // §5.1: every row of this (tunnel, mailbox), whatever phone list created it — a row under a
        // list no longer attached to the tunnel is a stale candidate like any other. Already-stale
        // rows are included for the FlagHold hold-period check.
        var existingStates = await db.ContactSyncStates
            .Where(s => s.TunnelId == tunnel.Id
                        && s.TargetMailboxId == targetMailboxId)
            .ToListAsync(ct);
```

Everything after that line in the method is unchanged.

- [ ] **Step 4: Update the engine call site and its fakes**

In `worker/Services/SyncEngine.cs`, replace the call at line 701:

```csharp
        await HandleStaleContactsAsync(tunnel, canonicalPhoneList, mailbox, run, sourceUsers, isDryRun, skipStale, counters, ct);
```

and replace the whole `HandleStaleContactsAsync` method (lines 1219–1270) with:

```csharp
    /// <summary>
    /// Phase 3 (§3.8) step H: the stale pass — removes contacts no longer in the source set.
    /// §5.1: one call per (tunnel, mailbox); the handler sees every row of the pair regardless of
    /// phone list, so rows under a retired list are handled too. Items carry the canonical list id.
    /// </summary>
    private async Task HandleStaleContactsAsync(
        Tunnel tunnel,
        PhoneList canonicalPhoneList,
        TargetMailbox mailbox,
        SyncRun run,
        List<SourceUser> sourceUsers,
        bool isDryRun,
        bool skipStale,
        MailboxCounters counters,
        CancellationToken ct)
    {
        // Phase 2 (§2.3): skipped when any source failed — the current set is incomplete.
        if (isDryRun || skipStale)
            return;

        var currentSourceIds = new HashSet<int>(sourceUsers.Select(u => u.Id));
        var staleResult = await staleContactHandler.HandleStaleAsync(
            tunnel, mailbox.Id, mailbox.EntraId, currentSourceIds, ct);

        for (int i = 0; i < staleResult.Removed; i++)
        {
            runLogger.AddItem(new SyncRunItem
            {
                SyncRunId = run.Id,
                TunnelId = tunnel.Id,
                PhoneListId = canonicalPhoneList.Id,
                TargetMailboxId = mailbox.Id,
                Action = "removed",
                CreatedAt = DateTime.UtcNow
            });
        }

        for (int i = 0; i < staleResult.StaleDetected; i++)
        {
            runLogger.AddItem(new SyncRunItem
            {
                SyncRunId = run.Id,
                TunnelId = tunnel.Id,
                PhoneListId = canonicalPhoneList.Id,
                TargetMailboxId = mailbox.Id,
                Action = "stale_detected",
                CreatedAt = DateTime.UtcNow
            });
        }

        counters.Removed += staleResult.Removed;
    }
```

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` update both fakes (around lines 1769–1788):

```csharp
    private sealed class FakeStaleContactHandler : IStaleContactHandler
    {
        public Task<StaleResult> HandleStaleAsync(
            Tunnel tunnel, int targetMailboxId,
            string mailboxEntraId, HashSet<int> currentSourceUserIds, CancellationToken ct)
            => Task.FromResult(new StaleResult(0, 0));
    }

    private sealed class RecordingStaleContactHandler : IStaleContactHandler
    {
        public int CallCount { get; private set; }

        public Task<StaleResult> HandleStaleAsync(
            Tunnel tunnel, int targetMailboxId,
            string mailboxEntraId, HashSet<int> currentSourceUserIds, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new StaleResult(0, 0));
        }
    }
```

- [ ] **Step 5: Run the unit tests to verify they pass**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed! - Failed: 0, Passed: 354, Skipped: 1`.

- [ ] **Step 6: Commit**

```bash
git add worker/Services/IStaleContactHandler.cs worker/Services/StaleContactHandler.cs worker/Services/SyncEngine.cs tests/AFHSync.Tests.Unit/Sync/StaleContactHandlerTests.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -F - <<'EOF'
refactor(worker): stale pass scoped by tunnel and mailbox, not per attached phone list

§5.1. The handler used to see only rows under the tunnel's currently
attached phone lists, so a row created under a list that was later
detached (production: list 10, April 2026) was never a stale candidate
and its Graph contact lived on. One call per (tunnel, mailbox) now
covers every row of the pair; run items carry the canonical list id.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 3: Existing state scoped by tunnel and mailbox; retired rows deduped and re-pointed (§5.1)

**Files:**
- Modify: `worker/Services/SyncEngine.cs:783-895` (`LoadExistingStatesAsync`)
- Test: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` (three tests, two seed helpers, `FakeRunLogger.FinalizedRemoved`)

**Interfaces:**
- Consumes: `IStaleContactHandler.HandleStaleAsync(tunnel, targetMailboxId, mailboxEntraId, currentSourceUserIds, ct)` from Task 2 (the third test runs the real handler).
- Produces: `LoadExistingStatesAsync(Tunnel tunnel, PhoneList canonicalPhoneList, List<int> attachedPhoneListIds, TargetMailbox mailbox, string? folderId, bool folderWasCreated, bool isDryRun, MailboxCounters counters, CancellationToken ct)` — same call site, parameter renamed.

- [ ] **Step 1: Add `FinalizedRemoved` to the run-logger fake**

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`, in `FakeRunLogger`, add the property next to `FinalizedFailed`:

```csharp
        public int FinalizedRemoved { get; private set; }
```

and in its `FinalizeRunAsync`, after `FinalizedFailed = contactsFailed;`:

```csharp
            FinalizedRemoved = contactsRemoved;
```

- [ ] **Step 2: Write the failing engine tests**

Add to `SyncEngineTests` (a new section before the `// Test infrastructure — fakes` region; anywhere among the tests is fine):

```csharp
    // ==============================
    // §5.1: existing state is scoped by tunnel + mailbox; retired phone-list rows are cleaned up
    // ==============================

    /// <summary>Tunnel 1 is attached to phone list 13 only; list 10 exists but is retired; one active mailbox (Id 1, EntraId "mbx").</summary>
    private static async Task SeedTunnelWithRetiredListAsync(string dbName, params ContactSyncState[] states)
    {
        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel { Id = 1, Name = "Buckhead", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
        var current = new PhoneList { Id = 13, Name = "All Users" };
        var retired = new PhoneList { Id = 10, Name = "Nick Jp test and david" };
        var tpl = new TunnelPhoneList { TunnelId = 1, PhoneListId = 13, Tunnel = tunnel, PhoneList = current };
        tunnel.TunnelPhoneLists.Add(tpl);
        seedCtx.Tunnels.Add(tunnel);
        seedCtx.PhoneLists.AddRange(current, retired);
        seedCtx.TunnelPhoneLists.Add(tpl);
        seedCtx.TargetMailboxes.Add(new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
        seedCtx.ContactSyncStates.AddRange(states);
        await seedCtx.SaveChangesAsync();
    }

    private static ContactSyncState State(int id, int sourceUserId, int phoneListId, string graphContactId, string? dataHash) => new()
    {
        Id = id, SourceUserId = sourceUserId, TunnelId = 1, PhoneListId = phoneListId, TargetMailboxId = 1,
        GraphContactId = graphContactId, DataHash = dataHash, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task RunAsync_RetiredListRowBesideCanonicalRow_DeletesTheRetiredContactAndRow()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithRetiredListAsync(dbName,
            State(1, sourceUserId: 1, phoneListId: 13, graphContactId: "g-13", dataHash: "new-hash"),
            State(2, sourceUserId: 1, phoneListId: 10, graphContactId: "g-10", dataHash: null));   // April leftover: a second contact for Alice
        var writer = new FakeContactWriter();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            contactWriter: writer, runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(new[] { "g-10" }, writer.DeletedContactIds);           // the duplicate under the retired list
        Assert.Empty(writer.CreatedContactIds);
        Assert.Empty(writer.UpdatedContactIds);                               // hash matches: nothing to PATCH
        Assert.Equal(1, runLogger.FinalizedRemoved);
        Assert.Equal(0, runLogger.FinalizedFailed);
        await using var verifyCtx = MakeDbContext(dbName);
        var remaining = await verifyCtx.ContactSyncStates.SingleAsync();
        Assert.Equal(1, remaining.Id);
        Assert.Equal(13, remaining.PhoneListId);
    }

    [Fact]
    public async Task RunAsync_RetiredListRowAlone_ForCurrentMember_IsReusedAndRepointed()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithRetiredListAsync(dbName,
            State(2, sourceUserId: 1, phoneListId: 10, graphContactId: "g-10", dataHash: "old-hash"));
        var writer = new FakeContactWriter();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            contactWriter: writer, runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Empty(writer.CreatedContactIds);                               // no second contact for Alice
        Assert.Equal(new[] { "g-10" }, writer.UpdatedContactIds);             // old-hash ⇒ PATCH the contact we already have
        Assert.Empty(writer.DeletedContactIds);
        Assert.Equal(0, runLogger.FinalizedFailed);
        await using var verifyCtx = MakeDbContext(dbName);
        var row = await verifyCtx.ContactSyncStates.SingleAsync();
        Assert.Equal(2, row.Id);
        Assert.Equal(13, row.PhoneListId);                                    // re-pointed to the canonical list
        Assert.Equal("new-hash", row.DataHash);
    }

    [Fact]
    public async Task RunAsync_RetiredListRowAlone_ForDepartedMember_IsRemovedByTheStalePass()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithRetiredListAsync(dbName,
            State(2, sourceUserId: 9, phoneListId: 10, graphContactId: "g-ghost", dataHash: null));   // user 9 left months ago
        var writer = new FakeContactWriter();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            contactWriter: writer,
            staleHandler: new StaleContactHandler(CreateFactory(dbName), writer, NullLogger<StaleContactHandler>.Instance),
            runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Equal(new[] { "g-ghost" }, writer.DeletedContactIds);
        Assert.Single(writer.CreatedContactIds);                               // Alice is created as usual
        Assert.Equal(1, runLogger.FinalizedRemoved);
        Assert.Equal(0, runLogger.FinalizedFailed);
        await using var verifyCtx = MakeDbContext(dbName);
        var alice = Assert.Single(await verifyCtx.ContactSyncStates.ToListAsync());
        Assert.Equal(1, alice.SourceUserId);
        Assert.Equal(13, alice.PhoneListId);
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~RetiredList" 2>&1 | tail -3`
Expected: `Failed: 3`. The first fails because the list-10 row is invisible (`DeletedContactIds` empty), the second because Alice is created a second time (`CreatedContactIds` not empty), the third because the ghost row and contact survive.

- [ ] **Step 4: Rewrite `LoadExistingStatesAsync`**

Replace the whole method in `worker/Services/SyncEngine.cs` (from the `/// Phase 3 (§3.8) step C` doc comment through `return existingStates;`) with:

```csharp
    /// <summary>
    /// Phase 3 (§3.8) step C, scoped per §5.1: loads every existing sync state row of this
    /// (tunnel, mailbox) pair — whatever phone list created it — de-duplicated to one row per source
    /// user, cleans up the duplicate rows (Graph contact + row), and re-points kept rows that sit under
    /// a phone list no longer attached to the tunnel onto the canonical list.
    /// </summary>
    private async Task<Dictionary<int, ContactSyncState>> LoadExistingStatesAsync(
        Tunnel tunnel,
        PhoneList canonicalPhoneList,
        List<int> attachedPhoneListIds,
        TargetMailbox mailbox,
        string? folderId,
        bool folderWasCreated,
        bool isDryRun,
        MailboxCounters counters,
        CancellationToken ct)
    {
        // If the folder was just created, any existing sync state is stale (contacts were deleted).
        // Clear every row of the pair so all contacts get re-created in the new folder.
        if (folderWasCreated && !isDryRun)
        {
            await using var cleanupDb = await dbContextFactory.CreateDbContextAsync(ct);
            var staleRows = await cleanupDb.ContactSyncStates
                .Where(s => s.TunnelId == tunnel.Id && s.TargetMailboxId == mailbox.Id)
                .ToListAsync(ct);
            if (staleRows.Count > 0)
            {
                cleanupDb.ContactSyncStates.RemoveRange(staleRows);
                await cleanupDb.SaveChangesAsync(ct);
                logger.LogInformation(
                    "Cleared {Count} stale sync states for tunnel {TunnelId} in mailbox {MailboxId} (folder was recreated)",
                    staleRows.Count, tunnel.Id, mailbox.Id);
            }
        }

        // §5.1: the scope is the (tunnel, mailbox) pair. Rows created under a phone list that has
        // since been detached from the tunnel are found here too, instead of lingering forever.
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var allExistingStates = await db.ContactSyncStates
            .AsNoTracking()
            .Where(s => s.TunnelId == tunnel.Id && s.TargetMailboxId == mailbox.Id)
            .ToListAsync(ct);

        // Deduplicate: keep one state per SourceUserId (prefer canonical phone list, then lowest ID).
        var existingStates = allExistingStates
            .GroupBy(s => s.SourceUserId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.PhoneListId == canonicalPhoneList.Id)
                      .ThenBy(s => s.Id)
                      .First());

        // Phase 2 (§2.2): a dry run against a mailbox with no folder — every contact "would create".
        if (isDryRun && folderId is null)
            existingStates = new Dictionary<int, ContactSyncState>();

        // Duplicate rows: every row that is not the kept one for its user. Their Graph contacts are
        // second copies in the same folder (the §5.1 April leftovers, or pre-fix duplicates).
        var duplicateStates = isDryRun && folderId is null
            ? []
            : allExistingStates
                .Where(s => existingStates.Values.All(kept => kept.Id != s.Id))
                .ToList();

        // Phase 2 (§2.2): no duplicate cleanup (Graph or DB) in a dry run — single guard below.
        if (duplicateStates.Count > 0)
        {
            if (isDryRun)
            {
                logger.LogInformation(
                    "Dry run: {Count} duplicate sync states for tunnel {TunnelId} in mailbox {MailboxId} — would clean up",
                    duplicateStates.Count, tunnel.Id, mailbox.Id);
            }
            else
            {
                logger.LogInformation(
                    "Found {Count} duplicate sync states for tunnel {TunnelId} in mailbox {MailboxId} — cleaning up",
                    duplicateStates.Count, tunnel.Id, mailbox.Id);

                var dupeOps = duplicateStates
                    .Where(d => !string.IsNullOrEmpty(d.GraphContactId))
                    .Select(d => (d.Id.ToString(), d.GraphContactId!))
                    .ToList();

                if (dupeOps.Count > 0)
                {
                    var dupeResults = await contactWriter.DeleteContactsBatchAsync(
                        mailbox.EntraId, dupeOps, ct);

                    foreach (var (key, result) in dupeResults)
                    {
                        if (!result.Success)
                            logger.LogWarning("Failed to delete duplicate Graph contact (key={Key}): {Error}", key, result.Error);
                    }
                }

                await using var dupeDb = await dbContextFactory.CreateDbContextAsync(ct);
                var dupeIds = duplicateStates.Select(d => d.Id).ToList();
                var dupeRows = await dupeDb.ContactSyncStates
                    .Where(s => dupeIds.Contains(s.Id))
                    .ToListAsync(ct);
                dupeDb.ContactSyncStates.RemoveRange(dupeRows);
                await dupeDb.SaveChangesAsync(ct);
                counters.Removed += duplicateStates.Count;
            }
        }

        // §5.1: kept rows under a phone list no longer attached to the tunnel are re-pointed to the
        // canonical list. After the dedupe there is exactly one row per source user for the pair, so
        // the unique index (source_user, phone_list, mailbox, tunnel) cannot conflict.
        if (!isDryRun)
        {
            var retiredIds = existingStates.Values
                .Where(s => !attachedPhoneListIds.Contains(s.PhoneListId))
                .Select(s => s.Id)
                .ToHashSet();
            if (retiredIds.Count > 0)
            {
                await using var repointDb = await dbContextFactory.CreateDbContextAsync(ct);
                var retiredRows = await repointDb.ContactSyncStates
                    .Where(s => retiredIds.Contains(s.Id))
                    .ToListAsync(ct);
                foreach (var row in retiredRows)
                {
                    row.PhoneListId = canonicalPhoneList.Id;
                    row.UpdatedAt = DateTime.UtcNow;
                }
                await repointDb.SaveChangesAsync(ct);
                foreach (var kept in existingStates.Values)
                {
                    if (retiredIds.Contains(kept.Id))
                        kept.PhoneListId = canonicalPhoneList.Id;
                }
                logger.LogInformation(
                    "Re-pointed {Count} state row(s) from retired phone list(s) to list {PhoneListId} for tunnel {TunnelId} in mailbox {MailboxId}",
                    retiredIds.Count, canonicalPhoneList.Id, tunnel.Id, mailbox.Id);
            }
        }

        return existingStates;
    }
```

The call site in `ProcessMailboxAsync` (`LoadExistingStatesAsync(tunnel, canonicalPhoneList, allPhoneListIds, mailbox, folderId, folderWasCreated, isDryRun, counters, ct)`) is unchanged; only the parameter name inside the method changed.

- [ ] **Step 5: Run the unit tests to verify they pass**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed! - Failed: 0, Passed: 357, Skipped: 1`.

- [ ] **Step 6: Commit**

```bash
git add worker/Services/SyncEngine.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -F - <<'EOF'
fix(worker): scope existing sync state by tunnel and mailbox; clean up retired phone-list rows

§5.1. Classification loaded only rows under the tunnel's attached phone
lists, so rows under a detached list (production: 3,620 rows under list
10 in four mailboxes since 2026-04-17) were neither reused nor deduped:
their Graph contacts stayed as duplicates or ghosts. Rows are now loaded
per (tunnel, mailbox); the existing dedupe deletes the duplicate row and
contact, and a kept row under a retired list is re-pointed to the
canonical list. Bookkeeping uses tracked operations so the unit tests
run on the InMemory provider.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 4: Reconciler drops missing rows (§5.2)

**Files:**
- Modify: `worker/Services/IFolderReconciler.cs` (result record)
- Modify: `worker/Services/FolderReconciler.cs:61-150` (`ReconcileAsync`)
- Test: `tests/AFHSync.Tests.Unit/Sync/FolderReconcilerTests.cs`
- Test infra: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` (`FakeFolderReconciler`)

**Interfaces:**
- Produces: `FolderReconcileResult(int Examined, int Adopted, int Removed, IReadOnlyList<int> MissingSourceUserIds)` with value equality (sequence equality on the list), a 3-argument constructor for "no missing rows", and `int Missing => MissingSourceUserIds.Count`. `IFolderReconciler.ReconcileAsync` signature unchanged. Task 5 writes `audit_missing` run items from `MissingSourceUserIds`.

- [ ] **Step 1: Write the failing reconciler tests**

Add to `tests/AFHSync.Tests.Unit/Sync/FolderReconcilerTests.cs` after `TwoStraysForOneUser_AdoptsTheFirst_RemovesTheSecond`:

```csharp
    // ==============================
    // §5.2: the reverse direction — rows whose contact is gone from the folder
    // ==============================

    [Fact]
    public async Task MissingRow_ContactGoneFromFolder_IsDroppedAndReported()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedStateAsync(dbName, sourceUserId: 1, graphContactId: "g-present");
        await SeedStateAsync(dbName, sourceUserId: 2, graphContactId: "g-gone");         // deleted on the phone
        var writer = new RecordingContactWriter();
        var reconciler = new FakeFolderReconciler(dbName, writer);
        reconciler.FolderContacts.Add(new GraphContactStub("g-present", "Alice", "alice@contoso.com"));
        var users = new List<SourceUser>
        {
            new() { Id = 1, EntraId = "u1", DisplayName = "Alice", Email = "alice@contoso.com" },
            new() { Id = 2, EntraId = "u2", DisplayName = "Bob", Email = "bob@contoso.com" }
        };

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", 1, users, CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(1, 0, 0, [2]), result);
        Assert.Equal(1, result.Missing);
        Assert.Empty(writer.DeletedContactIds);
        await using var verifyCtx = MakeDbContext(dbName);
        var remaining = await verifyCtx.ContactSyncStates.SingleAsync();
        Assert.Equal("g-present", remaining.GraphContactId);                  // Bob's row is gone; the classification that follows recreates him
    }

    [Fact]
    public async Task MissingRow_OfAnotherTunnel_IsLeftAlone()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedStateAsync(dbName, sourceUserId: 2, graphContactId: "g-elsewhere", tunnelId: 2);   // lives in tunnel 2's folder
        var reconciler = new FakeFolderReconciler(dbName, new RecordingContactWriter());

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", 1, [], CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(0, 0, 0), result);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(1, await verifyCtx.ContactSyncStates.CountAsync());
    }

    [Fact]
    public async Task MissingRow_WithNullGraphId_IsIgnored()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var ctx = MakeDbContext(dbName))
        {
            ctx.ContactSyncStates.Add(new ContactSyncState
            {
                SourceUserId = 3, PhoneListId = 1, TargetMailboxId = Mailbox.Id, TunnelId = Tunnel.Id,
                GraphContactId = null, DataHash = null, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }
        var reconciler = new FakeFolderReconciler(dbName, new RecordingContactWriter());

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", 1, [], CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(0, 0, 0), result);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(1, await verifyCtx.ContactSyncStates.CountAsync());
    }

    [Fact]
    public async Task MissingRow_AndStrayForTheSameUser_StrayIsAdoptedInsteadOfRecreated()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedStateAsync(dbName, sourceUserId: 1, graphContactId: "g-old");           // the id we remember is gone…
        var writer = new RecordingContactWriter();
        var reconciler = new FakeFolderReconciler(dbName, writer);
        reconciler.FolderContacts.Add(new GraphContactStub("g-new", "Alice", "alice@contoso.com"));   // …but Alice exists under another id
        var users = new List<SourceUser> { new() { Id = 1, EntraId = "u1", DisplayName = "Alice", Email = "alice@contoso.com" } };

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", 1, users, CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(1, 1, 0, [1]), result);
        Assert.Empty(writer.DeletedContactIds);
        await using var verifyCtx = MakeDbContext(dbName);
        var row = await verifyCtx.ContactSyncStates.SingleAsync();
        Assert.Equal("g-new", row.GraphContactId);
        Assert.Null(row.DataHash);                                             // PATCHed into shape by the classification that follows
    }

    [Fact]
    public void FolderReconcileResult_EqualityIncludesMissingIds()
    {
        Assert.Equal(new FolderReconcileResult(1, 0, 0, [2, 3]), new FolderReconcileResult(1, 0, 0, new List<int> { 2, 3 }));
        Assert.NotEqual(new FolderReconcileResult(1, 0, 0, [2]), new FolderReconcileResult(1, 0, 0, [3]));
        Assert.Equal(new FolderReconcileResult(1, 0, 0), new FolderReconcileResult(1, 0, 0, []));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~FolderReconcilerTests" 2>&1 | tail -3`
Expected: build error — `FolderReconcileResult` has no 4-argument constructor and no `Missing` property.

- [ ] **Step 3: Extend the result record**

Replace the record in `worker/Services/IFolderReconciler.cs`:

```csharp
/// <param name="Examined">Graph contacts found in the folder.</param>
/// <param name="Adopted">Strays matched to a current source user and given a state row.</param>
/// <param name="Removed">Strays deleted from Graph.</param>
/// <param name="MissingSourceUserIds">§5.2: source users whose row referenced a contact that is no longer in the folder; the row was dropped so classification recreates the contact.</param>
public sealed record FolderReconcileResult(int Examined, int Adopted, int Removed, IReadOnlyList<int> MissingSourceUserIds)
{
    public FolderReconcileResult(int Examined, int Adopted, int Removed)
        : this(Examined, Adopted, Removed, Array.Empty<int>()) { }

    public int Missing => MissingSourceUserIds.Count;

    public bool Equals(FolderReconcileResult? other) =>
        other is not null
        && Examined == other.Examined
        && Adopted == other.Adopted
        && Removed == other.Removed
        && MissingSourceUserIds.SequenceEqual(other.MissingSourceUserIds);

    public override int GetHashCode() => HashCode.Combine(Examined, Adopted, Removed, MissingSourceUserIds.Count);
}
```

Update the class doc comment on `IFolderReconciler` to:

```csharp
/// <summary>
/// Phase 3 (§3.7) and §5.2: reconciles a tunnel's contact folder in one mailbox against
/// contact_sync_state in both directions. A "stray" is a Graph contact whose id no state row references
/// (adopted or removed); a "missing" row is one of this tunnel's rows whose contact is no longer in the
/// folder (dropped so the classification that follows recreates the contact).
/// </summary>
```

- [ ] **Step 4: Implement the reverse direction in `FolderReconciler.ReconcileAsync`**

In `worker/Services/FolderReconciler.cs`, replace the block from `var knownIds = mailboxStates` through the `usersWithState` assignment with:

```csharp
        // Known = any state row in this mailbox, from any tunnel (including legacy rows with
        // tunnel_id IS NULL) — two tunnels can share one Graph folder (tunnel names are not
        // unique), so a contact another tunnel owns must never look like a stray here.
        var knownIds = mailboxStates
            .Where(s => !string.IsNullOrEmpty(s.GraphContactId))
            .Select(s => s.GraphContactId!)
            .ToHashSet(StringComparer.Ordinal);

        // §5.2: rows of THIS tunnel whose contact is no longer in the folder. Other tunnels' rows
        // reference other folders and are never "missing" here; rows without a Graph id are ignored.
        var graphIds = graphContacts.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var missingRows = mailboxStates
            .Where(s => s.TunnelId == tunnel.Id
                        && !string.IsNullOrEmpty(s.GraphContactId)
                        && !graphIds.Contains(s.GraphContactId!))
            .ToList();
        var missingRowIds = missingRows.Select(s => s.Id).ToHashSet();
        if (missingRows.Count > 0)
        {
            // Dropped before any adoption is saved: a stray adopted for the same user below would
            // otherwise collide with the dead row on the unique (user, list, mailbox, tunnel) index.
            db.ContactSyncStates.RemoveRange(missingRows);
            await db.SaveChangesAsync(CancellationToken.None);
            foreach (var row in missingRows)
                _logger.LogInformation(
                    "Reconcile: contact {ContactId} for SourceUserId={SourceUserId} is gone from the folder in mailbox {Email}; dropping the row so it is recreated",
                    row.GraphContactId, row.SourceUserId, mailbox.Email);
        }

        // Adoption eligibility is still scoped to THIS tunnel: a user with a state row under a
        // different tunnel may still need one adopted for this tunnel's own folder. A user whose only
        // row was just dropped as missing is eligible again, so a stray of theirs is adopted, not
        // deleted and recreated.
        var usersWithState = mailboxStates
            .Where(s => s.TunnelId == tunnel.Id && !missingRowIds.Contains(s.Id))
            .Select(s => s.SourceUserId)
            .ToHashSet();
```

Then change the final log line and return value at the end of the method to:

```csharp
        _logger.LogInformation(
            "Reconcile: tunnel {TunnelName} / mailbox {Email}: {Examined} Graph contact(s), {Adopted} adopted, {Removed} removed, {Missing} missing (recreated this run)",
            tunnel.Name, mailbox.Email, graphContacts.Count, adopted, removed, missingRows.Count);

        return new FolderReconcileResult(graphContacts.Count, adopted, removed, missingRows.Select(s => s.SourceUserId).ToList());
```

Also update the class summary at the top of `FolderReconciler.cs` by appending one sentence to the existing paragraph:

```csharp
/// §5.2 adds the reverse direction: this tunnel's rows whose Graph id is not in the folder are dropped
/// (and reported) so the classification that follows recreates the contact.
```

- [ ] **Step 5: Update the engine test fake to the new record**

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` replace `FakeFolderReconciler` with:

```csharp
    private sealed class FakeFolderReconciler : IFolderReconciler
    {
        public List<(int TunnelId, int MailboxId, string FolderId)> Calls { get; } = [];

        /// <summary>§5.2: returned as the missing-row report on every call (the real reconciler has already dropped those rows).</summary>
        public List<int> MissingSourceUserIds { get; } = [];

        public Task<FolderReconcileResult> ReconcileAsync(Tunnel tunnel, TargetMailbox mailbox, string folderId,
            int canonicalPhoneListId, IReadOnlyList<SourceUser> sourceUsers, CancellationToken ct)
        {
            Calls.Add((tunnel.Id, mailbox.Id, folderId));
            return Task.FromResult(new FolderReconcileResult(0, 0, 0, MissingSourceUserIds.ToList()));
        }
    }
```

- [ ] **Step 6: Run the unit tests to verify they pass**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed! - Failed: 0, Passed: 362, Skipped: 1`.

- [ ] **Step 7: Commit**

```bash
git add worker/Services/IFolderReconciler.cs worker/Services/FolderReconciler.cs tests/AFHSync.Tests.Unit/Sync/FolderReconcilerTests.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -F - <<'EOF'
feat(worker): reconciler drops state rows whose Graph contact is gone

§5.2. A row whose hash matches the source is never re-checked against
Graph, so a contact deleted on the phone (or by an April folder wipe)
stayed missing until the person's data changed. The reconciler now
lists the folder and drops this tunnel's rows whose id is not there;
the classification that follows recreates the contact. A stray for the
same user is adopted instead of deleted and recreated.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 5: Audit gating, stamps and `audit_missing` items in the engine (§5.2)

**Files:**
- Modify: `worker/Services/SyncEngine.cs:680-683` (step B call), `:759-780` (`ReconcileIfPendingAsync` → `ReconcileFolderAsync`), after `SetReconcilePendingAsync` (two helpers)
- Test: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` (`CreateEngine` parameter type, seed helper, throwing fake, ten tests)

**Interfaces:**
- Consumes: `SyncRun.AuditFolders`, `TunnelMailboxFolder.LastAuditedAt` (Task 1); `FolderReconcileResult.MissingSourceUserIds` (Task 4).
- Produces: run items with `Action = "audit_missing"` (`SourceUserId`, `TunnelId`, canonical `PhoneListId`, `TargetMailboxId`). Task 7's Audit tab filters on that string.

- [ ] **Step 1: Test infrastructure**

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`:

Change the `CreateEngine` parameter `FakeFolderReconciler? folderReconciler = null` to `IFolderReconciler? folderReconciler = null` (the body already passes it through).

Add this fake next to `FakeFolderReconciler`:

```csharp
    /// <summary>§5.2 failure handling: the folder listing blows up.</summary>
    private sealed class ThrowingFolderReconciler : IFolderReconciler
    {
        public Task<FolderReconcileResult> ReconcileAsync(Tunnel tunnel, TargetMailbox mailbox, string folderId,
            int canonicalPhoneListId, IReadOnlyList<SourceUser> sourceUsers, CancellationToken ct)
            => throw new InvalidOperationException("simulated Graph listing failure");
    }
```

Add this seed helper next to `SeedTunnelWithMailboxesAsync`:

```csharp
    /// <summary>§5.2: the folder row for tunnel 1 / mailbox 1 that FakeContactFolderManager resolves as "fake-folder-id".</summary>
    private static async Task SeedFolderRowAsync(string dbName, DateTime? lastAuditedAt, DateTime? reconcilePendingAt = null)
    {
        using var ctx = MakeDbContext(dbName);
        ctx.TunnelMailboxFolders.Add(new TunnelMailboxFolder
        {
            TunnelId = 1, TargetMailboxId = 1, GraphFolderId = "fake-folder-id", FolderName = "Avail Tunnel",
            UpdatedAt = DateTime.UtcNow, LastAuditedAt = lastAuditedAt, ReconcilePendingAt = reconcilePendingAt
        });
        await ctx.SaveChangesAsync();
    }

    private static TargetMailbox ActiveMailbox() => new() { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true };

    private static FakeSourceResolver OneUser() => new([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]);

    private static async Task<TunnelMailboxFolder> FolderRowAsync(string dbName)
    {
        await using var ctx = MakeDbContext(dbName);
        return await ctx.TunnelMailboxFolders.SingleAsync(f => f.TunnelId == 1 && f.TargetMailboxId == 1);
    }
```

- [ ] **Step 2: Write the failing gating tests**

Add to `SyncEngineTests`:

```csharp
    // ==============================
    // §5.2: folder audit gating, stamps, failure handling and audit_missing items
    // ==============================

    [Fact]
    public async Task RunAsync_Scheduled_AuditsFolderNeverAudited_AndStampsIt()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName, ActiveMailbox());
        await SeedFolderRowAsync(dbName, lastAuditedAt: null);
        var reconciler = new FakeFolderReconciler();
        var engine = CreateEngine(dbName, sourceResolver: OneUser(), folderReconciler: reconciler);

        await engine.RunAsync(null, RunType.Scheduled, isDryRun: false, CancellationToken.None);

        Assert.Equal(new[] { (1, 1, "fake-folder-id") }, reconciler.Calls);
        var row = await FolderRowAsync(dbName);
        Assert.NotNull(row.LastAuditedAt);
        Assert.Equal(DateTime.UtcNow.Date, row.LastAuditedAt!.Value.Date);
    }

    [Fact]
    public async Task RunAsync_Scheduled_NoFolderRow_StillAudits()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName, ActiveMailbox());       // no tunnel_mailbox_folders row ⇒ treated as never audited
        var reconciler = new FakeFolderReconciler();
        var engine = CreateEngine(dbName, sourceResolver: OneUser(), folderReconciler: reconciler);

        await engine.RunAsync(null, RunType.Scheduled, isDryRun: false, CancellationToken.None);

        Assert.Single(reconciler.Calls);
    }

    [Fact]
    public async Task RunAsync_Scheduled_AuditsFolderLastAuditedYesterday()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName, ActiveMailbox());
        await SeedFolderRowAsync(dbName, lastAuditedAt: DateTime.UtcNow.Date.AddDays(-1).AddHours(23));   // 23:00 UTC yesterday
        var reconciler = new FakeFolderReconciler();
        var engine = CreateEngine(dbName, sourceResolver: OneUser(), folderReconciler: reconciler);

        await engine.RunAsync(null, RunType.Scheduled, isDryRun: false, CancellationToken.None);

        Assert.Single(reconciler.Calls);
        Assert.Equal(DateTime.UtcNow.Date, (await FolderRowAsync(dbName)).LastAuditedAt!.Value.Date);
    }

    [Fact]
    public async Task RunAsync_Scheduled_SkipsFolderAuditedToday()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName, ActiveMailbox());
        var earlierToday = DateTime.UtcNow;                                  // same UTC date by construction
        await SeedFolderRowAsync(dbName, lastAuditedAt: earlierToday);
        var reconciler = new FakeFolderReconciler();
        var engine = CreateEngine(dbName, sourceResolver: OneUser(), folderReconciler: reconciler);

        await engine.RunAsync(null, RunType.Scheduled, isDryRun: false, CancellationToken.None);

        Assert.Empty(reconciler.Calls);
        Assert.Equal(earlierToday, (await FolderRowAsync(dbName)).LastAuditedAt);   // stamp untouched
    }

    [Fact]
    public async Task RunAsync_Manual_WithoutFlag_DoesNotAudit()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName, ActiveMailbox());
        await SeedFolderRowAsync(dbName, lastAuditedAt: null);
        var reconciler = new FakeFolderReconciler();
        var engine = CreateEngine(dbName, sourceResolver: OneUser(), folderReconciler: reconciler);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Empty(reconciler.Calls);
        Assert.Null((await FolderRowAsync(dbName)).LastAuditedAt);
    }

    [Fact]
    public async Task RunAsync_Manual_WithAuditFlagOnRow_Audits()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName, ActiveMailbox());
        await SeedFolderRowAsync(dbName, lastAuditedAt: DateTime.UtcNow);   // audited today already — the flag still wins
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.SyncRuns.Add(new SyncRun
            {
                Id = 7, RunType = RunType.Manual, Status = SyncStatus.Pending, IsDryRun = false,
                AuditFolders = true, CreatedAt = DateTime.UtcNow
            });
            await seedCtx.SaveChangesAsync();
        }
        var reconciler = new FakeFolderReconciler();
        var engine = CreateEngine(dbName, sourceResolver: OneUser(), folderReconciler: reconciler);

        await engine.RunAsync(7, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Single(reconciler.Calls);
    }

    [Fact]
    public async Task RunAsync_DryRun_WithAuditFlag_NeverAudits()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName, ActiveMailbox());
        await SeedFolderRowAsync(dbName, lastAuditedAt: null);
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.SyncRuns.Add(new SyncRun
            {
                Id = 7, RunType = RunType.DryRun, Status = SyncStatus.Pending, IsDryRun = true,
                AuditFolders = true, CreatedAt = DateTime.UtcNow
            });
            await seedCtx.SaveChangesAsync();
        }
        var reconciler = new FakeFolderReconciler();
        var engine = CreateEngine(dbName, sourceResolver: OneUser(), folderReconciler: reconciler);

        await engine.RunAsync(7, RunType.DryRun, isDryRun: true, CancellationToken.None);

        Assert.Empty(reconciler.Calls);
        Assert.Null((await FolderRowAsync(dbName)).LastAuditedAt);
    }

    [Fact]
    public async Task RunAsync_Scheduled_FolderJustCreated_DoesNotAudit()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName, ActiveMailbox());
        var folderManager = new FakeContactFolderManager();
        folderManager.MissingFolderMailboxes.Add("mbx");                     // a real run "creates" the folder ⇒ wasCreated = true
        var reconciler = new FakeFolderReconciler();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: OneUser(), folderManager: folderManager, folderReconciler: reconciler, runLogger: runLogger);

        await engine.RunAsync(null, RunType.Scheduled, isDryRun: false, CancellationToken.None);

        Assert.Empty(reconciler.Calls);                                       // the §2.5 wipe in step C covers a new folder
        Assert.Equal(1, runLogger.FinalizedCreated);
        Assert.Equal(0, runLogger.FinalizedFailed);
    }

    [Fact]
    public async Task RunAsync_Scheduled_AuditListingFails_WarnsAndContinues_StampUnchanged()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName, ActiveMailbox());
        await SeedFolderRowAsync(dbName, lastAuditedAt: null);
        var writer = new FakeContactWriter();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: OneUser(), contactWriter: writer,
            folderReconciler: new ThrowingFolderReconciler(), runLogger: runLogger);

        await engine.RunAsync(null, RunType.Scheduled, isDryRun: false, CancellationToken.None);

        Assert.Single(writer.CreatedContactIds);                              // the mailbox carried on
        Assert.Equal(0, runLogger.FinalizedFailed);
        Assert.Null((await FolderRowAsync(dbName)).LastAuditedAt);           // retried by the next scheduled run
    }

    [Fact]
    public async Task RunAsync_PendingReconcileFails_FailsTheMailbox_AndKeepsTheFlag()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName, ActiveMailbox());
        await SeedFolderRowAsync(dbName, lastAuditedAt: null, reconcilePendingAt: DateTime.UtcNow.AddHours(-1));
        var writer = new FakeContactWriter();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: OneUser(), contactWriter: writer,
            folderReconciler: new ThrowingFolderReconciler(), runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Empty(writer.CreatedContactIds);                               // Phase 3 (§3.7): never create on top of an unreconciled folder
        Assert.Equal(1, runLogger.FinalizedFailed);
        var row = await FolderRowAsync(dbName);
        Assert.NotNull(row.ReconcilePendingAt);
        Assert.Null(row.LastAuditedAt);
    }

    [Fact]
    public async Task RunAsync_PendingFlag_ReconcilesOnAnyRunType_ClearsFlag_AndStamps()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName, ActiveMailbox());
        await SeedFolderRowAsync(dbName, lastAuditedAt: null, reconcilePendingAt: DateTime.UtcNow.AddHours(-1));
        var reconciler = new FakeFolderReconciler();
        var engine = CreateEngine(dbName, sourceResolver: OneUser(), folderReconciler: reconciler);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Single(reconciler.Calls);
        var row = await FolderRowAsync(dbName);
        Assert.Null(row.ReconcilePendingAt);
        Assert.NotNull(row.LastAuditedAt);
    }

    [Fact]
    public async Task RunAsync_Scheduled_MissingRow_WritesAuditMissingItem_ThenCreates()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName, ActiveMailbox());
        await SeedFolderRowAsync(dbName, lastAuditedAt: null);
        var reconciler = new FakeFolderReconciler();
        reconciler.MissingSourceUserIds.Add(1);                              // the real reconciler already dropped Alice's dead row
        var writer = new FakeContactWriter();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: OneUser(), contactWriter: writer, folderReconciler: reconciler, runLogger: runLogger);

        await engine.RunAsync(null, RunType.Scheduled, isDryRun: false, CancellationToken.None);

        var audit = Assert.Single(runLogger.AddedItems, i => i.Action == "audit_missing");
        Assert.Equal(1, audit.SourceUserId);
        Assert.Equal(1, audit.TunnelId);
        Assert.Equal(1, audit.PhoneListId);
        Assert.Equal(1, audit.TargetMailboxId);
        Assert.Contains(runLogger.AddedItems, i => i.Action == "created" && i.SourceUserId == 1);
        Assert.Single(writer.CreatedContactIds);
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~RunAsync_Scheduled|FullyQualifiedName~RunAsync_Manual_With|FullyQualifiedName~RunAsync_DryRun_WithAudit|FullyQualifiedName~RunAsync_Pending" 2>&1 | tail -3`
Expected: the audit tests fail (`reconciler.Calls` empty where an audit was expected, no `audit_missing` item, no stamp); `RunAsync_Manual_WithoutFlag_DoesNotAudit`, `RunAsync_DryRun_WithAuditFlag_NeverAudits`, `RunAsync_Scheduled_FolderJustCreated_DoesNotAudit` and `RunAsync_PendingReconcileFails_FailsTheMailbox_AndKeepsTheFlag` may already pass; that is fine.

- [ ] **Step 4: Replace step B in the engine**

In `worker/Services/SyncEngine.cs`, change the step B call in `ProcessMailboxAsync` (lines 680–683) to:

```csharp
        // B. Folder reconcile (§3.7 pending flag, §5.2 requested or daily audit).
        await ReconcileFolderAsync(tunnel, canonicalPhoneList, mailbox, run, folderId, folderWasCreated, sourceUsers, isDryRun, counters, ct);
```

Replace the whole `ReconcileIfPendingAsync` method (doc comment through closing brace) with:

```csharp
    /// <summary>
    /// Phase 3 (§3.8) step B, extended by §5.2. Reconciles the folder — strays adopted or removed,
    /// missing rows dropped — when a previous run left the §3.7 flag pending, when the run asks for an
    /// audit, or when a scheduled run has not audited this folder today. Never in a dry run; never when
    /// the folder was just created (the §2.5 wipe in step C covers that). Runs BEFORE classification so
    /// adopted strays are PATCHed and dropped rows are recreated in this same run.
    /// </summary>
    private async Task ReconcileFolderAsync(
        Tunnel tunnel,
        PhoneList canonicalPhoneList,
        TargetMailbox mailbox,
        SyncRun run,
        string? folderId,
        bool folderWasCreated,
        List<SourceUser> sourceUsers,
        bool isDryRun,
        MailboxCounters counters,
        CancellationToken ct)
    {
        if (isDryRun || folderId is null || folderWasCreated)
            return;

        var pending = await IsReconcilePendingAsync(tunnel.Id, mailbox.Id);
        var due = pending
            || run.AuditFolders
            || (run.RunType == RunType.Scheduled && await IsAuditDueAsync(tunnel.Id, mailbox.Id));
        if (!due)
            return;

        if (pending)
            logger.LogInformation("Reconcile pending for tunnel {TunnelId} in mailbox {Email} from a previous run", tunnel.Id, mailbox.Email);

        FolderReconcileResult result;
        try
        {
            result = await folderReconciler.ReconcileAsync(tunnel, mailbox, folderId, canonicalPhoneList.Id, sourceUsers, ct);
        }
        catch (Exception ex) when (!pending && ex is not OperationCanceledException)
        {
            // §5.2: an audit is opportunistic. Warn, leave last_audited_at alone so the next scheduled
            // run retries, and let the mailbox carry on. A PENDING reconcile keeps today's behaviour:
            // the exception reaches the per-mailbox catch and the mailbox is skipped this run, because
            // creating on top of an unreconciled folder is exactly what §3.7 prevents.
            logger.LogWarning(ex, "Folder audit failed for tunnel {TunnelId} in mailbox {Email}; continuing without it", tunnel.Id, mailbox.Email);
            return;
        }

        counters.Removed += result.Removed;
        foreach (var sourceUserId in result.MissingSourceUserIds)
        {
            runLogger.AddItem(new SyncRunItem
            {
                SyncRunId = run.Id,
                TunnelId = tunnel.Id,
                PhoneListId = canonicalPhoneList.Id,
                TargetMailboxId = mailbox.Id,
                SourceUserId = sourceUserId,
                Action = "audit_missing",
                CreatedAt = DateTime.UtcNow
            });
        }

        if (pending)
            await SetReconcilePendingAsync(tunnel.Id, mailbox.Id, pending: false);
        await StampAuditedAsync(tunnel.Id, mailbox.Id);
    }
```

Add these two helpers directly after `SetReconcilePendingAsync`:

```csharp
    /// <summary>
    /// §5.2: true when the folder row's last_audited_at is null or from an earlier UTC day. No folder
    /// row ⇒ due. A read failure ⇒ not due (logged); the next scheduled run tries again.
    /// </summary>
    private async Task<bool> IsAuditDueAsync(int tunnelId, int mailboxId)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            var last = await db.TunnelMailboxFolders
                .Where(f => f.TunnelId == tunnelId && f.TargetMailboxId == mailboxId)
                .Select(f => f.LastAuditedAt)
                .FirstOrDefaultAsync(CancellationToken.None);
            return last is null || last.Value.Date < DateTime.UtcNow.Date;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read the audit stamp for tunnel {TunnelId} mailbox {MailboxId}", tunnelId, mailboxId);
            return false;
        }
    }

    /// <summary>
    /// §5.2: records a completed reconcile. No-op when the folder row does not exist. Fresh context +
    /// CancellationToken.None — the stamp must outlive a cancel, like the reconcile flag.
    /// </summary>
    private async Task StampAuditedAsync(int tunnelId, int mailboxId)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            var row = await db.TunnelMailboxFolders
                .FirstOrDefaultAsync(f => f.TunnelId == tunnelId && f.TargetMailboxId == mailboxId, CancellationToken.None);
            if (row is null)
                return;
            row.LastAuditedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stamp the audit time for tunnel {TunnelId} mailbox {MailboxId}", tunnelId, mailboxId);
        }
    }
```

- [ ] **Step 5: Run the unit tests to verify they pass**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed! - Failed: 0, Passed: 374, Skipped: 1`. If any pre-existing test that runs `RunType.Scheduled` now fails, it is because the fake reconciler is now called for a never-audited folder; read the failure, and if it only asserts on unrelated counters, the fake's zero result should leave it passing — report anything else instead of patching the test.

- [ ] **Step 6: Commit**

```bash
git add worker/Services/SyncEngine.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -F - <<'EOF'
feat(worker): daily folder audit on scheduled runs, on-demand audit flag, audit_missing items

§5.2. Step B reconciles the folder when the §3.7 flag is pending, when
the run row asks for it, or when a scheduled run has not audited that
folder this UTC day (tunnel_mailbox_folders.last_audited_at). With the
00:00/12:00 cron that is the midnight run only. An audit that fails to
list the folder warns and moves on; a pending reconcile that fails still
skips the mailbox as before. Dropped rows are reported as audit_missing
run items and recreated in the same run.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 6: API — `auditFolders` on the trigger request and run DTOs (§5.3)

**Files:**
- Modify: `api/DTOs/TriggerSyncRequest.cs`
- Modify: `api/DTOs/SyncRunDto.cs`, `api/DTOs/SyncRunDetailDto.cs`
- Modify: `api/Controllers/SyncRunsController.cs:57-64` (row creation), `:163-178` (list mapping), `:279-283` (detail mapping)
- Test: `tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs`

**Interfaces:**
- Produces: request JSON `{ runType, isDryRun, tunnelIds, auditFolders }`; `SyncRunDto` and `SyncRunDetailDto` gain `auditFolders: boolean` right after `isDryRun`. Task 7 consumes both.

- [ ] **Step 1: Write the failing API test**

Add to `tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs` after `PostSync_StoresRequestedTunnelIds_AndExactlyOneJobId`:

```csharp
    [Fact]
    public async Task PostSync_StoresAuditFolders_AndExposesItOnListAndDetail()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        db.SyncRuns.RemoveRange(db.SyncRuns.Where(r => r.Status == SyncStatus.Running || r.Status == SyncStatus.Pending));
        await db.SaveChangesAsync();

        var response = await AuthenticatedPostAsync("/api/sync-runs", new
        {
            runType = "manual",
            isDryRun = false,
            auditFolders = true
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var runId = body.GetProperty("runId").GetInt32();

        var run = await db.SyncRuns.FindAsync(runId);
        Assert.NotNull(run);
        Assert.True(run!.AuditFolders);
        Assert.Equal(RunType.Manual, run.RunType);

        var detail = await AuthenticatedGetAsync($"/api/sync-runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailJson = await detail.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(detailJson.GetProperty("auditFolders").GetBoolean());

        var list = await AuthenticatedGetAsync("/api/sync-runs?page=1&pageSize=50");
        var listJson = await list.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var item = listJson.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("id").GetInt32() == runId);
        Assert.True(item.GetProperty("auditFolders").GetBoolean());
    }

    [Fact]
    public async Task PostSync_WithoutAuditFolders_DefaultsToFalse()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
        db.SyncRuns.RemoveRange(db.SyncRuns.Where(r => r.Status == SyncStatus.Running || r.Status == SyncStatus.Pending));
        await db.SaveChangesAsync();

        var response = await AuthenticatedPostAsync("/api/sync-runs", new { runType = "manual", isDryRun = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var runId = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("runId").GetInt32();

        var run = await db.SyncRuns.FindAsync(runId);
        Assert.False(run!.AuditFolders);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet --filter "FullyQualifiedName~SyncRunsControllerTests" 2>&1 | tail -3`
Expected: `PostSync_StoresAuditFolders_AndExposesItOnListAndDetail` fails at `Assert.True(run!.AuditFolders)` (the request property is ignored today); the default test passes.

- [ ] **Step 3: Implement the request, DTOs and mappings**

`api/DTOs/TriggerSyncRequest.cs`:

```csharp
namespace AFHSync.Api.DTOs;

public record TriggerSyncRequest(
    string RunType = "manual",   // "manual" or "dry_run"
    bool IsDryRun = false,
    int[]? TunnelIds = null,     // null = all active tunnels
    bool AuditFolders = false    // §5.2: reconcile every folder this run touches (ignored by the engine in a dry run)
);
```

`api/DTOs/SyncRunDto.cs` — insert `bool AuditFolders,` directly after `bool IsDryRun,`. `api/DTOs/SyncRunDetailDto.cs` — same insertion after `bool IsDryRun,`.

`api/Controllers/SyncRunsController.cs`:

In `TriggerSync`, the run row (after `IsDryRun = request.IsDryRun,`):

```csharp
            AuditFolders = request.AuditFolders,
```

In `GetRuns`, the projection (after `r.IsDryRun,`):

```csharp
                r.AuditFolders,
```

In `GetRun`, the detail construction (after `run.IsDryRun,`):

```csharp
            run.AuditFolders,
```

- [ ] **Step 4: Run the integration tests to verify they pass**

Run: `docker compose up -d postgres 2>&1 | tail -1 && dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed! - Failed: 0, Passed: 52, ...` (49 baseline + 1 from Task 1 + 2 here).

- [ ] **Step 5: Commit**

```bash
git add api/DTOs/TriggerSyncRequest.cs api/DTOs/SyncRunDto.cs api/DTOs/SyncRunDetailDto.cs api/Controllers/SyncRunsController.cs tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs
git commit -F - <<'EOF'
feat(api): auditFolders on the sync trigger request and run DTOs

§5.3. The API stores the audit request on the run row, as it does for
dry runs (§2.7: the job reads the row, never its arguments), and exposes
it on the run list and detail so the UI can badge audit runs.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 7: Frontend — Audit folders checkbox, badges, Audit tab (§5.3)

**Files:**
- Modify: `frontend/src/types/common.ts:7` (`SyncItemAction`)
- Modify: `frontend/src/types/sync-run.ts` (`SyncRunDto`, `SyncRunDetailDto`, `TriggerSyncRequest`)
- Modify: `frontend/src/components/StatusBadge.tsx:14` (`audit` style)
- Modify: `frontend/src/app/(app)/runs/page.tsx:98` (status cell)
- Modify: `frontend/src/app/(app)/runs/[id]/page.tsx:140-149` (`ACTION_TABS`), `:213` (header badge)
- Create: `frontend/src/lib/sync-trigger.ts`
- Test: `frontend/src/lib/sync-trigger.test.ts`
- Modify: `frontend/src/app/(app)/page.tsx` (imports, state, `handleRunSync`, header checkbox)

**Interfaces:**
- Consumes: `auditFolders` on `SyncRunDto`, `SyncRunDetailDto`, and the trigger request (Task 6); `audit_missing` item action (Task 5).
- Produces: `buildManualTriggerRequest(auditFolders: boolean): TriggerSyncRequest`.

- [ ] **Step 1: Write the failing vitest test**

Create `frontend/src/lib/sync-trigger.test.ts`:

```ts
import { describe, it, expect } from 'vitest';
import { buildManualTriggerRequest } from './sync-trigger';

describe('buildManualTriggerRequest', () => {
  it('is a plain manual run of every tunnel by default', () => {
    expect(buildManualTriggerRequest(false)).toEqual({
      runType: 'manual',
      isDryRun: false,
      tunnelIds: null,
      auditFolders: false,
    });
  });

  it('carries the audit flag when asked', () => {
    expect(buildManualTriggerRequest(true)).toEqual({
      runType: 'manual',
      isDryRun: false,
      tunnelIds: null,
      auditFolders: true,
    });
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd frontend && npm test 2>&1 | grep -E "Tests |FAIL|Error" | head -5; cd ..`
Expected: a failure loading `./sync-trigger` (module not found).

- [ ] **Step 3: Types, helper and badge**

`frontend/src/types/common.ts`, line 7:

```ts
export type SyncItemAction = 'created' | 'updated' | 'skipped' | 'failed' | 'removed' | 'stale_detected' | 'photo_updated' | 'audit_missing';
```

`frontend/src/types/sync-run.ts` — add `auditFolders: boolean;` directly after `isDryRun: boolean;` in both `SyncRunDto` and `SyncRunDetailDto`, and change `TriggerSyncRequest` to:

```ts
export interface TriggerSyncRequest {
  runType: SyncRunType;
  isDryRun: boolean;
  tunnelIds: number[] | null;
  /** §5.2: reconcile every folder this run touches; ignored by the engine in a dry run. */
  auditFolders: boolean;
}
```

Create `frontend/src/lib/sync-trigger.ts`:

```ts
import type { TriggerSyncRequest } from '@/types/sync-run';

/** §5.3: the dashboard's "Run Sync Now" request. Audit is opt-in; a manual run is never a dry run here. */
export function buildManualTriggerRequest(auditFolders: boolean): TriggerSyncRequest {
  return { runType: 'manual', isDryRun: false, tunnelIds: null, auditFolders };
}
```

`frontend/src/components/StatusBadge.tsx` — add after the `dry_run` entry:

```ts
  audit: { bg: 'bg-sky-50', text: 'text-sky-700', dot: 'bg-sky-500' },
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd frontend && npm test 2>&1 | grep "Tests "; cd ..`
Expected: `Tests  21 passed (21)`.

- [ ] **Step 5: Runs list and detail**

`frontend/src/app/(app)/runs/page.tsx`, the status cell (line ~98):

```tsx
        {row.original.isDryRun && <StatusBadge status="dry_run" />}
        {row.original.auditFolders && <StatusBadge status="audit" />}
```

`frontend/src/app/(app)/runs/[id]/page.tsx`, `ACTION_TABS` — insert after the Removed entry:

```ts
  { label: 'Audit', value: 'audit_missing' },
```

and the header (line ~213):

```tsx
        {run.isDryRun && <StatusBadge status="dry_run" />}
        {run.auditFolders && <StatusBadge status="audit" />}
```

- [ ] **Step 6: Dashboard checkbox**

`frontend/src/app/(app)/page.tsx`:

Add imports after the `Button` import:

```tsx
import { Checkbox } from '@/components/ui/checkbox';
import { Label } from '@/components/ui/label';
import { buildManualTriggerRequest } from '@/lib/sync-trigger';
```

In `DashboardPage`, after `const [activeRunId, setActiveRunId] = useState<number | null>(null);`:

```tsx
  const [auditFolders, setAuditFolders] = useState(false);
```

Change `handleRunSync` so the mutate argument is the helper:

```tsx
  function handleRunSync() {
    triggerSync.mutate(
      buildManualTriggerRequest(auditFolders),
      {
```

(the rest of the function is unchanged.)

In the `PageHeader` children, insert this before the **Run Sync Now** `<Button>`:

```tsx
        <Label
          className="text-text-muted font-normal cursor-pointer"
          title="Re-checks every contact folder against Graph. Slower."
        >
          <Checkbox
            checked={auditFolders}
            onCheckedChange={(checked) => setAuditFolders(checked)}
            disabled={isSyncing}
          />
          Audit folders
        </Label>
```

- [ ] **Step 7: Build and test the frontend**

Run: `cd frontend && npm run build 2>&1 | tail -2 && npm test 2>&1 | grep "Tests "; cd ..`
Expected: build ends with the route table and no type errors; `Tests  21 passed (21)`. If the build reports a type error on `onCheckedChange`, the base-ui signature is `(checked: boolean, eventDetails) => void`; keep the one-argument arrow as written.

- [ ] **Step 8: Commit**

```bash
git add frontend/src/types/common.ts frontend/src/types/sync-run.ts frontend/src/components/StatusBadge.tsx "frontend/src/app/(app)/runs/page.tsx" "frontend/src/app/(app)/runs/[id]/page.tsx" frontend/src/lib/sync-trigger.ts frontend/src/lib/sync-trigger.test.ts "frontend/src/app/(app)/page.tsx"
git commit -F - <<'EOF'
feat(frontend): Audit folders checkbox on the dashboard, audit badge and Audit tab on runs

§5.3. Run Sync Now can request a folder audit; audit runs are badged in
the run list and detail, and the detail's Audit tab lists the
audit_missing items (rows dropped because their contact was gone).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
EOF
```

---

### Task 8: Verification gates and hand-off

**Files:**
- Modify (as-shipped notes only after deploy, by Nick's process): `docs/superpowers/plans/2026-09-09-state-scope-and-folder-audit.md`

- [ ] **Step 1: Run all three gates from the repo root**

```bash
dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1
docker compose up -d postgres && dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -1
cd frontend && npm run build 2>&1 | tail -2 && npm test 2>&1 | grep "Tests "; cd ..
```

Expected: unit `Failed: 0, Passed: 374, Skipped: 1`; integration `Failed: 0, Passed: 52`; frontend build clean and `Tests  21 passed (21)`.

- [ ] **Step 2: Confirm the branch is clean and the history reads as intended**

Run: `git status --short && git log --oneline main..HEAD`
Expected: no uncommitted files; eight commits over `main` (spec + seven tasks).

- [ ] **Step 3: Request review, then hand off**

Use `superpowers:requesting-code-review` on the whole branch (`git diff main...HEAD`) with the spec as the requirements source. After review, `superpowers:finishing-a-development-branch`: PR to `github.com/nickafh/sync` or local fast-forward merge is Nick's call, then `./deploy.sh` on the box and the §5.5 verification:

1. Before deploy, on the VM: `docker exec afh-postgres psql -U afhsync -d afhsync -c "SELECT s.phone_list_id, COUNT(*) FROM contact_sync_state s LEFT JOIN tunnel_phone_lists tp ON tp.tunnel_id = s.tunnel_id AND tp.phone_list_id = s.phone_list_id WHERE tp.id IS NULL GROUP BY 1;"` — expect 3,620 under list 10.
2. `./deploy.sh` with no run in progress (rebuilds api, worker and frontend; the migration applies at API startup).
3. Dashboard: tick **Audit folders**, **Run Sync Now**. Expect `Reconcile:` lines for every folder in `docker logs afh-worker`, `Re-pointed`/`duplicate sync states` lines for nick@, jp@, David@ and kevingoldfinger@, and Removed plus `audit_missing` items ≈ 3,620 for those four mailboxes.
4. After: the query in step 1 returns no rows; `SELECT COUNT(*) FROM tunnel_mailbox_folders WHERE last_audited_at IS NULL;` is small; jp@'s Outlook no longer lists Charlotte Hedgepeth (AFH) and the doubled "email • email" entries are gone.
5. Next 00:00 UTC run: `Reconcile:` lines for every folder, about five minutes longer; the 12:00 run: none, about two minutes.

Then update this plan's task checkboxes and add an "As shipped" note with the actual counts from steps 3–5.
