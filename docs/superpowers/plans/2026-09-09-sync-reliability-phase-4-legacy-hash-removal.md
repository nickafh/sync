# Sync Reliability — Phase 4 follow-up: remove the legacy-hash migration

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete the Phase 4 §4.1 legacy-hash migration (`ContactPayloadResult.LegacyDataHash`, the builder's legacy AddMissing hash, `SyncEngine`'s rehash classification and `RehashStatesAsync`) now that production verification shows it can never fire again, and record why in the spec.

**Architecture:** Pure removal, no behaviour change for any contact in production. `ContactPayloadBuilder` keeps excluding AddMissing fields from the hash (the §4.1 guarantee) but no longer computes a second hash; `ContactPayloadResult` goes back to `(Payload, DataHash)`; `SyncEngine.ClassifyContacts` returns `(pendingCreates, pendingUpdates)` and the orchestrator no longer calls a rehash step. The `contact_sync_state.previous_data_hash` column stays — the ordinary update path writes it. No schema change, no migration, no API or frontend change.

**Tech Stack:** .NET 10 worker, xUnit 2.9 with EF Core 10 InMemory (unit) and Postgres 16 via Docker Compose (integration). Frontend untouched.

**Spec:** `docs/superpowers/specs/2026-08-25-sync-reliability-design.md` — Phase 4 §4.1 (amended by Task 1 of this plan). Deploy notes as shipped: `docs/superpowers/plans/2026-08-27-sync-reliability-phase-4.md` Task 4 Step 2.

## Why now (evidence gathered 2026-09-09, read-only, on the VM)

- Worker container `afh-worker` was created 2026-08-27 20:26 UTC (the Phase 4 deploy); its log runs unbroken to today and contains **zero** `Rehashed` lines (case-insensitive grep).
- `contact_sync_state.last_result` holds only `updated` (878,186) and `created` (150,643); **no** `rehashed` rows.
- The first contact run after the deploy (sync_runs id 844, manual, 2026-08-27 20:35 UTC) reported `contacts_updated = 0`, `contacts_skipped = 978,086`. Every run since reports `contacts_updated = 0` and ~980k skipped. So every stored hash already equals the current formula: no rehash was needed and no update wave happened.
- Root cause of "nothing to migrate": the production `field_profile_fields` table (one profile, `Default`) has **no** row with `behavior = 'add_missing'`. Department — the field the spec assumed was AddMissing — is `nosync`; every other field is `always` or `nosync`. `LegacyDataHash` is null whenever no AddMissing field contributes a value, so it was null for every contact and the legacy branch never matched.
- §4.2 and §4.5 verified at the same time: 38 `Retrying N throttled batch step(s)` lines across five days since deploy, run `throttle_events` > 0 on those days, and zero `GET /health` request-log lines.

The spec's own retirement rule ("can be deleted once a full run has completed — every row then carries the new formula") is therefore satisfied.

## Global Constraints

- Branch `sync-reliability/phase-4-followup` from `main`; PR to `github.com/nickafh/sync`; `./deploy.sh` on the box after merge (spec §Process).
- `dotnet test` (unit + integration) and `npm test` / `npm run build` in `frontend/` must pass before the PR (spec §Process).
- AddMissing fields stay **excluded** from the delta hash (spec §4.1, unchanged).
- No database migration; `previous_data_hash` is not dropped (the update path at `SyncEngine.cs:1366` writes it).
- Commit trailer on every commit: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

---

### Task 1: Branch, baseline, spec amendment

**Files:**
- Modify: `docs/superpowers/specs/2026-08-25-sync-reliability-design.md:159` (the §4.1 bullet)
- Modify: `docs/superpowers/plans/2026-08-27-sync-reliability-phase-4.md` (deploy-note step 2, "as verified")
- Commit (pre-existing, unrelated): `.gitignore` — the `.planning/` ignore line was dropped when GSD was retired on 2026-09-09; it is sitting uncommitted on `main`.

- [ ] **Step 1: Create the branch and commit the pending `.gitignore` change**

Run:
```bash
git checkout -b sync-reliability/phase-4-followup main
git status --short
```
Expected: exactly one line, ` M .gitignore`. Then:
```bash
git add .gitignore
git commit -m "chore: drop .planning from .gitignore (GSD retired 2026-09-09)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git status --short
```
Expected: empty.

- [ ] **Step 2: Record the baseline**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed!  - Failed:     0, Passed:   356, Skipped:     1, Total:   357`.

- [ ] **Step 3: Amend spec §4.1**

In `docs/superpowers/specs/2026-08-25-sync-reliability-design.md`, the §4.1 bullet (line 159) currently ends with:

```
The legacy computation can be deleted once a full run has completed (every row then carries the new formula). No cron pause or off-hours run is needed.
```

Replace that ending with:

```
The legacy computation can be deleted once a full run has completed (every row then carries the new formula). No cron pause or off-hours run is needed. **Retired 2026-09-09.** Production verification (worker log since the 2026-08-27 deploy; `contact_sync_state.last_result`; `sync_runs` 844 onward) showed zero rehashes and zero updates on the first post-deploy run: the production Default profile has no `add_missing` field (Department is `nosync`), so `LegacyDataHash` was null for every contact and the two formulas never differed. Every stored hash carries the current formula, and `LegacyDataHash`, the builder's legacy computation and `SyncEngine.RehashStatesAsync` are removed. AddMissing stays excluded from the hash. Switching a field to `add_missing` later needs no migration: from `nosync` the hash input is unchanged; from `always` each contact with a value takes one hash-driven PATCH, as with any other profile change.
```

- [ ] **Step 4: Note the verified outcome in the Phase 4 plan's deploy notes**

In `docs/superpowers/plans/2026-08-27-sync-reliability-phase-4.md`, find the deploy-note line beginning:

```
2. First run after deploy: `docker logs afh-worker | grep Rehashed` shows `Rehashed N contact state(s) in
```

Insert this line directly **before** it (same list, so number it as an unnumbered note):

```
   > **As verified 2026-09-09:** N was 0 on every run — the production profile has no `add_missing` field, so nothing needed migrating and no update wave occurred (`contacts_updated = 0` on run 844 and after). The legacy-hash code is removed by `docs/superpowers/plans/2026-09-09-sync-reliability-phase-4-legacy-hash-removal.md`.
```

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-08-25-sync-reliability-design.md docs/superpowers/plans/2026-08-27-sync-reliability-phase-4.md docs/superpowers/plans/2026-09-09-sync-reliability-phase-4-legacy-hash-removal.md
git commit -m "docs(spec): §4.1 legacy-hash migration retired — production profile has no AddMissing field, nothing to migrate

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Remove the rehash path from `SyncEngine`

**Files:**
- Modify: `worker/Services/SyncEngine.cs:688-694` (orchestrator step D/D'), `:903-965` (`ClassifyContacts`), `:1234-1265` (`RehashStatesAsync`)
- Test: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs:1555-1622`

**Interfaces:**
- Consumes: `ContactPayloadResult.LegacyDataHash` still exists during this task (removed in Task 3), so the project compiles after each step.
- Produces: `ClassifyContacts(...)` now returns a 2-tuple `(List<(string key, int sourceUserId, SortedDictionary<string, string> payload, string dataHash)> pendingCreates, List<(string key, int sourceUserId, string graphContactId, int stateId, SortedDictionary<string, string> payload, string dataHash, string? previousHash)> pendingUpdates)`. `RehashStatesAsync` no longer exists.

- [ ] **Step 1: Delete the two rehash tests**

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` delete everything from the section header

```csharp
    // ==============================
    // Phase 4 (4.1): a stored hash from the old formula is rewritten locally, not PATCHed
    // ==============================
```

through the closing brace of `RunAsync_DryRun_DoesNotRehash` (the last line before the blank line that precedes `// Stub implementations`). That removes exactly two `[Fact]` methods: `RunAsync_StoredHashEqualsLegacyHash_RehashesWithoutGraphWrite` and `RunAsync_DryRun_DoesNotRehash`. Leave `FakeContactPayloadBuilder` alone for now (Task 3 edits it).

- [ ] **Step 2: Run the suite — still green, two fewer**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed!  - Failed:     0, Passed:   354, Skipped:     1`.

- [ ] **Step 3: Remove the rehash call from the orchestrator**

In `worker/Services/SyncEngine.cs` replace:

```csharp
        // D. Classify every source user as create / update / skip — no Graph calls.
        var (pendingCreates, pendingUpdates, rehashes) = ClassifyContacts(tunnel, canonicalPhoneList, mailbox, run,
            sourceUsers, fieldSettings, existingStates, counters);

        // D'. Phase 4 (§4.1): migrate rows hashed by the old formula — a local write, no Graph call.
        await RehashStatesAsync(rehashes, mailbox.Id, isDryRun);
```

with:

```csharp
        // D. Classify every source user as create / update / skip — no Graph calls.
        var (pendingCreates, pendingUpdates) = ClassifyContacts(tunnel, canonicalPhoneList, mailbox, run,
            sourceUsers, fieldSettings, existingStates, counters);
```

- [ ] **Step 4: Shrink `ClassifyContacts` to a 2-tuple and drop the legacy branch**

Replace the method signature:

```csharp
    private (List<(string key, int sourceUserId, SortedDictionary<string, string> payload, string dataHash)> pendingCreates,
             List<(string key, int sourceUserId, string graphContactId, int stateId, SortedDictionary<string, string> payload, string dataHash, string? previousHash)> pendingUpdates,
             List<(int stateId, string oldHash, string newHash)> rehashes)
        ClassifyContacts(
```

with:

```csharp
    private (List<(string key, int sourceUserId, SortedDictionary<string, string> payload, string dataHash)> pendingCreates,
             List<(string key, int sourceUserId, string graphContactId, int stateId, SortedDictionary<string, string> payload, string dataHash, string? previousHash)> pendingUpdates)
        ClassifyContacts(
```

Delete these two lines inside the method body:

```csharp
        // Phase 4 (§4.1): rows whose stored hash was written by the pre-Phase-4 formula.
        var rehashes = new List<(int stateId, string oldHash, string newHash)>();
```

Replace the classification chain:

```csharp
                if (existingState == null)
                {
                    pendingCreates.Add((sourceUser.Id.ToString(), sourceUser.Id, result.Payload, result.DataHash));
                }
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
```

with:

```csharp
                if (existingState == null)
                {
                    pendingCreates.Add((sourceUser.Id.ToString(), sourceUser.Id, result.Payload, result.DataHash));
                }
                else if (existingState.DataHash != result.DataHash)
                {
```

Replace the return:

```csharp
        return (pendingCreates, pendingUpdates, rehashes);
```

with:

```csharp
        return (pendingCreates, pendingUpdates);
```

- [ ] **Step 5: Delete `RehashStatesAsync`**

Delete the whole method including its `<summary>` block — from

```csharp
    /// <summary>
    /// Phase 4 (§4.1): rewrites the stored hash of rows whose value matched the pre-Phase-4
```

through the closing brace after

```csharp
        logger.LogInformation("Rehashed {Count} contact state(s) in mailbox {MailboxId} (AddMissing hash migration)", rehashed, mailboxId);
    }
```

leaving one blank line between the closing brace of `HealDeadStatesAsync` and the `HandleStaleContactsAsync` summary that follows.

- [ ] **Step 6: Build and run the suite**

Run: `dotnet build worker/AFHSync.Worker.csproj --nologo -v quiet > /dev/null 2>&1; echo "build exit: $?"`
Expected: `build exit: 0`.

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed!  - Failed:     0, Passed:   354, Skipped:     1`.

Run: `grep -n -i 'rehash' worker/Services/SyncEngine.cs`
Expected: no output.

- [ ] **Step 7: Commit**

```bash
git add worker/Services/SyncEngine.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -m "refactor(worker): remove the §4.1 rehash path — never fired in production, every stored hash carries the current formula

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Drop `LegacyDataHash` from the result record and the builder

**Files:**
- Modify: `worker/Services/IContactPayloadBuilder.cs:32-41` (`ContactPayloadResult`)
- Modify: `worker/Services/ContactPayloadBuilder.cs:15, 55, 71-73, 103-116, 141-150`
- Test: `tests/AFHSync.Tests.Unit/Sync/ContactPayloadBuilderTests.cs:131-189`
- Test: `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` (`FakeContactPayloadBuilder`, near the end of the file)

**Interfaces:**
- Consumes: Task 2's `SyncEngine`, which no longer reads `LegacyDataHash`.
- Produces: `public record ContactPayloadResult(SortedDictionary<string, string> Payload, string DataHash);` — two positional parameters, no optional third.

- [ ] **Step 1: Trim the builder tests to the surviving guarantee**

In `tests/AFHSync.Tests.Unit/Sync/ContactPayloadBuilderTests.cs`:

Replace the section header

```csharp
    // ==============================
    // Phase 4 (§4.1): AddMissing never drives the hash; the legacy hash lets old rows migrate
    // ==============================
```

with

```csharp
    // ==============================
    // Phase 4 (§4.1): AddMissing never drives the hash
    // ==============================
```

In `Hash_IgnoresAddMissingFields`, delete the line

```csharp
        Assert.NotEqual(a.LegacyDataHash, b.LegacyDataHash);
```

so the test ends with `Assert.Equal(a.DataHash, b.DataHash);            // only the Always field is hashed`.

Delete the two `[Fact]` methods `LegacyDataHash_IsTheOldFormula_AddMissingIncludedAsIfAlways` and `LegacyDataHash_IsNull_WhenNoAddMissingFieldContributed` in full (each from its `[Fact]` attribute through its closing brace, plus the blank line between them). `AddMissing_PayloadBehaviourIsUnchanged` stays.

- [ ] **Step 2: Simplify the engine test fake**

In `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` replace:

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

with:

```csharp
    /// <summary>
    /// Always returns hash "new-hash" so existing states with "old-hash" trigger updates,
    /// and states with "new-hash" are skipped.
    /// </summary>
    private sealed class FakeContactPayloadBuilder : IContactPayloadBuilder
    {
        public ContactPayloadResult BuildPayload(
            SourceUser source,
            IReadOnlyList<FieldProfileField> fieldSettings,
            ContactSyncState? existingState)
        {
            var payload = new SortedDictionary<string, string> { { "DisplayName", source.DisplayName ?? "Unknown" } };
            return new ContactPayloadResult(payload, "new-hash");
        }
    }
```

- [ ] **Step 3: Run the suite — green, two fewer**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed!  - Failed:     0, Passed:   352, Skipped:     1`. (The record still has the optional third parameter, so the tests compile; this step proves the deletions themselves broke nothing.)

- [ ] **Step 4: Remove the parameter from `ContactPayloadResult`**

In `worker/Services/IContactPayloadBuilder.cs` replace:

```csharp
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

with:

```csharp
/// <param name="DataHash">Lowercase hex SHA-256 of the hash input (Always + RemoveBlank fields).</param>
public record ContactPayloadResult(
    SortedDictionary<string, string> Payload,
    string DataHash);
```

- [ ] **Step 5: Remove the legacy computation from the builder**

In `worker/Services/ContactPayloadBuilder.cs`:

Line 15 — replace

```csharp
/// Per D-06: Nosync excludes; AddMissing writes on create only and never affects the hash (Phase 4 §4.1); Always always includes; RemoveBlank clears empty.
```

with

```csharp
/// Per D-06: Nosync excludes; AddMissing writes on create only and never affects the hash; Always always includes; RemoveBlank clears empty.
```

Line 55 — replace

```csharp
    /// - AddMissing: EXCLUDED from the hash (Phase 4 §4.1) — a change to a field we only add on create must not trigger an update; the value is still folded into LegacyDataHash so rows hashed by the old formula migrate without a Graph write
```

with

```csharp
    /// - AddMissing: excluded from the hash — a change to a field we only add on create must not trigger an update
```

Delete these three lines after the `hashInput` declaration:

```csharp
        // Phase 4 (§4.1): AddMissing values used to be hashed. Keep them in a side dictionary so
        // the legacy hash can still be computed for rows written before the formula changed.
        var legacyAddMissing = new SortedDictionary<string, string>(StringComparer.Ordinal);
```

Replace the AddMissing case:

```csharp
                case SyncBehavior.AddMissing:
                {
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
                }
```

with:

```csharp
                case SyncBehavior.AddMissing:
                {
                    // Hash: excluded — a change to a field we only add on create must not trigger an update.
                    // Payload: include only for new contacts (no existing sync state).
                    // When a contact already exists, the existing value is preserved.
                    var value = GetFieldValue(source, field.FieldName);
                    if (value is not null && existingState is null)
                    {
                        payload[field.FieldName] = value;
                    }
                    break;
                }
```

Replace the tail of the method:

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

with:

```csharp
        var hash = ComputeHash(hashInput);

        return new ContactPayloadResult(payload, hash);
```

- [ ] **Step 6: Build, run the suite, grep for leftovers**

Run: `dotnet build AFHSync.slnx --nologo -v quiet > /dev/null 2>&1; echo "build exit: $?"`
Expected: `build exit: 0` (the API and integration projects compile against the same record; NU1903 package-vulnerability warnings are pre-existing and not part of this plan).

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed!  - Failed:     0, Passed:   352, Skipped:     1`.

Run: `grep -rn -i 'LegacyDataHash\|LegacyHash\|legacyAddMissing\|legacyInput\|rehash' --include='*.cs' worker shared api tests`
Expected: no output.

- [ ] **Step 7: Commit**

```bash
git add worker/Services/IContactPayloadBuilder.cs worker/Services/ContactPayloadBuilder.cs tests/AFHSync.Tests.Unit/Sync/ContactPayloadBuilderTests.cs tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
git commit -m "refactor(worker): drop LegacyDataHash from ContactPayloadResult and the builder's legacy AddMissing hash

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Full verification and PR

**Files:** none new.

- [ ] **Step 1: Gates**

Run: `dotnet test tests/AFHSync.Tests.Unit --nologo -v quiet 2>&1 | tail -1`
Expected: `Passed!  - Failed:     0, Passed:   352, Skipped:     1`.

Run (the Postgres-backed migration test only executes when `AFHSYNC_TEST_PG` is set; never echo the password):
```bash
docker compose up -d postgres
PW=$(grep -E '^POSTGRES_PASSWORD=' .env | cut -d= -f2-)
AFHSYNC_TEST_PG="Host=localhost;Port=5432;Username=afhsync;Password=${PW};Database=postgres;Timeout=3" dotnet test tests/AFHSync.Tests.Integration --nologo -v quiet 2>&1 | tail -1
```
Expected: `Passed!  - Failed:     0, Passed:    49, Skipped:     0` (unchanged from Phase 4 — no API or schema change). Without the env var the same command reports `Passed: 48, Skipped: 1`.

Run: `cd frontend && npm run build 2>&1 | tail -2 && npm test 2>&1 | grep "Tests "; cd ..`
Expected: `✓ Compiled successfully`; `Tests  19 passed (19)` (untouched).

Run: `git status --short ; git log --oneline main..HEAD | cat`
Expected: empty status; four commits on the branch (chore .gitignore, docs(spec), refactor engine, refactor builder).

- [ ] **Step 2: Push and open the PR**

```bash
git push -u origin sync-reliability/phase-4-followup
gh pr create --base main --title "Remove the Phase 4 §4.1 legacy-hash migration (never fired in production)" --body-file .superpowers/sdd/2026-09-09-legacy-hash-removal/pr-body.md
```

PR body (`.superpowers/sdd/2026-09-09-legacy-hash-removal/pr-body.md`):

```markdown
## Why
Phase 4 §4.1 shipped a self-migrating hash change: rows whose stored hash matched the pre-Phase-4 formula
were rehashed locally instead of PATCHed. Production verification on 2026-09-09 (worker log since the
2026-08-27 deploy, `contact_sync_state.last_result`, `sync_runs` 844 onward) shows the path never fired —
zero `Rehashed` lines, zero `rehashed` rows, `contacts_updated = 0` on the first post-deploy run — because
the production Default profile has no `add_missing` field (Department is `nosync`). `LegacyDataHash` was
null for every contact, so the old and new formulas never differed and every stored hash already carries
the current formula. The spec's retirement condition is met.
Spec: docs/superpowers/specs/2026-08-25-sync-reliability-design.md (§4.1, amended).

## What
- `ContactPayloadResult` is `(Payload, DataHash)` again; the builder no longer computes a legacy hash.
  AddMissing fields remain excluded from the hash (`Hash_IgnoresAddMissingFields` still pins this).
- `SyncEngine.ClassifyContacts` returns `(pendingCreates, pendingUpdates)`; `RehashStatesAsync` and the
  legacy-match branch are gone. `previous_data_hash` stays (written by the ordinary update path).
- Spec §4.1 and the Phase 4 plan's deploy notes record the verified outcome.
- Also carries the `.gitignore` tidy-up from retiring GSD (drops the `.planning/` line).

## Tests
- Unit: 352 passed / 1 pre-existing skip (was 356: the four tests that existed only to pin the legacy hash
  and the rehash path are removed; `Hash_IgnoresAddMissingFields` and `AddMissing_PayloadBehaviourIsUnchanged`
  stay).
- Integration: 49/0 with Postgres (unchanged). Frontend untouched (19/19).

## Deploy
1. `./deploy.sh` (only `worker/` changed ⇒ it rebuilds the worker). No migration.
2. First run after deploy: `contacts_updated` stays 0 and `contacts_skipped` stays ≈ 980k, exactly as before;
   `docker logs afh-worker | grep -c Rehashed` stays 0.
3. Rollback: revert the worker image — free, no data was changed by this PR.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

If Nick prefers to merge locally (as for Phase 4), skip `gh pr create`, `git checkout main && git merge --ff-only sync-reliability/phase-4-followup && git push`, and keep the body above as the deploy notes.

---

## Self-review

### 1. Spec coverage

| Spec statement | Task |
|---|---|
| §4.1 "The legacy computation can be deleted once a full run has completed" | 2, 3 |
| §4.1 AddMissing stays excluded from `hashInput` | 3 (builder unchanged on this point; `Hash_IgnoresAddMissingFields` retained) |
| §4.1 amended with the verified outcome and the retirement | 1 |
| §Process — branch, gates, PR, deploy | 1, 4 |
| Phase 4 plan deploy notes reflect what actually happened | 1 |

No spec requirement is left without a task. Nothing in §4.2–§4.5 is touched.

### 2. Placeholder scan

No "TBD"/"TODO"/"similar to Task N". Every edit shows the exact before and after text; every run step has its command and expected output. Expected counts: unit 356 → 354 (Task 2) → 352 (Task 3); integration and frontend unchanged.

### 3. Type consistency

- `ContactPayloadResult(Payload, DataHash)`: the builder's `new ContactPayloadResult(payload, hash)` (Task 3 Step 5), the test fake's `new ContactPayloadResult(payload, "new-hash")` (Task 3 Step 2) and every other existing two-argument call site agree. Task 3 Step 3 runs the tests *before* the record loses its optional parameter, so the ordering never breaks the build.
- `ClassifyContacts` returns a 2-tuple; the orchestrator deconstructs two names (Task 2 Step 3) and nothing else in the engine consumed `rehashes` (grep in Task 2 Step 6 proves it).
- `SyncEngine.cs:1366` (`s.PreviousDataHash = update.PreviousHash`) is the surviving writer of `previous_data_hash`; it is untouched.
