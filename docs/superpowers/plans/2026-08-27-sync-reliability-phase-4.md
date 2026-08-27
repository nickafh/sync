# Sync Reliability — Phase 4 Implementation Plan (Deferred items)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the two deferred correctness items: AddMissing fields stop driving delta detection (§4.1) without a Graph-side update wave, and throttled/timed-out `$batch` steps are retried in-run honouring `Retry-After` (§4.2); plus silence the worker's `/health` request-log noise (§4.5).

**Architecture:** `ContactPayloadBuilder` excludes AddMissing fields from the hash and additionally returns the pre-Phase-4 ("legacy") hash whenever an AddMissing field contributed a value; `SyncEngine.ClassifyContacts` treats a stored hash that equals the legacy hash as unchanged and rewrites it locally (a `rehash`, no Graph write), so the formula change migrates itself contact-by-contact across the next run instead of PATCHing ~1M contacts. `ContactWriter.ExecuteBatchWithRetryAsync` re-posts only the 429/503/504 steps (via the SDK's `NewBatchWithFailedRequests`) up to three times, waiting the largest per-step `Retry-After` (else a linear backoff), incrementing the shared `ThrottleCounter` per retried step; a delay seam keeps the tests instant. The worker's Serilog config raises `Microsoft.AspNetCore` to Warning so the 30 s `/health` probes stop flooding `docker logs`.

**Tech Stack:** .NET 10 worker (Hangfire 1.8, EF Core 10 InMemory in unit tests), Microsoft.Graph 5.103 / Microsoft.Graph.Core batching (`BatchRequestContentCollection`, `BatchResponseContentCollection.GetResponsesStatusCodesAsync`, `GetResponseByIdAsync(stepId)`, `NewBatchWithFailedRequests`), xUnit 2.9, Serilog.

**Spec:** `docs/superpowers/specs/2026-08-25-sync-reliability-design.md` — Phase 4 (§4.1, §4.2, §4.5) as amended by Task 1 of this plan (§4.1 dual-hash instead of an update wave; §4.2 mechanics; §4.5 new).

## Global Constraints

- Branch: `sync-reliability/phase-4` from local `main` at `68e6dd6` (Phases 2, 3 and §4.3/§4.4 are deployed). PR target: `main` on `github.com/nickafh/sync`.
- Commit after every task. Use `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` as the last line of each commit message.
- Run all shell commands from the repo root `/Users/nick/Documents/Code/AFHsync` unless a step says otherwise.
- Backend gate: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet` (baseline `Passed: 331, Skipped: 1`) and `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet` (baseline `Passed: 48, Skipped: 1` without Postgres; `49, 0` via `docker compose up -d postgres` + the helper `.superpowers/sdd/2026-08-26-sync-reliability-phase-2/run-integration.sh`). Nothing in this phase touches the API or the schema, so the integration count stays at baseline.
- Frontend gate: `cd frontend && npm run build && npm test` (vitest baseline 19) — untouched by this phase; run once in Task 4.
- **No migration and no API change in this phase.** `ContactSyncState.DataHash` keeps its meaning (the current formula's hash of the contact); `PreviousDataHash` records the pre-rehash value; `LastResult = "rehashed"` marks rows migrated without a Graph write.
- Dry runs still write nothing to Graph or `contact_sync_state` — a rehash is a state write and is therefore skipped in a dry run (the contact simply classifies as unchanged).
- Copy rules: `LastResult` value `rehashed`; log line `Rehashed {Count} contact state(s) in mailbox {MailboxId} (AddMissing hash migration)`; retry log line `Retrying {Count} throttled batch step(s) for mailbox {MailboxId}, attempt {Attempt}/{Max}, after {DelayMs}ms`.
- `$batch` step retry: statuses `429`, `503`, `504` only; at most **3 retries** per step (4 posts total for a step that never recovers); delay = the largest `Retry-After` among the retried steps, clamped to `[0, 5 min]`, else `2 s × attempt` (2 s, 4 s, 6 s); `ThrottleCounter.Increment()` once per retried step per retry; a final failure keeps today's `HTTP {status}` error with `OutcomeUnknown = false`; a transport exception on a retry post marks only the steps in that retry batch `OutcomeUnknown`.
- Keep `SyncEngine.cs` edits surgical: locate by the quoted code, not line numbers.

---

## File map

| File | Responsibility |
|---|---|
| `docs/superpowers/specs/2026-08-25-sync-reliability-design.md` | §4.1 rewritten (dual hash, no wave), §4.2 mechanics, §4.5 added |
| `worker/Services/IContactPayloadBuilder.cs` | `ContactPayloadResult(Payload, DataHash, LegacyDataHash = null)` |
| `worker/Services/ContactPayloadBuilder.cs` | AddMissing excluded from `hashInput`; legacy hash computed alongside |
| `worker/Services/SyncEngine.cs` | `ClassifyContacts` returns rehashes; new `RehashStatesAsync`; orchestrator wires it |
| `worker/Services/ContactWriter.cs` | per-step retry loop, `ThrottleCounter` + delay seam in the constructor, `IsRetryableStepStatus` |
| `worker/Program.cs` | Serilog `MinimumLevel.Override("Microsoft.AspNetCore", Warning)` |
| `tests/AFHSync.Tests.Unit/Sync/ContactPayloadBuilderTests.cs`, `SyncEngineTests.cs`, `ContactWriterTests.cs` | unit tests (fake `$batch` transport gains a per-step script) |

---

### Task 0: Baseline

**Files:** none

- [ ] **Step 1: Create the branch and confirm a clean tree**

Run: `git status --short && git branch --show-current && git log --oneline -1`
Expected: no status output; `main`; `68e6dd6 fix(ui): dashboard polls while idle…`.

Run: `git checkout -b sync-reliability/phase-4 && git branch --show-current`
Expected: `sync-reliability/phase-4`.

- [ ] **Step 2: Record the baseline**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed!  - Failed: 0, Passed: 331, Skipped: 1, Total: 332`.

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed!  - Failed: 0, Passed: 48, Skipped: 1, Total: 49` (49/0 with Postgres via the helper).

---

### Task 1: Spec amendments (docs)

**Files:**
- Modify: `docs/superpowers/specs/2026-08-25-sync-reliability-design.md`

- [ ] **Step 1: Rewrite §4.1, expand §4.2, add §4.5**

Replace the **4.1** bullet with:

```markdown
- **4.1 AddMissing excluded from hash — migrated without an update wave.** `ContactPayloadBuilder` no longer includes AddMissing fields in `hashInput` (a change to a field the profile only adds on create must not trigger an update). To avoid PATCHing every synced contact once (~1M Graph writes at today's counts), `BuildPayload` also returns `LegacyDataHash` — the pre-Phase-4 formula's hash, non-null only when an AddMissing field contributed a value — and `SyncEngine.ClassifyContacts` treats a stored `DataHash` equal to `LegacyDataHash` as unchanged: the row's hash is rewritten locally (`PreviousDataHash` = old hash, `LastResult = "rehashed"`), the contact counts as skipped, and no Graph call is made. Never in a dry run. The legacy computation can be deleted once a full run has completed (every row then carries the new formula). No cron pause or off-hours run is needed.
```

Replace the **4.2** bullet with:

```markdown
- **4.2 `$batch` per-step retry.** In `ContactWriter.ExecuteBatchWithRetryAsync`, steps answering 429/503/504 are re-posted (only those steps, via `BatchRequestContentCollection.NewBatchWithFailedRequests`) up to 3 times, waiting the largest per-step `Retry-After` (clamped to 5 min) or `2 s × attempt`; `ThrottleCounter` is incremented once per retried step per retry so `SyncRun.ThrottleEvents` finally reflects batch-level throttling. A step that still fails after 3 retries keeps today's `HTTP {status}` failure (`OutcomeUnknown = false`); a transport exception during a retry post marks only that retry batch's steps `OutcomeUnknown` (§3.7 reconcile handles them). The delay is injectable so unit tests run instantly.
```

After the **4.4** bullet add:

```markdown
- **4.5 Worker `/health` request logging.** The worker's Serilog configuration overrides `Microsoft.AspNetCore` to Warning: the Docker health check hits `/health` every 30 s and ASP.NET Core was logging five Information lines per probe, burying the sync log lines in `docker logs afh-worker`. Startup, Hangfire and sync logging are unaffected (`Microsoft.Hosting.Lifetime` and the app's own categories stay at Information).
```

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-08-25-sync-reliability-design.md
git commit -m "docs(spec): Phase 4 — AddMissing hash migrated via legacy hash (no update wave), batch retry mechanics, worker /health log noise

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: AddMissing out of the hash, migrated by rehash (§4.1)

**Files:**
- Modify: `worker/Services/IContactPayloadBuilder.cs`
- Modify: `worker/Services/ContactPayloadBuilder.cs`
- Modify: `worker/Services/SyncEngine.cs` (`ProcessMailboxAsync` orchestrator, `ClassifyContacts`, new `RehashStatesAsync`)
- Modify: `tests/AFHSync.Tests.Unit/Sync/ContactPayloadBuilderTests.cs`, `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public record ContactPayloadResult(SortedDictionary<string, string> Payload, string DataHash, string? LegacyDataHash = null);
  // SyncEngine (private)
  ClassifyContacts(...) → (pendingCreates, pendingUpdates, List<(int stateId, string oldHash, string newHash)> rehashes)
  Task RehashStatesAsync(List<(int stateId, string oldHash, string newHash)> rehashes, int mailboxId, bool isDryRun)
  ```
- Consumes: nothing from earlier tasks.

- [ ] **Step 1: Write the failing builder tests**

In `tests/AFHSync.Tests.Unit/Sync/ContactPayloadBuilderTests.cs`, add after `BuildPayload_AddMissing_ExcludesField_WhenExistingStateExists`:

```csharp
    // ==============================
    // Phase 4 (§4.1): AddMissing never drives the hash; the legacy hash lets old rows migrate
    // ==============================

    [Fact]
    public void Hash_IgnoresAddMissingFields()
    {
        var withValue = CreateSourceUser(displayName: "Jane Smith", jobTitle: "Advisor");
        var withOther = CreateSourceUser(displayName: "Jane Smith", jobTitle: "Director");
        var fields = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.AddMissing),
        };

        var a = _builder.BuildPayload(withValue, fields, existingState: null);
        var b = _builder.BuildPayload(withOther, fields, existingState: null);

        Assert.Equal(a.DataHash, b.DataHash);            // only the Always field is hashed
        Assert.NotEqual(a.LegacyDataHash, b.LegacyDataHash);
    }

    [Fact]
    public void LegacyDataHash_IsTheOldFormula_AddMissingIncludedAsIfAlways()
    {
        var source = CreateSourceUser(displayName: "Jane Smith", jobTitle: "Advisor");
        var addMissing = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.AddMissing),
        };
        var always = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.Always),
        };

        var migrated = _builder.BuildPayload(source, addMissing, existingState: null);
        var reference = _builder.BuildPayload(source, always, existingState: null);

        // The pre-Phase-4 formula hashed AddMissing values exactly like Always values.
        Assert.Equal(reference.DataHash, migrated.LegacyDataHash);
        Assert.NotEqual(reference.DataHash, migrated.DataHash);
    }

    [Fact]
    public void LegacyDataHash_IsNull_WhenNoAddMissingFieldContributed()
    {
        var source = CreateSourceUser(displayName: "Jane Smith", jobTitle: null);
        var noAddMissing = new List<FieldProfileField> { CreateField("DisplayName", SyncBehavior.Always) };
        var addMissingButNull = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.AddMissing),   // source value is null ⇒ contributed nothing
        };

        Assert.Null(_builder.BuildPayload(source, noAddMissing, existingState: null).LegacyDataHash);
        Assert.Null(_builder.BuildPayload(source, addMissingButNull, existingState: null).LegacyDataHash);
    }

    [Fact]
    public void AddMissing_PayloadBehaviourIsUnchanged()
    {
        var source = CreateSourceUser(jobTitle: "Agent");
        var fields = new List<FieldProfileField> { CreateField("JobTitle", SyncBehavior.AddMissing) };

        Assert.Equal("Agent", _builder.BuildPayload(source, fields, existingState: null).Payload["JobTitle"]);
        Assert.False(_builder.BuildPayload(source, fields, new ContactSyncState { Id = 1 }).Payload.ContainsKey("JobTitle"));
    }
```

(`CreateSourceUser(displayName:, jobTitle:)` and `CreateField(name, behavior)` are the file's existing helpers — check their parameter names at the bottom of the file and use them exactly.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~ContactPayloadBuilderTests" 2>&1 | grep -E "error|Failed|Passed" | head -5`
Expected: build error `'ContactPayloadResult' does not contain a definition for 'LegacyDataHash'`.

- [ ] **Step 3: Extend the result and the builder**

In `worker/Services/IContactPayloadBuilder.cs` replace the record with:

```csharp
/// <summary>
/// Immutable result of <see cref="IContactPayloadBuilder.BuildPayload"/>.
/// </summary>
/// <param name="Payload">
/// Sorted dictionary of field name -> string value for fields that should be written to the contact.
/// Keys are sorted by <see cref="StringComparer.Ordinal"/> to ensure consistent serialization.
/// </param>
/// <param name="DataHash">Lowercase hex SHA-256 of the hash input (Always + RemoveBlank fields).</param>
/// <param name="LegacyDataHash">
/// Phase 4 (§4.1): the pre-Phase-4 formula's hash — the same input plus every AddMissing value —
/// or null when no AddMissing field contributed a value (then the two formulas agree). Lets a
/// stored hash written by the old formula be recognised as "unchanged" and rewritten locally.
/// </param>
public record ContactPayloadResult(
    SortedDictionary<string, string> Payload,
    string DataHash,
    string? LegacyDataHash = null);
```

In `worker/Services/ContactPayloadBuilder.cs`:

1. Replace the class doc's line `/// Per D-06: Nosync excludes; AddMissing includes for new contacts only; Always always includes; RemoveBlank clears empty.` with `/// Per D-06: Nosync excludes; AddMissing writes on create only and never affects the hash (Phase 4 §4.1); Always always includes; RemoveBlank clears empty.`

2. In the `BuildPayload` doc comment, replace `/// - AddMissing: included in hash (source value is tracked even if not written to existing contacts)` with `/// - AddMissing: EXCLUDED from the hash (Phase 4 §4.1) — a change to a field we only add on create must not trigger an update; the value is still folded into LegacyDataHash so rows hashed by the old formula migrate without a Graph write`.

3. Replace

```csharp
        var payload = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var hashInput = new SortedDictionary<string, string>(StringComparer.Ordinal);
```

with

```csharp
        var payload = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var hashInput = new SortedDictionary<string, string>(StringComparer.Ordinal);
        // Phase 4 (§4.1): AddMissing values used to be hashed. Keep them in a side dictionary so
        // the legacy hash can still be computed for rows written before the formula changed.
        var legacyAddMissing = new SortedDictionary<string, string>(StringComparer.Ordinal);
```

4. In `case SyncBehavior.AddMissing:` replace

```csharp
                    // Hash: always include (track source value for delta detection).
                    // Payload: include only for new contacts (no existing sync state).
                    // When a contact already exists, the existing value is preserved.
                    var value = GetFieldValue(source, field.FieldName);
                    if (value is not null)
                    {
                        hashInput[field.FieldName] = value;
                        if (existingState is null)
                        {
                            payload[field.FieldName] = value;
                        }
                    }
                    break;
```

with

```csharp
                    // Hash: EXCLUDED (Phase 4 §4.1) — only the legacy hash still sees this value.
                    // Payload: include only for new contacts (no existing sync state).
                    // When a contact already exists, the existing value is preserved.
                    var value = GetFieldValue(source, field.FieldName);
                    if (value is not null)
                    {
                        legacyAddMissing[field.FieldName] = value;
                        if (existingState is null)
                        {
                            payload[field.FieldName] = value;
                        }
                    }
                    break;
```

5. Replace

```csharp
        var hash = ComputeHash(hashInput);
        return new ContactPayloadResult(payload, hash);
```

with

```csharp
        var hash = ComputeHash(hashInput);

        string? legacyHash = null;
        if (legacyAddMissing.Count > 0)
        {
            var legacyInput = new SortedDictionary<string, string>(hashInput, StringComparer.Ordinal);
            foreach (var (name, value) in legacyAddMissing)
                legacyInput[name] = value;
            legacyHash = ComputeHash(legacyInput);
        }

        return new ContactPayloadResult(payload, hash, legacyHash);
```

- [ ] **Step 4: Run the builder tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~ContactPayloadBuilderTests" 2>&1 | tail -1`
Expected: `Passed!  - Failed: 0, Passed: <N>, Skipped: 0` where N is the file's previous count + 4 (all green — the four new tests plus the unchanged existing ones).

- [ ] **Step 5: Write the failing engine tests**

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs`, add before the `// Stub implementations` banner:

```csharp
    // ==============================
    // Phase 4 (4.1): a stored hash from the old formula is rewritten locally, not PATCHed
    // ==============================

    [Fact]
    public async Task RunAsync_StoredHashEqualsLegacyHash_RehashesWithoutGraphWrite()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.ContactSyncStates.Add(new ContactSyncState
            {
                Id = 1, SourceUserId = 1, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1,
                GraphContactId = "g-1", DataHash = "legacy-hash", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await seedCtx.SaveChangesAsync();
        }
        var writer = new FakeContactWriter();
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            payloadBuilder: new FakeContactPayloadBuilder { LegacyHash = "legacy-hash" },
            contactWriter: writer,
            runLogger: runLogger);

        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        Assert.Empty(writer.UpdatedContactIds);                 // no PATCH
        Assert.DoesNotContain(runLogger.AddedItems, i => i.Action == "updated");
        Assert.Equal(1, runLogger.FinalizedSkipped);
        await using var verifyCtx = MakeDbContext(dbName);
        var state = await verifyCtx.ContactSyncStates.SingleAsync();
        Assert.Equal("new-hash", state.DataHash);                // rewritten to the current formula
        Assert.Equal("legacy-hash", state.PreviousDataHash);
        Assert.Equal("rehashed", state.LastResult);
    }

    [Fact]
    public async Task RunAsync_DryRun_DoesNotRehash()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedTunnelWithMailboxesAsync(dbName,
            new TargetMailbox { Id = 1, EntraId = "mbx", Email = "u@contoso.com", IsActive = true });
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.ContactSyncStates.Add(new ContactSyncState
            {
                Id = 1, SourceUserId = 1, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1,
                GraphContactId = "g-1", DataHash = "legacy-hash", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await seedCtx.SaveChangesAsync();
        }
        var engine = CreateEngine(dbName,
            sourceResolver: new FakeSourceResolver([new SourceUser { Id = 1, EntraId = "u1", DisplayName = "Alice" }]),
            payloadBuilder: new FakeContactPayloadBuilder { LegacyHash = "legacy-hash" });

        await engine.RunAsync(null, RunType.DryRun, isDryRun: true, CancellationToken.None);

        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal("legacy-hash", (await verifyCtx.ContactSyncStates.SingleAsync()).DataHash);
    }
```

And extend the existing `FakeContactPayloadBuilder` stub so it can return a legacy hash — replace it with:

```csharp
    /// <summary>
    /// Always returns hash "new-hash" so existing states with "old-hash" trigger updates,
    /// and states with "new-hash" are skipped. Phase 4: <see cref="LegacyHash"/> (default null)
    /// is returned as LegacyDataHash so tests can exercise the rehash path.
    /// </summary>
    private sealed class FakeContactPayloadBuilder : IContactPayloadBuilder
    {
        public string? LegacyHash { get; init; }

        public ContactPayloadResult BuildPayload(
            SourceUser source,
            IReadOnlyList<FieldProfileField> fieldSettings,
            ContactSyncState? existingState)
        {
            var payload = new SortedDictionary<string, string> { { "DisplayName", source.DisplayName ?? "Unknown" } };
            return new ContactPayloadResult(payload, "new-hash", LegacyHash);
        }
    }
```

- [ ] **Step 6: Run to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~SyncEngineTests.RunAsync_StoredHashEqualsLegacyHash|FullyQualifiedName~SyncEngineTests.RunAsync_DryRun_DoesNotRehash" 2>&1 | tail -6`
Expected: `RunAsync_StoredHashEqualsLegacyHash_RehashesWithoutGraphWrite` FAILS (`Assert.Empty` — the engine PATCHed it); `RunAsync_DryRun_DoesNotRehash` passes already (a dry run never writes).

- [ ] **Step 7: Classify the legacy match as a rehash**

In `worker/Services/SyncEngine.cs`, `ClassifyContacts`:

1. Change the return type — replace

```csharp
    private (List<(string key, int sourceUserId, SortedDictionary<string, string> payload, string dataHash)> pendingCreates,
             List<(string key, int sourceUserId, string graphContactId, int stateId, SortedDictionary<string, string> payload, string dataHash, string? previousHash)> pendingUpdates)
        ClassifyContacts(
```

with

```csharp
    private (List<(string key, int sourceUserId, SortedDictionary<string, string> payload, string dataHash)> pendingCreates,
             List<(string key, int sourceUserId, string graphContactId, int stateId, SortedDictionary<string, string> payload, string dataHash, string? previousHash)> pendingUpdates,
             List<(int stateId, string oldHash, string newHash)> rehashes)
        ClassifyContacts(
```

2. After the `var pendingUpdates = new List<…>();` line add:

```csharp
        // Phase 4 (§4.1): rows whose stored hash was written by the pre-Phase-4 formula.
        var rehashes = new List<(int stateId, string oldHash, string newHash)>();
```

3. Replace

```csharp
                else if (existingState.DataHash != result.DataHash)
                {
                    pendingUpdates.Add((sourceUser.Id.ToString(), sourceUser.Id, existingState.GraphContactId!,
                        existingState.Id, result.Payload, result.DataHash, existingState.DataHash));
                }
```

with

```csharp
                else if (existingState.DataHash != result.DataHash
                         && result.LegacyDataHash is not null
                         && existingState.DataHash == result.LegacyDataHash)
                {
                    // Phase 4 (§4.1): nothing changed at the source — only the hash formula did.
                    // Rewrite the stored hash locally; no PATCH.
                    rehashes.Add((existingState.Id, existingState.DataHash!, result.DataHash));
                    counters.Skipped++;
                }
                else if (existingState.DataHash != result.DataHash)
                {
                    pendingUpdates.Add((sourceUser.Id.ToString(), sourceUser.Id, existingState.GraphContactId!,
                        existingState.Id, result.Payload, result.DataHash, existingState.DataHash));
                }
```

4. Replace `        return (pendingCreates, pendingUpdates);` (the method's last statement) with `        return (pendingCreates, pendingUpdates, rehashes);`.

5. In the `ProcessMailboxAsync` orchestrator replace

```csharp
        var (pendingCreates, pendingUpdates) = ClassifyContacts(tunnel, canonicalPhoneList, mailbox, run,
            sourceUsers, fieldSettings, existingStates, counters);
```

with

```csharp
        var (pendingCreates, pendingUpdates, rehashes) = ClassifyContacts(tunnel, canonicalPhoneList, mailbox, run,
            sourceUsers, fieldSettings, existingStates, counters);

        // D'. Phase 4 (§4.1): migrate rows hashed by the old formula — a local write, no Graph call.
        await RehashStatesAsync(rehashes, mailbox.Id, isDryRun);
```

6. Directly after `HealDeadStatesAsync` add:

```csharp
    /// <summary>
    /// Phase 4 (§4.1): rewrites the stored hash of rows whose value matched the pre-Phase-4
    /// formula, so the AddMissing hash change migrates without PATCHing every contact. Fresh
    /// context + CancellationToken.None like the other bookkeeping writes; never in a dry run.
    /// </summary>
    private async Task RehashStatesAsync(List<(int stateId, string oldHash, string newHash)> rehashes, int mailboxId, bool isDryRun)
    {
        if (isDryRun || rehashes.Count == 0)
            return;

        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        var ids = rehashes.Select(r => r.stateId).ToList();
        var rows = await db.ContactSyncStates
            .Where(s => ids.Contains(s.Id))
            .ToListAsync(CancellationToken.None);
        var byId = rehashes.ToDictionary(r => r.stateId);
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            if (!byId.TryGetValue(row.Id, out var r) || row.DataHash != r.oldHash)
                continue;   // changed underneath us — leave it for the next run
            row.PreviousDataHash = r.oldHash;
            row.DataHash = r.newHash;
            row.LastResult = "rehashed";
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("Rehashed {Count} contact state(s) in mailbox {MailboxId} (AddMissing hash migration)", rows.Count, mailboxId);
    }
```

- [ ] **Step 8: Run the unit suite**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed!  - Failed: 0, Passed: 337, Skipped: 1` (331 + 4 + 2).

- [ ] **Step 9: Commit**

```bash
git add worker/Services/IContactPayloadBuilder.cs worker/Services/ContactPayloadBuilder.cs worker/Services/SyncEngine.cs tests/AFHSync.Tests.Unit/Sync/ContactPayloadBuilderTests.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -m "feat(worker): AddMissing fields no longer drive the delta hash; old-formula rows are rehashed locally instead of PATCHed

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---
### Task 3: `$batch` per-step retry honouring `Retry-After` (§4.2) + worker `/health` log noise (§4.5)

> **Executed with a ruling (2026-08-27):** the resolved `Microsoft.Graph.Core` 3.2.5 renumbers step ids in `NewBatchWithFailedRequests`, so the steps below that rely on it were replaced — `ExecuteBatchWithRetryAsync(mailboxEntraId, keys, buildStep, results, onSuccess, ct)` builds every batch (initial and retry) itself from a per-key `RequestInformation` factory; the retry wait honours the caller's `ct` (final-review fix). The spec §4.2 text is authoritative; the plan text is kept as written for the record.

**Files:**
- Modify: `worker/Services/ContactWriter.cs`
- Modify: `worker/Program.cs`
- Modify: `tests/AFHSync.Tests.Unit/Sync/ContactWriterTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public ContactWriter(GraphClientFactory graphClientFactory, ThrottleCounter throttleCounter, ILogger<ContactWriter> logger, Func<TimeSpan, Task>? delay = null);
  internal const int MaxBatchStepRetries = 3;
  internal static bool IsRetryableStepStatus(HttpStatusCode status);          // 429, 503, 504
  internal static TimeSpan RetryDelayFor(int attempt, TimeSpan? retryAfter);   // attempt is 1-based
  ```
  DI: `AddScoped<IContactWriter, ContactWriter>()` is unchanged — `ThrottleCounter` is already a singleton and the optional `delay` resolves to its default (`Task.Delay`).
- Consumes: nothing from Task 2 (independent files).

- [ ] **Step 1: Write the failing unit tests**

In `tests/AFHSync.Tests.Unit/Sync/ContactWriterTests.cs`:

1. Replace the fake transport (everything from the `// ── Fake Graph SDK transport` banner to the end of the file) with:

```csharp
    // ── Fake Graph SDK transport (no network, no credential) ─────────────────────────────────

    /// <summary>What the fake transport answers for one batch step. Status 201/200 ⇒ a body with an id.</summary>
    private sealed record FakeStep(int Status, string? RetryAfter = null);

    private static (ContactWriter writer, FakeBatchHandler handler) BuildWriterWithFakeGraphTransport(
        Action? onBatchHandled = null,
        Exception? throwOnSend = null,
        int throwOnCallNumber = 1,
        Func<int, string, FakeStep>? script = null,
        ThrottleCounter? throttle = null,
        List<TimeSpan>? delays = null)
    {
        var handler = new FakeBatchHandler(onBatchHandled, throwOnSend, throwOnCallNumber, script);
        var httpClient = new HttpClient(handler);
        var client = new GraphServiceClient(httpClient, new NoOpAuthenticationProvider());
        var factory = new GraphClientFactory(client);
        var writer = new ContactWriter(
            factory,
            throttle ?? new ThrottleCounter(),
            NullLogger<ContactWriter>.Instance,
            delay: d => { delays?.Add(d); return Task.CompletedTask; });   // never actually sleep in tests
        return (writer, handler);
    }

    private sealed class NoOpAuthenticationProvider : IAuthenticationProvider
    {
        public Task AuthenticateRequestAsync(
            RequestInformation request,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Fake $batch transport. By default echoes a 201-with-id success for every step so
    /// ContactWriter's real batch/retry code runs end to end without hitting Graph. A
    /// <c>script(callNumber, stepId)</c> can answer individual steps with another status and an
    /// optional Retry-After header; <c>throwOnSend</c> throws on call number <c>throwOnCallNumber</c>.
    /// Every request's step ids are recorded in <see cref="RequestStepIds"/> (one list per call).
    /// </summary>
    private sealed class FakeBatchHandler(
        Action? onBatchHandled,
        Exception? throwOnSend = null,
        int throwOnCallNumber = 1,
        Func<int, string, FakeStep>? script = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public List<List<string>> RequestStepIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (throwOnSend is not null && CallCount == throwOnCallNumber)
                throw throwOnSend;
            var bodyStr = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(bodyStr!);
            var ids = doc.RootElement.GetProperty("requests").EnumerateArray()
                .Select(r => r.GetProperty("id").GetString()!)
                .ToList();
            RequestStepIds.Add(ids);

            var responses = new JsonArray();
            for (var i = 0; i < ids.Count; i++)
            {
                var step = script?.Invoke(CallCount, ids[i]) ?? new FakeStep(201);
                var node = new JsonObject { ["id"] = ids[i], ["status"] = step.Status };
                if (step.RetryAfter is not null)
                    node["headers"] = new JsonObject { ["Retry-After"] = step.RetryAfter };
                node["body"] = step.Status is 200 or 201
                    ? new JsonObject { ["id"] = $"graph-contact-{CallCount}-{i}" }
                    : new JsonObject { ["error"] = new JsonObject { ["code"] = $"status{step.Status}", ["message"] = "fake" } };
                responses.Add(node);
            }
            var json = new JsonObject { ["responses"] = responses }.ToJsonString();

            onBatchHandled?.Invoke();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
```

and add `using System.Text.Json.Nodes;` to the file's usings. (The existing tests keep compiling: `BuildWriterWithFakeGraphTransport()`, `(onBatchHandled: …)` and `(throwOnSend: …)` still resolve, and the default success body id format is unchanged.)

2. Add these tests before the `// ── Fake Graph SDK transport` banner:

```csharp
    // ── Phase 4 (4.2): throttled / timed-out steps are re-posted, honouring Retry-After ──────

    private static List<(string key, SortedDictionary<string, string> payload)> Ops(params string[] keys)
        => keys.Select(k => (k, new SortedDictionary<string, string> { ["DisplayName"] = k })).ToList();

    [Fact]
    public async Task CreateContactsBatchAsync_RetriesOnlyThrottledSteps_HonoursRetryAfter_CountsThrottle()
    {
        var throttle = new ThrottleCounter();
        var delays = new List<TimeSpan>();
        // The script needs the second step's id, which the SDK assigns — read it from the recorded
        // request (the fake records ids before it consults the script) instead of guessing the format.
        FakeBatchHandler? h = null;
        var (writer, handler) = BuildWriterWithFakeGraphTransport(
            script: (call, stepId) => call == 1 && stepId == h!.RequestStepIds[0][1]
                ? new FakeStep(429, RetryAfter: "7")      // first call: the second step is throttled
                : new FakeStep(201),
            throttle: throttle, delays: delays);
        h = handler;

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", Ops("k1", "k2", "k3"), onChunkCompleted: null, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(3, handler.RequestStepIds[0].Count);
        Assert.Single(handler.RequestStepIds[1]);                               // only the throttled step was re-posted
        Assert.Equal(handler.RequestStepIds[0][1], handler.RequestStepIds[1][0]);   // same step id on the retry
        Assert.All(results.Values, r => Assert.True(r.Success));
        Assert.Equal("graph-contact-2-0", results["k2"].GraphContactId);        // k2's id came from the retry
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, delays);
        Assert.Equal(1, throttle.Count);
    }

    [Fact]
    public async Task CreateContactsBatchAsync_GivesUpAfterThreeRetries_KeepsHttpStatusFailure()
    {
        var throttle = new ThrottleCounter();
        var delays = new List<TimeSpan>();
        var (writer, handler) = BuildWriterWithFakeGraphTransport(
            script: (_, _) => new FakeStep(503),                                  // never recovers, no Retry-After
            throttle: throttle, delays: delays);

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", Ops("k1"), onChunkCompleted: null, CancellationToken.None);

        Assert.Equal(4, handler.CallCount);                                      // 1 + 3 retries
        Assert.False(results["k1"].Success);
        Assert.Equal("HTTP 503", results["k1"].Error);
        Assert.False(results["k1"].OutcomeUnknown);                              // Graph answered every time
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6) }, delays);
        Assert.Equal(3, throttle.Count);
    }

    [Fact]
    public async Task CreateContactsBatchAsync_DoesNotRetryNonTransientStatus()
    {
        var throttle = new ThrottleCounter();
        var delays = new List<TimeSpan>();
        var (writer, handler) = BuildWriterWithFakeGraphTransport(
            script: (_, _) => new FakeStep(400), throttle: throttle, delays: delays);

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", Ops("k1"), onChunkCompleted: null, CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal("HTTP 400", results["k1"].Error);
        Assert.Empty(delays);
        Assert.Equal(0, throttle.Count);
    }

    [Fact]
    public async Task UpdateContactsBatchAsync_RetriesGatewayTimeout()
    {
        var throttle = new ThrottleCounter();
        var (writer, handler) = BuildWriterWithFakeGraphTransport(
            script: (call, _) => call == 1 ? new FakeStep(504) : new FakeStep(200), throttle: throttle, delays: []);
        var ops = new List<(string key, string graphContactId, SortedDictionary<string, string> payload)>
        {
            ("k1", "graph-1", new SortedDictionary<string, string> { ["DisplayName"] = "A" }),
        };

        var results = await writer.UpdateContactsBatchAsync("mbx1", ops, onChunkCompleted: null, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.True(results["k1"].Success);
        Assert.Equal(1, throttle.Count);
    }

    [Fact]
    public async Task CreateContactsBatchAsync_TransportThrowsOnRetry_MarksOnlyRetriedStepsUnknown()
    {
        FakeBatchHandler? h = null;
        var (writer, handler) = BuildWriterWithFakeGraphTransport(
            script: (call, stepId) => call == 1 && stepId == h!.RequestStepIds[0][1] ? new FakeStep(429) : new FakeStep(201),
            throwOnSend: new HttpRequestException("connection reset"),
            throwOnCallNumber: 2,
            delays: []);
        h = handler;

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", Ops("k1", "k2"), onChunkCompleted: null, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.True(results["k1"].Success);                                      // answered definitively on call 1
        Assert.False(results["k1"].OutcomeUnknown);
        Assert.False(results["k2"].Success);
        Assert.True(results["k2"].OutcomeUnknown);                               // the retry post never got an answer
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(400, false)]
    [InlineData(404, false)]
    [InlineData(500, false)]
    [InlineData(502, false)]
    public void IsRetryableStepStatus_Is429_503_504_Only(int status, bool expected)
        => Assert.Equal(expected, ContactWriter.IsRetryableStepStatus((HttpStatusCode)status));

    [Theory]
    [InlineData(1, null, 2)]
    [InlineData(2, null, 4)]
    [InlineData(3, null, 6)]
    [InlineData(1, 7, 7)]
    [InlineData(2, 600, 300)]     // clamped to 5 minutes
    [InlineData(1, -3, 0)]        // a Retry-After date already in the past ⇒ no wait
    public void RetryDelayFor_UsesRetryAfterElseLinearBackoff(int attempt, int? retryAfterSeconds, int expectedSeconds)
    {
        var retryAfter = retryAfterSeconds is int s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ContactWriter.RetryDelayFor(attempt, retryAfter));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~ContactWriterTests" 2>&1 | grep -E "error" | head -4`
Expected: build errors — `ContactWriter` has no constructor taking `ThrottleCounter`, and `IsRetryableStepStatus` / `RetryDelayFor` do not exist.

- [ ] **Step 3: Constructor, constants and helpers**

In `worker/Services/ContactWriter.cs`:

1. Add `using System.Net;` to the usings.

2. Replace the fields + constructor

```csharp
    private readonly GraphClientFactory _graphClientFactory;
    private readonly ILogger<ContactWriter> _logger;

    public ContactWriter(GraphClientFactory graphClientFactory, ILogger<ContactWriter> logger)
    {
        _graphClientFactory = graphClientFactory;
        _logger = logger;
    }
```

with

```csharp
    private readonly GraphClientFactory _graphClientFactory;
    private readonly ThrottleCounter _throttleCounter;
    private readonly ILogger<ContactWriter> _logger;
    private readonly Func<TimeSpan, Task> _delay;

    /// <param name="delay">
    /// Phase 4 (§4.2): how to wait between batch-step retries. Defaults to <see cref="Task.Delay(TimeSpan)"/>;
    /// unit tests inject a recorder so they never sleep.
    /// </param>
    public ContactWriter(
        GraphClientFactory graphClientFactory,
        ThrottleCounter throttleCounter,
        ILogger<ContactWriter> logger,
        Func<TimeSpan, Task>? delay = null)
    {
        _graphClientFactory = graphClientFactory;
        _throttleCounter = throttleCounter;
        _logger = logger;
        _delay = delay ?? (d => Task.Delay(d));
    }

    /// <summary>Phase 4 (§4.2): a throttled / timed-out batch step is re-posted at most this many times.</summary>
    internal const int MaxBatchStepRetries = 3;
    private static readonly TimeSpan StepRetryBaseDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>Statuses Graph uses for "try this step again later": 429, 503, 504.</summary>
    internal static bool IsRetryableStepStatus(HttpStatusCode status)
        => (int)status is 429 or 503 or 504;

    /// <summary>
    /// The wait before retry number <paramref name="attempt"/> (1-based): the server's Retry-After
    /// clamped to [0, 5 min] when it sent one, otherwise 2 s × attempt.
    /// </summary>
    internal static TimeSpan RetryDelayFor(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is TimeSpan ra)
        {
            if (ra < TimeSpan.Zero) return TimeSpan.Zero;
            return ra > MaxRetryAfter ? MaxRetryAfter : ra;
        }
        return StepRetryBaseDelay * attempt;
    }
```

- [ ] **Step 4: The retry loop**

Replace the whole `ExecuteBatchWithRetryAsync` method (its XML doc through its closing brace) with:

```csharp
    /// <summary>
    /// Posts a batch and maps each step's answer into <paramref name="results"/>.
    ///
    /// Phase 4 (§4.2): steps answering 429/503/504 are re-posted — only those steps, via
    /// <see cref="BatchRequestContentCollection.NewBatchWithFailedRequests"/>, which keeps their
    /// step ids — up to <see cref="MaxBatchStepRetries"/> times, waiting the largest per-step
    /// Retry-After (else 2 s × attempt). Each retried step bumps <see cref="ThrottleCounter"/> so
    /// SyncRun.ThrottleEvents reflects batch-level throttling too.
    ///
    /// Phase 2 (§2.6a follow-up): posts with <see cref="CancellationToken.None"/> — the caller's
    /// chunk loop already checked <c>ct.ThrowIfCancellationRequested()</c> before this batch was
    /// built, so once we're here the batch always runs to completion and its outcome is always
    /// persisted via the chunk's <c>onChunkCompleted</c> callback, instead of a shutdown mid-POST
    /// turning every key in the batch into a swallowed "canceled" failure.
    /// </summary>
    private async Task ExecuteBatchWithRetryAsync(
        string mailboxEntraId,
        BatchRequestContentCollection batchContent,
        Dictionary<string, string> stepIdToKey,
        Dictionary<string, BatchOperationResult> results,
        Func<BatchResponseContentCollection, string, Task<BatchOperationResult>> onSuccess)
    {
        var pending = batchContent;
        var pendingStepIds = stepIdToKey.Keys.ToList();

        for (var attempt = 0; ; attempt++)
        {
            BatchResponseContentCollection? response;
            try
            {
                response = await _graphClientFactory.Client.Batch.PostAsync(pending, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch request failed entirely (post {Attempt})", attempt + 1);
                // Phase 3 (§3.7): the request may have reached Graph — the caller reconciles the folder.
                // Only the steps in THIS post are unknown; earlier definitive answers stand.
                foreach (var stepId in pendingStepIds)
                    results[stepIdToKey[stepId]] = new BatchOperationResult(false, Error: ex.Message, OutcomeUnknown: true);
                return;
            }

            if (response == null)
            {
                foreach (var stepId in pendingStepIds)
                    results[stepIdToKey[stepId]] = new BatchOperationResult(false, Error: "Null batch response", OutcomeUnknown: true);
                return;
            }

            var statusCodes = await response.GetResponsesStatusCodesAsync();
            var retryable = new Dictionary<string, HttpStatusCode>();
            TimeSpan? retryAfter = null;

            foreach (var (stepId, statusCode) in statusCodes)
            {
                if (!stepIdToKey.TryGetValue(stepId, out var key)) continue;

                if (BatchResponseContent.IsSuccessStatusCode(statusCode))
                {
                    try
                    {
                        results[key] = await onSuccess(response, stepId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse batch response for step {StepId}", stepId);
                        results[key] = new BatchOperationResult(false, Error: NoContactIdError);
                    }
                }
                else if (IsRetryableStepStatus(statusCode) && attempt < MaxBatchStepRetries)
                {
                    retryable[stepId] = statusCode;
                    _throttleCounter.Increment();
                    var stepRetryAfter = await ReadRetryAfterAsync(response, stepId);
                    if (stepRetryAfter is not null && (retryAfter is null || stepRetryAfter > retryAfter))
                        retryAfter = stepRetryAfter;
                    // Provisional — overwritten by the retry's answer (or kept if it keeps failing).
                    results[key] = new BatchOperationResult(false, Error: $"HTTP {(int)statusCode}");
                }
                else
                {
                    _logger.LogWarning(
                        "Batch step {StepId} (key={Key}) failed with HTTP {StatusCode}",
                        stepId, key, (int)statusCode);
                    results[key] = new BatchOperationResult(
                        false,
                        Error: $"HTTP {(int)statusCode}",
                        NotFound: (int)statusCode == 404);
                }
            }

            if (retryable.Count == 0)
                return;

            var delay = RetryDelayFor(attempt + 1, retryAfter);
            _logger.LogWarning(
                "Retrying {Count} throttled batch step(s) for mailbox {MailboxId}, attempt {Attempt}/{Max}, after {DelayMs}ms",
                retryable.Count, mailboxEntraId, attempt + 1, MaxBatchStepRetries, delay.TotalMilliseconds);
            await _delay(delay);
            pending = pending.NewBatchWithFailedRequests(retryable);
            pendingStepIds = retryable.Keys.ToList();
        }
    }

    /// <summary>One step's Retry-After (delta seconds or an HTTP date), or null when absent or unreadable.</summary>
    private static async Task<TimeSpan?> ReadRetryAfterAsync(BatchResponseContentCollection response, string stepId)
    {
        try
        {
            using var http = await response.GetResponseByIdAsync(stepId);
            var header = http.Headers.RetryAfter;
            if (header?.Delta is TimeSpan delta) return delta;
            if (header?.Date is DateTimeOffset date) return date - DateTimeOffset.UtcNow;
            return null;
        }
        catch
        {
            return null;
        }
    }
```

Then update the three call sites to pass the mailbox: in `CreateContactsBatchAsync`, `UpdateContactsBatchAsync` and `DeleteContactsBatchAsync` change `await ExecuteBatchWithRetryAsync(batchContent, stepIdToKey, results, …` to `await ExecuteBatchWithRetryAsync(mailboxEntraId, batchContent, stepIdToKey, results, …`.

If `response.GetResponseByIdAsync(stepId)` (the non-generic overload returning `HttpResponseMessage`) does not exist in the referenced `Microsoft.Graph.Core`, or `NewBatchWithFailedRequests` does not accept a `Dictionary<string, HttpStatusCode>`, STOP and report BLOCKED with the compiler error — do not substitute a different mechanism.

- [ ] **Step 5: Run the writer tests**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet --filter "FullyQualifiedName~ContactWriterTests" 2>&1 | tail -1`
Expected: all green — the file's previous count + 18 (5 facts + 7 + 6 theory rows).

If `CreateContactsBatchAsync_RetriesOnlyThrottledSteps…` fails on `Assert.Equal(throttledStepId, handler.RequestStepIds[1][0])`, the SDK renumbered the step in the retry batch: report it (the plan assumes `NewBatchWithFailedRequests` preserves ids, which `stepIdToKey` relies on) rather than weakening the assertion.

- [ ] **Step 6: Silence the worker's `/health` request logging (§4.5)**

In `worker/Program.cs` replace

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateLogger();
```

with

```csharp
Log.Logger = new LoggerConfiguration()
    // Phase 4 (§4.5): the Docker health check hits /health every 30 s and ASP.NET Core logged five
    // Information lines per probe, burying the sync log. Startup (Microsoft.Hosting.Lifetime),
    // Hangfire and the app's own categories are unaffected.
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateLogger();
```

Run: `dotnet build worker --nologo -v quiet 2>&1 | grep -E "error|warning CS" | grep -v CS9113 | head -3`
Expected: no output.

- [ ] **Step 7: Full unit + integration suites**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed!  - Failed: 0, Passed: 355, Skipped: 1` (337 + 18).

Run: `dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed!  - Failed: 0, Passed: 48, Skipped: 1` (unchanged).

- [ ] **Step 8: Commit**

```bash
git add worker/Services/ContactWriter.cs worker/Program.cs tests/AFHSync.Tests.Unit/Sync/ContactWriterTests.cs
git commit -m "feat(worker): retry throttled/timed-out \$batch steps up to 3× honouring Retry-After (counts ThrottleEvents); silence /health request logging

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Full verification and PR

**Files:** none new.

- [ ] **Step 1: Gates**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed!  - Failed: 0, Passed: 355, Skipped: 1`.

Run: `docker compose up -d postgres && sleep 3 && .superpowers/sdd/2026-08-26-sync-reliability-phase-2/run-integration.sh 2>&1 | tail -1`
Expected: `Passed!  - Failed: 0, Passed: 49, Skipped: 0`.

Run: `cd frontend && npm run build 2>&1 | tail -2 && npm test 2>&1 | grep "Tests "; cd ..`
Expected: `✓ Compiled successfully`; `Tests  19 passed (19)`.

Run: `git status --short ; git log --oneline main..HEAD | cat`
Expected: empty status; 3 commits (spec, §4.1, §4.2+§4.5) plus this plan if committed on the branch.

- [ ] **Step 2: Integration choice**

Present the merge / PR / keep options (superpowers:finishing-a-development-branch). PR body:

```markdown
## Why
AddMissing fields (default profile: Department) drove the delta hash, so a source change in a field we
never write to existing contacts still PATCHed them; `$batch` steps that Graph throttled or timed out
(429/503/504) were recorded as failures and only recreated a run later, and never counted as throttle
events; and the worker's `docker logs` were five `/health` lines per 30 s.
Spec: docs/superpowers/specs/2026-08-25-sync-reliability-design.md (Phase 4 §4.1, §4.2, §4.5).

## What
- §4.1 `ContactPayloadBuilder` excludes AddMissing from the hash and returns the legacy hash; `SyncEngine`
  rewrites a stored hash that matches the legacy formula locally (`LastResult = "rehashed"`, no Graph
  write) — the formula change migrates itself on the next run instead of PATCHing ~1M contacts. No
  runbook, no cron pause.
- §4.2 `ContactWriter` re-posts only the 429/503/504 steps (`NewBatchWithFailedRequests`) up to 3×,
  waiting the largest `Retry-After` (≤ 5 min) else 2 s × attempt; each retried step increments
  `ThrottleCounter`; a transport failure on a retry marks only that retry batch `OutcomeUnknown`.
- §4.5 Serilog override `Microsoft.AspNetCore` → Warning in the worker.

## Tests
- Unit: 355 passed / 1 pre-existing skip (was 331): builder hash/legacy ×4, engine rehash ×2, batch retry
  ×5 facts + 13 theory rows.
- Integration: 49/0 with Postgres (unchanged — no API or schema change). Frontend untouched (19/19).

## Deploy
1. `./deploy.sh` (let it pull; only `worker/` changed ⇒ it rebuilds the worker). No migration.
2. First run after deploy: `docker logs afh-worker | grep Rehashed` shows `Rehashed N contact state(s) …`
   per mailbox (N ≈ every contact whose profile has an AddMissing value — Department in the default
   profile); run detail shows those contacts as **skipped**, not updated, and `contactsUpdated` stays
   small. The second run logs no rehashes.
3. `docker logs afh-worker` no longer shows `/health` request lines; `Retrying N throttled batch step(s)…`
   appears when Graph throttles, and the run's Throttle Events counter now includes batch-level retries.
4. Rollback: revert the worker image; rows already rehashed carry the new formula, which the old code would
   treat as changed and PATCH once — harmless.
```

---

## Self-review

### 1. Spec coverage (Phase 4 → task)

| Spec bullet | Task |
|---|---|
| §4.1 AddMissing excluded from `hashInput` | 2 |
| §4.1 (amended) legacy hash + local rehash, `PreviousDataHash`, `LastResult = "rehashed"`, never in a dry run, no runbook | 2 |
| §4.2 429/503/504 steps re-posted via `NewBatchWithFailedRequests` ≤ 3×, `Retry-After` (≤ 5 min) else 2 s × attempt | 3 |
| §4.2 `ThrottleCounter` per retried step; final failure keeps `HTTP {status}`; retry transport failure ⇒ `OutcomeUnknown` for that batch only; injectable delay | 3 |
| §4.5 worker `/health` logging | 3 |
| Tests / gates | 2, 3, 4 |
| §4.3, §4.4 | already shipped (`68e6dd6`) |

### 2. Placeholder scan

No "TBD"/"TODO"/"similar to Task N". Every code step carries the code; every run step its command and expected output. Two SDK assumptions are called out with explicit STOP-and-report instructions rather than left implicit: `GetResponseByIdAsync(stepId)` returning `HttpResponseMessage`, and `NewBatchWithFailedRequests` preserving step ids. Expected counts: unit 331 → 337 (Task 2) → 355 (Task 3); integration unchanged; vitest unchanged.

### 3. Type consistency

- `ContactPayloadResult(Payload, DataHash, LegacyDataHash = null)`: the builder's `new ContactPayloadResult(payload, hash, legacyHash)`, the engine's `result.LegacyDataHash`, and the test fake's `new ContactPayloadResult(payload, "new-hash", LegacyHash)` agree; the existing positional two-argument call in the fake still compiles.
- `ClassifyContacts` returns a 3-tuple `(pendingCreates, pendingUpdates, rehashes)`; the orchestrator deconstructs three names and passes `rehashes` to `RehashStatesAsync(List<(int stateId, string oldHash, string newHash)>, int, bool)`.
- `ContactWriter(GraphClientFactory, ThrottleCounter, ILogger<ContactWriter>, Func<TimeSpan, Task>? = null)`: the test helper passes four arguments in that order; the worker DI registration is unchanged and resolves `ThrottleCounter` (singleton) with the optional delay defaulted.
- `ExecuteBatchWithRetryAsync(string mailboxEntraId, BatchRequestContentCollection, Dictionary<string,string>, Dictionary<string,BatchOperationResult>, Func<…>)`: all three call sites updated to pass `mailboxEntraId` first.
- `IsRetryableStepStatus(HttpStatusCode)` / `RetryDelayFor(int, TimeSpan?)` are `internal static`; `InternalsVisibleTo("AFHSync.Tests.Unit")` exists on the worker project, and the theories cast `int` → `HttpStatusCode`.
