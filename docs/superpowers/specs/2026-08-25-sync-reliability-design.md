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
| `HiddenFromAddressListsEnabled` | any comparison → `true` (GAL-hidden filtering happens in `SourceResolver`), record an informational note |
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

One EF migration: `target_mailboxes.mailbox_unavailable_at timestamptz null`, new table `tunnel_mailbox_folders`, `sync_runs.requested_tunnel_ids text null`.

- **2.1 No-mailbox users.** On a mailbox-level Graph failure whose error code is `MailboxNotEnabledForRESTAPI` (or message contains "inactive, soft-deleted, or is hosted on-premise"), set `MailboxUnavailableAt = now`. `LoadTargetMailboxesAsync` excludes rows where `MailboxUnavailableAt > now - 7d`; older stamps are re-probed and cleared on any successful mailbox operation. Excluded mailboxes are counted in the tunnel log line, not as failures. No run item is written.
- **2.2 Dry runs write nothing.** `ProcessMailboxAsync` with `isDryRun`: no `contact_sync_state` insert/update/delete, no duplicate cleanup, no stale handling; run items still emitted. `ContactWriter.CreateContactsBatchAsync`: a step whose response lacks an `id` is `Success=false, Error="no contact id in response"`.
- **2.3 Failed source ⇒ no stale pass.** `SourceResolver.ResolveAsync` returns `SourceResolution(List<SourceUser> Users, IReadOnlyList<string> FailedSources)`. If `FailedSources` is non-empty the tunnel skips `StaleContactHandler` for every mailbox this run, logs Error, adds to `tunnelErrors`, counts as warned.
- **2.4 Stale reset.** `StaleContactHandler`: states whose `SourceUserId` is in the current set and `IsStale=true` → `IsStale=false, StaleDetectedAt=null`, saved in the same transaction.
- **2.5 Folder identity.** `tunnel_mailbox_folders(id, tunnel_id, target_mailbox_id, graph_folder_id, folder_name, updated_at; unique(tunnel_id,target_mailbox_id))`. `ContactFolderManager.EnsureFolderAsync`: lookup row → GET folder by id (404 → fall through) → else search by name → else create; upsert the row. If `folder_name != tunnel.Name`, PATCH `displayName` and update the row. The existing "folder was created ⇒ wipe sync state" behaviour is retained only when a folder was genuinely created. Edit page: rename flagged high-impact with the explanation "the folder will be renamed on every phone at the next sync".
- **2.6 Atomic write bookkeeping.** `ContactWriter` batch methods report results per chunk; `SyncEngine` persists the corresponding state rows immediately after each chunk using `CancellationToken.None`. The run's tunnel loop checks `ct.IsCancellationRequested` and breaks (marking the run Cancelled) instead of iterating remaining tunnels.
- **2.7 Explicit run claiming.** `POST /api/sync-runs` creates the row with `RequestedTunnelIds` + `IsDryRun`, enqueues **one** job `RunAsync(runId)`; enqueue failure marks the row Failed. `SyncEngine.RunAsync(int runId, …)` claims that row (advisory lock retained for the Running guard); the cron path calls `RunAsync(runId: null, RunType.Scheduled)` and always creates its own row. `StaleRunCleanupService` fails Pending rows older than 10 minutes. `StopSync` cancels by the stored Hangfire job id.
- **2.8 Notes prefix.** `ContactWriter.MapPayloadToContact` sets `PersonalNotes` only when `PersonalNotes` is in the payload or the contact is being created.

Tests: unit per item using the in-memory DbContext patterns in `tests/AFHSync.Tests.Unit/Sync`; `MigrationTests` covers the new schema.

## Phase 3 — API/UI correctness

Migration: new table `sync_run_tunnels(id, sync_run_id, tunnel_id, status, targets_count, contacts_created, contacts_updated, contacts_removed, contacts_skipped, contacts_failed, error_summary, started_at, completed_at)`.

- **3.1 Per-tunnel run records.** Written by `SyncEngine` after every tunnel (including zero-activity and skipped-for-error tunnels). `SyncRunsController` builds `tunnelSummaries` from it (errors still from run items). `TunnelsController` computes `LastSync` per tunnel and `TargetUserCount` from the latest record's `targets_count`.
- **3.2 Edit-page target scope.** Same validation as the wizard; `Select` state uses the scope enum, not truthiness; `TunnelsController.Create/Update` reject `targetUserEmails` that deserialize to an empty array and empty-string `targetGroupId` (400).
- **3.3 Pagination.** `/sync-runs`, `/sync-runs/{id}/items`, `/phone-lists/{id}/contacts` return `{ items, hasMore }` (phone-list contacts additionally return `total`); hooks request `pageSize` exactly. `PhoneListsController` clamps `pageSize` to `[1,500]`; lists page shows "N of M".
- **3.4 Graph pickers.** `GET /graph/ddgs/{id}/members` accepts `page,pageSize` and pages Graph via `PageIterator`; `GET /graph/security-groups` pages fully (cap 2000); `GET /graph/users/search` escapes `'`; impact preview counts use `@odata.count`.
- **3.5 Contact Filters.** `ContactExclusionsController.ResolveMailboxContactsAsync` honors `ContactFolderId` and pages; exclusion replace runs in one transaction with `DistinctBy(EntraId)`.
- **3.6 Lifetimes.** `DDGResolver` registered `Singleton` in api and worker; `GetOrCreatePowerShell` disposes the runspace when `Connect-ExchangeOnline` fails; a command failing with a session/auth error (`UnauthorizedAccessException`, "session", "token") resets and retries once; `Dispose` runs `Disconnect-ExchangeOnline`. `[AutomaticRetry(Attempts = 0)]` moves to `ICleanupJobRunner.RunAsync`. `RefreshTargetMailboxesAsync` runs once per sync run (memo) and is used by the group-scope path too.

## Phase 4 — Deferred

- **4.1 AddMissing excluded from hash.** `ContactPayloadBuilder` excludes AddMissing fields from `hashInput`. Causes a one-time full update wave; ship with a runbook step: pause the cron (`sync_schedule_cron`), deploy, trigger one manual run off-hours, re-enable.
- **4.2 `$batch` per-step retry.** In `ExecuteBatchWithRetryAsync`, steps returning 429/503/504 are retried up to 3× honoring `Retry-After`; each retry increments `ThrottleCounter`.

## Out of scope

Changing DDG definitions in Exchange; redesigning the Specific-Users source as a new source type (hidden button is sufficient); any UI redesign.

## Process

Branch per phase (`sync-reliability/phase-N`) from `main`, PR to `github.com/nickafh/sync`, merge, `./deploy.sh` on the box, verify, then next phase. `dotnet test` (unit + integration) and `npm test`/`npm run build` in `frontend/` must pass before each PR.
