# State Scope and Folder Audit — Design

**Status:** Approved 2026-09-09 (approaches and design reviewed in conversation). Branch `sync-reliability/phase-5`, one migration, one deploy. Extends the Sync Reliability design (`2026-08-25-sync-reliability-design.md`): §3.7 folder reconcile and the Phase 2 stale pass.

## Background

**Code-reality findings that shaped this design** (2026-09-09 production investigation, triggered by Charlotte Hedgepeth appearing in jp@'s Outlook but not nick@'s after a manual run).

- `SyncEngine.ProcessTunnelAsync` builds `allPhoneListIds` from `tunnel.TunnelPhoneLists`. `LoadExistingStatesAsync` loads `contact_sync_state` rows for the (tunnel, mailbox) only where `phone_list_id` is in that list, and `HandleStaleContactsAsync` calls `StaleContactHandler.HandleStaleAsync` once per attached phone list, each call scoped to that list. A row whose phone list is no longer attached to its tunnel is therefore invisible to classification and to the stale pass. `FolderReconciler` treats any Graph id referenced by any row in the mailbox as "known", so it protects that row's Graph contact as well. Nothing ever revisits such a row.
- Production has exactly that: on 2026-04-17 tunnels 23, 24, 27–31 and 33 were moved from phone list 10, "Nick Jp test and david" (specific users: nick@, jp@, David@, kevingoldfinger@), to list 13, "All Users" (33 to list 12). 3,620 rows still carry `phone_list_id = 10` (905 per mailbox), all with `data_hash IS NULL`, untouched since April. 3,344 of them sit beside a list-13 row for the same user, tunnel and mailbox: a duplicate contact in the folder, which Outlook's linked-contact view shows as "email • email". 276 have no sibling: ex-members whose contact was never stale-removed. No list-10 row shares a Graph id with a list-13 row.
- The tunnel edit impact preview (`TunnelsController`, "removed target lists") already counts rows under a removed list as removals. The engine never performs them.
- A row whose hash matches the source is skipped without any Graph call, so a contact deleted outside the app (by the user, by a folder wipe during April testing) leaves a row pointing at nothing until the person's data changes and the PATCH 404s. nick@'s Charlotte row (id 17230, created 2026-04-17) is one: the row exists, the contact does not.
- Charlotte herself dropped out of the `officeLocation eq 'Buckhead'` and Blue Ridge queries between the 12:00 and 14:24 UTC runs on 2026-09-08; run 894 stale-removed her from ~1,030 active mailboxes. The fleet is correct. jp@ still shows her only because of the April list-10 ghost.
- Runs are scheduled by one Hangfire recurring job (`sync-all`, cron from `sync_schedule_cron`, production `0 0,12 * * *`). Per §2.7 a run's parameters come from its `sync_runs` row, never from job arguments.

## 5.0 Migration (one)

- `sync_runs.audit_folders boolean not null default false`.
- `tunnel_mailbox_folders.last_audited_at timestamptz null`.
- No data fix-up. The first real run after deploy cleans the list-10 rows through the paths in §5.1 and §5.2.
- Applied at API startup as today; the worker assumes the schema.

## 5.1 Tunnel-scoped state

- `LoadExistingStatesAsync` loads every `contact_sync_state` row with `tunnel_id = tunnel.Id AND target_mailbox_id = mailbox.Id`, regardless of `phone_list_id`. The dedupe is unchanged: one row per `SourceUserId`, preferring the canonical phone list, then the lowest id. The existing duplicate cleanup deletes the other rows and their Graph contacts (batch delete, counted as Removed, never in a dry run).
- After the cleanup, kept rows whose `phone_list_id` is not attached to the tunnel are re-pointed to the canonical phone list in one save (not in a dry run). The unique index `(source_user_id, phone_list_id, target_mailbox_id, tunnel_id)` cannot conflict: the duplicate rows are deleted and saved first, so after the dedupe there is exactly one row per source user for the pair. Per-mailbox bookkeeping in this section (duplicate row deletion, the folder-recreated wipe, the re-point) uses tracked EF operations rather than `ExecuteDelete`/`ExecuteUpdate`, so the unit tests exercise the real paths on the InMemory provider; the row counts involved are per (tunnel, mailbox). Log Information: `Re-pointed {Count} state row(s) from retired phone list(s) to list {PhoneListId} for tunnel {TunnelId} in mailbox {MailboxId}`.
- The "folder was recreated" wipe uses the same tunnel + mailbox scope.
- `IStaleContactHandler.HandleStaleAsync(tunnel, targetMailboxId, mailboxEntraId, currentSourceUserIds, ct)` loses `phoneListId`; the handler loads by tunnel + mailbox. `HandleStaleContactsAsync` calls it once per (tunnel, mailbox); its `removed` and `stale_detected` run items carry the canonical phone list id. Behaviour per policy (AutoRemove, FlagHold, Leave) and the §2.4 reset are unchanged.
- `allPhoneListIds` survives only as the set of attached lists used by the re-point decision.
- Effect on production at the first real run: in the four test mailboxes, 3,344 duplicate Graph contacts deleted with their rows (§5.1 cleanup) and the 276 ghost rows handled by §5.2 (contact gone) or the stale pass (contact present, member gone). Zero rows under list 10 afterwards. The other ~1,060 mailboxes have no retired-list rows and see no change from this section.

## 5.2 Folder audit

- `FolderReconciler.ReconcileAsync` reconciles both directions on every call. Strays (Graph contacts no row references) are adopted or removed exactly as today. New: **missing** rows, rows of *this* tunnel in this mailbox whose non-null `GraphContactId` is not in the folder listing, are deleted (tracked remove, saved with `CancellationToken.None` before any adoption is saved, so an adopted stray for the same user cannot collide with the dead row on the unique index) and reported back as their `SourceUserId`s in `FolderReconcileResult(Examined, Adopted, Removed, IReadOnlyList<int> MissingSourceUserIds)`. Rows of other tunnels are never considered missing (they reference other folders). Rows with a null Graph id are ignored. Classification runs after the reconcile, so a missing member's contact is recreated in the same run.
- The engine writes one run item per missing row (`Action = "audit_missing"`, `SourceUserId`, `TunnelId`, canonical `PhoneListId`, `TargetMailboxId`); the recreate then logs its usual `created` item. Log Information per reconcile: `Reconcile: tunnel {TunnelName} / mailbox {Email}: {Examined} Graph contact(s), {Adopted} adopted, {Removed} removed, {Missing} missing (recreated this run)`.
- Gating, in step B of `ProcessMailboxAsync`, never in a dry run and never when the folder was just created (the §2.5 wipe in step C covers that case):
  1. `reconcile_pending_at` set (existing §3.7 trigger), or
  2. `run.AuditFolders` is true, or
  3. `run.RunType == Scheduled` and the folder row's `last_audited_at` is null or its UTC date is before today's UTC date (no folder row ⇒ treated as null).
  With the production cron this is the 00:00 UTC run and nothing else; if that run never reaches a mailbox (cancelled, failed), the 12:00 run audits it. Manual runs audit only when asked.
- After a completed reconcile: `reconcile_pending_at` cleared as today and `last_audited_at = now` (same fresh-context, `CancellationToken.None` helper pattern as `SetReconcilePendingAsync`; no-op when the folder row does not exist).
- Failure handling. A folder listing or bookkeeping failure on a pending-flag reconcile (trigger 1) keeps today's behaviour: the exception reaches the per-mailbox catch, the mailbox counts as one failure and is skipped this run, and the flag stays set. On an audit-only reconcile (triggers 2 or 3 without 1) the failure is logged as a Warning with the reason, `last_audited_at` is left unchanged, nothing is counted, and the mailbox continues with step C.
- Cost: one paged listing (`$select=id`, `$top=999`) per (tunnel, mailbox) per day, about 7,500 Graph reads, roughly five minutes at concurrency 4. No new `sync_runs` counters: stray removals add to Removed as today; missing rows are visible as `audit_missing` items.
- The cron registration (`RunAsync(null, RunType.Scheduled, false, …)`) is unchanged. Scheduled rows are created with `audit_folders = false`; trigger 3 keys off `RunType`.

## 5.3 API and UI

- `TriggerSyncRequest` gains `bool AuditFolders = false`. `POST /api/sync-runs` stores it on the row like `IsDryRun`. `SyncRunDto` and `SyncRunDetailDto` gain `AuditFolders`.
- Dashboard: an **Audit folders** checkbox beside **Run Sync Now**, default off, hint "Re-checks every contact folder against Graph. Slower." The trigger sends `auditFolders`.
- Runs list and run detail: an `audit` status badge next to the existing `dry_run` badge when `auditFolders` is true. Run detail `ACTION_TABS` gains **Audit** (`audit_missing`).

## 5.4 Tests

- Unit (in-memory DbContext, existing fakes): `FolderReconcilerTests` — missing row deleted and reported; a row of another tunnel whose id is not in this folder is left alone; null-id rows ignored; existing stray tests unchanged. `StaleContactHandlerTests` — new signature; a row under an unattached phone list is removed (AutoRemove) or flagged (FlagHold). `SyncEngineTests` — retired-list row beside a canonical row for one user ⇒ retired row and its Graph contact deleted, canonical kept; retired-list row alone for a current member ⇒ used as existing (no create) and re-pointed to the canonical list; retired-list row alone for a departed member ⇒ removed by the stale pass; scheduled run audits when `last_audited_at` is null or yesterday and skips when today; manual run audits only with the flag; dry run never audits; folder just created ⇒ no audit; audit listing failure on a scheduled run ⇒ Warning, mailbox continues, stamp unchanged; pending-flag failure ⇒ mailbox failure as today; missing row ⇒ `audit_missing` item then `created` in the same run; stamp written after a completed audit. API (`SyncRunsController`) — `AuditFolders` stored and exposed.
- Integration (Postgres): migration asserts both columns (operations test plus the `MigrateAsync` column assertions). No engine-on-Postgres test for the re-point: the integration project has no engine harness, and a unique-index conflict is impossible by construction (§5.1: duplicates are deleted and saved before the re-point), which the engine unit tests cover.
- Integration (in-memory API host): `POST /api/sync-runs` stores `AuditFolders`; the run list and detail expose it; omitting it defaults to false.
- Frontend: `npm run build` and existing tests pass; the dashboard trigger payload carries `auditFolders`.
- Gates: the three verification commands in `CLAUDE.md`.

## 5.5 Deploy verification

1. Before: `SELECT s.phone_list_id, COUNT(*) FROM contact_sync_state s LEFT JOIN tunnel_phone_lists tp ON tp.tunnel_id = s.tunnel_id AND tp.phone_list_id = s.phone_list_id WHERE tp.id IS NULL GROUP BY 1;` (expect 3,620 under list 10). Note jp@'s and nick@'s "Your contacts" counts in Outlook (968 and 951 on 2026-09-09).
2. With no run in progress, `./deploy.sh` (migration applies at API startup).
3. Dashboard: **Run Sync Now** with **Audit folders** checked. Expect, for the four test mailboxes, Removed plus `audit_missing` items ≈ 3,620 (the audit in step B drops list-10 rows whose contact is already gone before the step C cleanup sees them, so the split between the two is not predictable; nick@'s Charlotte row 17230 lands in `audit_missing`), plus any strays and missing rows fleet-wide, and `Reconcile:` log lines for every folder.
4. After: the query in step 1 returns no rows; `SELECT COUNT(*) FROM tunnel_mailbox_folders WHERE last_audited_at IS NULL` is small (inactive or unavailable mailboxes only); jp@'s Outlook no longer lists Charlotte Hedgepeth (AFH) and the doubled "email • email" entries are gone; the two counts converge on about 948 plus personal contacts.
5. Next 00:00 UTC scheduled run audits every folder (duration about five minutes longer); the following 12:00 run shows no `Reconcile:` lines and finishes in about two minutes.

## Out of scope

Mailboxes a tunnel no longer targets keep their folder and rows (same class of gap, separate change). Rows in inactive target mailboxes (about 44k) stay as they are. CiraSync-era Outlook categories are not the app's and are not touched.

## Process

Branch `sync-reliability/phase-5` from `main`; this spec commits first, then the plan, then code with TDD. PR to `github.com/nickafh/sync` or local merge (Nick's call), `./deploy.sh` on the box, verify per §5.5, then update the plan as shipped.
