# Sync Reliability — Design

**Date:** 2026-08-25
**Status:** Approved (sections reviewed in conversation)
**Scope:** Fix the DDG filter conversion failure that silently shrank the "Avalon Users" target set from 355 to 6 mailboxes, plus the other correctness bugs found during that investigation. Delivered in four phases, each its own branch/PR, deployed with `./deploy.sh` before the next starts.

## Background

On 2026-08-25 the Avalon Gate Code tunnel was updating only 6 mailboxes. Root cause chain:

1. Every DDG in the tenant now has a `RecipientFilter` of the form
   `((Office -eq 'X') -and (CustomAttribute3 -eq 'Staff')) -and (((RecipientTypeDetails -eq 'UserMailbox') -or (RecipientTypeDetails -eq 'SharedMailbox'))) -and (HiddenFromAddressListsEnabled -eq 'False') -or (((RecipientTypeDetails -eq 'MailContact') -and (CustomAttribute4 -eq 'DDL')) …) -and (-not(Name -like 'SystemMailbox{*')) …`
2. `api/Services/FilterConverter.cs` strips only the single-clause `-and (RecipientTypeDetails -eq 'X')` form. The OR-group and the MailContact branch leak into the Graph `$filter`; Graph rejects it (`Request_UnsupportedQuery`).
3. `Convert` returns `Success=true` with a warning; `TargetFilterResolver` catches the Graph exception per DDG and continues; the phone list degrades to its explicit emails only. Run status is "warning" anyway because 259 mailbox-less users fail every run, so nothing surfaced.

The last run that reached the full Avalon target set was #635 (2026-07-22). `/api/graph/ddgs` shows `memberCount 0` for all 16 DDGs. The real filters are captured in `2026-08-25-ddg-recipient-filters.json` (this directory) for use as test fixtures.

## Phase 1 — DDG filter conversion + failure visibility

### 1.1 OPATH parser (`api/Services/Opath/`)

- `OpathTokenizer`: parens, operators (`-eq -ne -like -notlike -gt -lt -ge -le -and -or -not`), single-quoted strings with `''` escape, `$true/$false`, bare identifiers (attribute names), numbers.
- `OpathParser`: recursive descent → AST: `And(l,r)`, `Or(l,r)`, `Not(x)`, `Compare(attr, op, value)`, `Const(bool)`. Precedence: `-not` > `-and` > `-or`; parens override.
- Parse errors throw `OpathParseException(message, position)`.

### 1.2 Conversion (`FilterConverter.Convert`)

Pipeline: parse → **fold** non-Graph predicates to constants → **simplify** → **render** OData.

Fold rules (attribute names case-insensitive):

| Attribute | Rule |
|---|---|
| `RecipientTypeDetails`, `RecipientType` | `-eq` to any of UserMailbox, SharedMailbox, RoomMailbox, EquipmentMailbox, MailUser, LinkedMailbox, TeamMailbox → `true`; `-eq` to anything else (MailContact, DynamicDistributionGroup, MailUniversalDistributionGroup, …) → `false`. `-ne` is the negation. |
| `RecipientTypeDetailsValue` | any comparison → `false` (Exchange only uses it inside `-not(...)` exclusions) |
| `Name -like 'SystemMailbox{*'`, `'CAS_{*'`, any `Name -like` | → `false` |
| `HiddenFromAddressListsEnabled` | any comparison → `true` (GAL-hidden filtering happens in `SourceResolver`) |
| Known Graph attributes (existing `AttributeMap`) | kept |
| Anything else | kept, added to `UnknownAttributes` |

Simplify: `And(true,x)=x`, `And(false,_)=false`, `Or(true,_)=true`, `Or(false,x)=x`, `Not(const)=!const`, `Not(Not(x))=x`; iterate to fixpoint. If the whole tree folds to `true`, the filter is "all users" — return `Success=false` with warning "filter matches all users" (a DDG source must be selective). If it folds to `false`, `Success=false` "filter matches no users".

Render: attribute rename happens on `Compare` nodes only (quoted values are never touched). `-eq/-ne` → `eq/ne`; `-like 'x*'` → `startsWith(f,'x')`, `'*x'` → `endsWith`, `'*x*'` → `contains`, no wildcard → `eq`; `-notlike` → `not(<like>)`; `-gt/-lt/-ge/-le` → `gt/lt/ge/le`. Values re-escaped (`'` → `''`). Parenthesize `Or` inside `And`; otherwise minimal parens.

Result type: `FilterConversionResult(bool Success, string Filter, string? Warning, IReadOnlyList<string> UnknownAttributes)`. **`Success=false` whenever `UnknownAttributes` is non-empty** or the parser fails. `ToPlainLanguage` uses the same AST (renders with the `PlainNameMap`, drops folded predicates), so it stops corrupting quoted values too.

Expected results for the fixture set, e.g. Buckhead Staff → `officeLocation eq 'Buckhead' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'` (77 users in the tenant). All 16 fixtures must convert with `Success=true`.

### 1.3 Consumers

- `TunnelsController.RefreshDdg`: on `!Success` return `422 { message }` and leave `SourceIdentifier` unchanged. On success it also stores `SourceFilterPlain`.
- `GraphController` DDG DTOs already carry `graphFilter`/`graphFilterSuccess`/`graphFilterWarning`; `graphFilter` is `null` when `!Success`.
- Frontend (`TunnelWizard.tsx`, `tunnels/[id]/page.tsx`, `DDGSearchList.tsx`, `lists/page.tsx` DDG target picker): a DDG with `graphFilterSuccess=false` is rendered disabled with the warning; the `graphFilter ?? recipientFilter` fallback is removed.
- `TargetFilterResolver.ResolveAsync` returns `TargetFilterResolution(HashSet<string> Emails, IReadOnlyList<DdgFailure> Failures)` where `DdgFailure(string Id, string? DisplayName, string Reason)`. Reasons: not found, conversion failed (+unknown attrs), Graph query threw (+message), resolved to 0 members.
- `SyncEngine.ProcessTunnelAsync`: for each failure, log at Error, add `"{tunnel}: DDG target '{name}' failed: {reason}"` to `tunnelErrors`, write a `SyncRunItem` with `Action="failed"`, `TunnelId` set, no source user/mailbox (both are nullable), and `ErrorMessage = "DDG target '{displayName}': {reason}"`, and count the tunnel as warned. The tunnel still processes the mailboxes it could resolve; no stale/removal side effects occur in unresolved mailboxes because they are simply not processed.

### 1.4 Bundled small fixes

- `DDGRefreshButton` is not rendered when `sourceSmtpAddress` is empty; its error toast shows `error.message`.
- `SourceResolver.MapGraphUserToSourceUser`: `Notes = extensionAttribute5` only; comments updated; the `ResetDataHashesForCloudNotes` migration doc-comment corrected (no code change).

### 1.5 Tests

- `OpathParserTests`: precedence, nesting, escapes, `$false`, parse errors.
- `FilterConverterTests`: all 16 fixtures → expected OData (golden strings); value-corruption regression (`Title -like 'Office Manager*'`); `-notlike`; unknown attribute ⇒ `Success=false`; all-true ⇒ `Success=false`.
- `TargetFilterResolverTests`: failure reported + explicit emails still returned; 0-member DDG reported.
- Frontend: there is no component test harness (only `sync-error-classifier.test.ts`), so the gate is `npm run build` (type-check + lint) plus a manual check of the wizard/edit/targets pickers against a DDG with `graphFilterSuccess=false`.

### 1.6 Deploy verification

`/api/graph/ddgs` shows non-zero `memberCount` for all 16 DDGs; a manual run's Avalon summary shows ~349 additional mailboxes updated with the September gate code; run 'errors' list contains no DDG failures.

## Phase 2 — Data integrity

**Status:** Approved 2026-08-26 (sections reviewed in conversation). Branch `sync-reliability/phase-2`, one migration, one deploy.

**Code-reality findings that shaped this section** (2026-08-26 code map): the 259 per-run failures are enabled Entra accounts without a REST-enabled mailbox; the existing "self-heal" sets `IsActive=false` and `RefreshTargetMailboxesAsync` flips it back on the next refresh, so they fail every run. Dry runs today create Graph folders, wipe/delete `contact_sync_state`, and insert state rows with `GraphContactId = null` and `LastResult = "created"` — which a real run then treats as already synced. Every `RunAsync` caller passes `CancellationToken.None`; cancellation only happens via the `cancel_sync` flag between tunnels. A multi-tunnel manual trigger enqueues one Hangfire job per tunnel and whichever runs first claims the single Pending row; the others create unlinked rows. Nothing at worker startup reconciles a row left `Running` by a dead process, `RunAsync` has Hangfire's default 10 retries, and the Running guard is run-type-agnostic (a Running photo-sync row blocks contact runs). `ContactWriter` batch results are aggregated across 20-op chunks and persisted once per mailbox. `MapPayloadToContact` sets `PersonalNotes` whenever `OfficeLocation` is present, ignoring whether notes are in the payload.

### 2.0 Migration (one)

- `target_mailboxes`: `mailbox_unavailable_at timestamptz null`, `mailbox_last_probed_at timestamptz null`, `mailbox_unavailable_reason text null`. `IsActive` keeps its meaning (exists and enabled in Entra); the `IsActive=false` self-heal on the no-mailbox error is removed.
- New table `tunnel_mailbox_folders(id, tunnel_id, target_mailbox_id, graph_folder_id, folder_name, updated_at)`, unique `(tunnel_id, target_mailbox_id)`, cascade delete with tunnel and mailbox.
- `sync_runs.requested_tunnel_ids text null` (JSON array of tunnel ids; null = all). `hangfire_job_ids` stays and holds one id.
- Data fix-up in the same migration: `DELETE FROM contact_sync_state WHERE graph_contact_id IS NULL` (dry-run artifacts and lost-id creates; the next real run recreates the contact). Deploy step 1 counts them first.
- Applied at API startup as today; the worker assumes the schema.

### 2.1 Unavailable mailboxes

- In `ProcessMailboxAsync`, a folder-lookup failure whose `ODataError.Error.Code == "MailboxNotEnabledForRESTAPI"` or whose message contains "inactive, soft-deleted, or is hosted on-premise" is classified *unavailable*: set `MailboxUnavailableAt` (if null), `MailboxLastProbedAt = now`, `MailboxUnavailableReason = message`; log Information; write no run item; do not count as a failure. Any other error remains a failure as today.
- `LoadTargetMailboxesAsync` excludes rows where `MailboxUnavailableAt IS NOT NULL AND MailboxLastProbedAt > now - 7d`; older stamps are included (weekly re-probe, forever). The first successful folder lookup clears all three columns. Each tunnel's log line reports `N excluded (unavailable)`.
- UI: Targets page gains an **Unavailable mailboxes** section (name, email, since, last checked, reason; oldest first) backed by `GET /api/targets/unavailable`, with an "N of M" header so it reconciles with the dashboard's Target Users count.

### 2.2 Dry runs write nothing

- `ProcessMailboxAsync(isDryRun: true)`: folder is looked up, never created (no folder ⇒ every contact is "would create"); no `contact_sync_state` insert/update/delete; no duplicate cleanup (Graph or DB); no stale pass; run items still emitted. The dry-run branches stop populating `statesToAdd`/`statesToUpdate`; the final `SaveChangesAsync` and both `ExecuteDeleteAsync` calls are guarded.
- `ContactWriter.CreateContactsBatchAsync`: a step whose response lacks an `id`, or whose response fails to parse, is `Success=false, Error="no contact id in response"`; no state row is written for it.

### 2.3 Failed source ⇒ no stale pass

- `SourceResolver.ResolveAsync` returns `SourceResolution(List<SourceUser> Users, IReadOnlyList<SourceFailure> FailedSources)`, `SourceFailure(int SourceId, string DisplayName, string Reason)`; the existing per-source catch records the failure and continues.
- `ProcessTunnelAsync`: for each failure log Error, add `"{tunnel}: source '{name}' failed: {reason}"` to `tunnelErrors`, write a `SyncRunItem` (`Action="failed"`, `TunnelId`, `ErrorMessage = "Source '{name}': {reason}"`), count the tunnel as warned, and call `ProcessMailboxAsync` with `skipStale: true` for every mailbox. Contacts from the sources that resolved are still created/updated. Zero users still short-circuits the tunnel as today.

### 2.4 Stale reset

- `StaleContactHandler`: states whose `SourceUserId` is in the current set and `IsStale=true` → `IsStale=false, StaleDetectedAt=null`, saved in the same transaction as the stale marking (FlagHold and Leave; AutoRemove deletes rows so nothing to reset).

### 2.5 Folder identity

- `ContactFolderManager.GetOrCreateFolderAsync(tunnel, mailbox, isDryRun)`: (1) `tunnel_mailbox_folders` row → `GET /contactFolders/{id}`; found ⇒ use, 404 ⇒ fall through; (2) search by name; (3) create (skipped in dry run); (4) upsert the row with id and current name; `wasCreated` is true only for (3) and only that triggers the existing state wipe; (5) if `folder_name != tunnel.Name`, `PATCH displayName` and update the row; if the PATCH fails, log Warning, still use the resolved folder, and leave the stored name unchanged so the rename is retried next run. Run-scoped in-memory cache stays as the first check.
- UI: tunnel edit page flags a name change as high-impact: "The contact folder will be renamed on every phone at the next sync."

### 2.6 Durable bookkeeping and cancellation

- `ContactWriter` batch methods accept `Func<IReadOnlyDictionary<string, BatchOperationResult>, Task> onChunkCompleted`, invoked after each 20-op chunk; `SyncEngine` persists that chunk's state rows in the callback with `CancellationToken.None`. The end-of-mailbox `SaveChangesAsync` remains for heals only. A crash loses at most the chunk in flight (≤20 contacts per mailbox). Nothing reconciles Graph contacts that have no state row today; that window is bounded, not closed — see Phase 3.7.
- Hangfire injects its shutdown token into `RunAsync`'s `ct`. The tunnel loop and the mailbox loop check `ct.IsCancellationRequested` at each boundary; on cancellation the run is finalized `Cancelled` with `"worker shutting down"` using `CancellationToken.None`. `compose.yaml` sets `stop_grace_period: 60s` on the worker. The `cancel_sync` flag keeps serving Stop Sync.

### 2.7 Explicit run claiming

- `POST /api/sync-runs` creates the row (`Pending`, `RunType`, `IsDryRun`, `RequestedTunnelIds`) and enqueues **one** job `RunAsync(runId)`; enqueue failure marks the row `Failed`. The per-tunnel fan-out is removed.
- `ISyncEngine.RunAsync(int? runId, RunType runType, bool isDryRun, CancellationToken ct)`: under the existing advisory lock, `runId` given ⇒ claim that row (`Pending → Running`; already finalized ⇒ return without work); `runId` null (cron) ⇒ if any row is `Pending` or `Running`, skip (no row created, Information log); otherwise create a new row from the `runType`/`isDryRun` arguments. Once a row is claimed or created, `RunType`, `IsDryRun`, and the tunnel list are read from the row, never from the arguments. `[AutomaticRetry(Attempts = 0)]` on the interface method.
- Worker startup, before the Hangfire server starts: every `Running` row → `Failed`, `ErrorSummary = "interrupted by worker restart"`; `cancel_sync` cleared. Nothing is auto-restarted.
- `StaleRunCleanupService` also fails `Pending` rows older than 10 minutes.
- `StopSync` unchanged (flag + force-cancel + delete by stored job id).
- Photo sync (`PhotoSyncService.RunAllAsync`) creates and claims its row through the same locked path (`RunType.PhotoSync`), gets the retry-off attribute and the startup reconcile. The single "one run at a time" lane across run types stays — photo sync writes the same contacts.

### 2.8 Notes prefix

- `ContactWriter.MapPayloadToContact(payload, isCreate)` sets `PersonalNotes` only when `"PersonalNotes"` is a key in the payload or `isCreate` is true. On updates where the field profile omits notes (AddMissing), phone-side edits survive even when `OfficeLocation` is synced.

### 2.9 Tests

- Unit (in-memory DbContext; fakes updated: `FakeSourceResolver` → `SourceResolution`, `FakeContactWriter` gains a no-id step mode and the chunk callback, `FakeContactFolderManager` gains by-id/404/rename paths): 2.1 stamp/no-item/exclude-within-7d/re-include-after/clear-on-success/other-error-still-fails; 2.7 claim-by-id, finalized-row no-op, startup reconcile, Pending>10min failed, cancelled token ⇒ `Cancelled` with no further tunnels; 2.3 partial failure ⇒ run item + `tunnelErrors` + stale handler not invoked + other sources written; 2.4 reset; 2.2 dry run leaves state and folder untouched, no-id step ⇒ `Success=false`; 2.6 second chunk throws ⇒ first chunk persisted; 2.8 update without notes leaves them, create sets prefix; 2.5 by-id hit, 404 fallthrough, rename PATCH, `wasCreated` only on create.
- Integration: replace the stub `MigrationTests` with a real one asserting the new columns, table, unique index, and `requested_tunnel_ids` after `MigrateAsync` on the test Postgres.
- Gates: `dotnet test` (unit + integration) and `npm run build`.

### 2.10 Deploy verification

1. Before: `docker exec afh-postgres psql -U afhsync -d afhsync -c "SELECT COUNT(*) FROM contact_sync_state WHERE graph_contact_id IS NULL;"` — the number the migration deletes.
2. With no run in progress, `./deploy.sh` (no manual `git pull` first — the script diffs its own pull).
3. After: Targets page lists ~259 unavailable mailboxes; a manual run ends **Success** (not Warning) when nothing else is wrong; the worker log shows `N excluded (unavailable)` per tunnel (run-detail placement needs Phase 3.1's per-tunnel run records); a run started during a deploy ends `Cancelled — worker shutting down`, never orphaned.

## Phase 3 — API/UI correctness

Migration: new table `sync_run_tunnels(id, sync_run_id, tunnel_id, status, targets_count, contacts_created, contacts_updated, contacts_removed, contacts_skipped, contacts_failed, error_summary, started_at, completed_at)`.

- **3.1 Per-tunnel run records.** Written by `SyncEngine` after every tunnel (including zero-activity and skipped-for-error tunnels). `SyncRunsController` builds `tunnelSummaries` from it (errors still from run items). `TunnelsController` computes `LastSync` per tunnel and `TargetUserCount` from the latest record's `targets_count`.
- **3.2 Edit-page target scope.** Same validation as the wizard; `Select` state uses the scope enum, not truthiness; `TunnelsController.Create/Update` reject `targetUserEmails` that deserialize to an empty array and empty-string `targetGroupId` (400).
- **3.3 Pagination.** `/sync-runs`, `/sync-runs/{id}/items`, `/phone-lists/{id}/contacts` return `{ items, hasMore }` (phone-list contacts additionally return `total`); hooks request `pageSize` exactly. `PhoneListsController` clamps `pageSize` to `[1,500]`; lists page shows "N of M".
- **3.4 Graph pickers.** `GET /graph/ddgs/{id}/members` accepts `page,pageSize` and pages Graph via `PageIterator`; `GET /graph/security-groups` pages fully (cap 2000); `GET /graph/users/search` escapes `'`; impact preview counts use `@odata.count`.
- **3.5 Contact Filters.** `ContactExclusionsController.ResolveMailboxContactsAsync` honors `ContactFolderId` and pages; exclusion replace runs in one transaction with `DistinctBy(EntraId)`.
- **3.6 Lifetimes.** `DDGResolver` registered `Singleton` in api and worker; `GetOrCreatePowerShell` disposes the runspace when `Connect-ExchangeOnline` fails; a command failing with a session/auth error (`UnauthorizedAccessException`, "session", "token") resets and retries once; `Dispose` runs `Disconnect-ExchangeOnline`. `[AutomaticRetry(Attempts = 0)]` moves to `ICleanupJobRunner.RunAsync`. `RefreshTargetMailboxesAsync` runs once per sync run (memo) and is used by the group-scope path too.
- **3.7 Orphaned Graph contacts.** After an unknown-outcome batch, reconcile the folder's Graph contacts against `contact_sync_state` by a deterministic key and remove/adopt strays.

## Phase 4 — Deferred

- **4.1 AddMissing excluded from hash.** `ContactPayloadBuilder` excludes AddMissing fields from `hashInput`. Causes a one-time full update wave; ship with a runbook step: pause the cron (`sync_schedule_cron`), deploy, trigger one manual run off-hours, re-enable.
- **4.2 `$batch` per-step retry.** In `ExecuteBatchWithRetryAsync`, steps returning 429/503/504 are retried up to 3× honoring `Retry-After`; each retry increments `ThrottleCounter`.

## Out of scope

Changing DDG definitions in Exchange; redesigning the Specific-Users source as a new source type (hidden button is sufficient); any UI redesign.

## Process

Branch per phase (`sync-reliability/phase-N`) from `main`, PR to `github.com/nickafh/sync`, merge, `./deploy.sh` on the box, verify, then next phase. `dotnet test` (unit + integration) and `npm test`/`npm run build` in `frontend/` must pass before each PR.
