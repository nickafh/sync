# History

## 2026-04-17 — Sync reliability pass

Incident context: a full sync during business hours produced 975–1,725 HTTP 429/504 failures, the dashboard "Tunnels" counter was stuck on 0 even though tunnels were completing, and Stop Sync wouldn't cleanly cancel. Several related issues surfaced during investigation and were fixed in the same session.

### 1. Dashboard "Tunnels: 0" during active sync

**Symptom:** Tunnels completed (visible in the Tunnels list via `Last Run` timestamps and live-growing `Users` counts), but the dashboard's live progress card showed `Tunnels: 0` for the entire run.

**Root cause:** `SyncEngine.ProcessTunnelAsync` writes interim per-mailbox progress via `UpdateRunProgressAsync(..., 0, 0, 0, ...)` — hardcoding the tunnel counters to zero. This overwrote the accumulated `tunnelsProcessed` that the outer loop had just saved after each tunnel completed.

**Fix:** Threaded `priorTunnelsProcessed`, `priorTunnelsWarned`, `priorTunnelsFailed` through `ProcessTunnelAsync` and used them in the interim update so they're preserved across mailbox writes.

Files: `worker/Services/SyncEngine.cs`

### 2. Graph retry policy too narrow and too short

**Symptom:** Of 1,483 failures in run #166 and 1,725 in #164, error distribution was:
- HTTP 429: 1,340 (90%)
- HTTP 504: 142
- HTTP 503: 1

Plus `ThrottleEvents` stayed at 0 or 1 despite obvious sustained throttling.

**Root causes:**
- `GraphResilienceHandler` only retried `429` and `503`. `504 Gateway Timeout` and `502 Bad Gateway` were not retried at all — they failed on first hit.
- `MaxRetryAttempts = 5` with `Delay = 2s` gave only ~62 seconds of total backoff window (2s→4s→8s→16s→32s). Sustained Graph throttling windows longer than ~1 min exhausted retries immediately.
- `ThrottleCounter` only increments when Polly retries at the *raw HTTP* layer. Graph's `$batch` endpoint returns HTTP 200 with per-step 429s embedded in the body, so Polly never sees them and the counter never fires. (Note: this counter-visibility gap still exists — fixing it requires per-step retry logic in `ContactWriter.ExecuteBatchWithRetryAsync`, deferred.)

**Fix:**
- Added `GatewayTimeout` and `BadGateway` to `ShouldHandle`.
- Bumped `MaxRetryAttempts` 5 → 10.
- Bumped base `Delay` 2s → 5s (total backoff window now ~15 minutes, capped by `MaxDelay = 5 min`).
- Made `MaxRetryAttempts` and `Delay` injectable so tests can use 1ms delays (avoids 30-min test runs).

Files: `worker/Graph/GraphResilienceHandler.cs`, `tests/AFHSync.Tests.Unit/Sync/GraphResilienceHandlerTests.cs`

### 3. Auto-triggered photo sync running concurrent with contact sync

**Symptom:** Turning on `photo_sync_auto_trigger` showed photo sync and contact sync both as `Running` simultaneously in the dashboard.

**Root cause:** The auto-trigger block (`SyncEngine.RunAsync` Step 5b) called `photoSyncService.RunAllAsync(...)` *inside* the contact sync's `try` block — after all tunnels processed but *before* `FinalizeRunAsync` stamped the contact run's status as Success/Warning/Failed. Both `SyncRun` rows were in `Running` state at the same time in the DB.

**Fix:** Moved the auto-trigger call to Step 9, *after* `FinalizeRunAsync` completes. The contact run is committed as terminal before the photo sync creates its own run. Also dropped `skipRunningCheck: true` (no longer needed — contact run is done by the time the check runs) and added a skip when the contact sync was cancelled.

Files: `worker/Services/SyncEngine.cs`

### 4. Photo sync showed all zeros in live progress

**Symptom:** Photo sync would show as `Running` for minutes but `Photos: 0`, `Tunnels: 0` on the dashboard.

**Root cause:** `PhotoSyncService.RunAllAsync` accumulated `totalPhotosUpdated` / `totalPhotosFailed` in local variables and only wrote them via `FinalizeRunAsync` at the very end. No interim progress writes.

**Fix:** Added `PhotoSyncService.UpdateRunProgressAsync` helper (mirrors `SyncEngine`'s pattern) and called it after each tunnel's photo pass. Dashboard now shows live `Photos`, `Photos Failed`, `Tunnels` counters during photo sync.

Files: `worker/Services/PhotoSyncService.cs`

### 5. Stop Sync was basically advisory

**Symptom:** Clicking Stop Sync wouldn't actually kill the running sync. The button set a flag but the dashboard stayed "Syncing…" forever if the worker was stuck in a Graph retry loop. Also, a prior killed-mid-execution sync left `cancel_sync=true` which caused new syncs to self-cancel on the first between-tunnel check.

**Root causes + fixes:**

- **Stale `cancel_sync` flag:** `SyncEngine.RunAsync` and `PhotoSyncService.RunAllAsync` now force `cancel_sync=false` at the start of every run before entering the processing loop.
- **API didn't unblock UI:** `POST /api/sync-runs/stop` previously just set the flag. Now it *also* force-updates every `Running` SyncRun to `Cancelled` with `completed_at=NOW()` so the dashboard's "Sync in progress" bar clears immediately.
- **Cancelled runs were mis-labeled:** `SyncEngine` used `DetermineStatus(...)` which returns Success/Warning regardless of cancel. Added a `wasCancelled` bool flag, set when the between-tunnel cancel check fires, and used in final status to return `SyncStatus.Cancelled` instead.
- **Worker could overwrite the API's Cancelled stamp:** `RunLogger.FinalizeRunAsync` now checks whether the run is already `Cancelled` in the DB and, if so, skips the overwrite instead of stamping Success/Warning on top.
- **PhotoSyncService didn't honour cancel:** Added between-tunnel cancel check that matches `SyncEngine`'s behavior.

Caveat: if the worker is stuck inside a long Graph retry loop, the thread can't check `cancel_sync` until the current HTTP call returns. Hard cancellation via `CancellationToken` propagation is deferred. The API-side force-update keeps the UI unblocked in the meantime.

Files: `worker/Services/SyncEngine.cs`, `worker/Services/PhotoSyncService.cs`, `worker/Services/RunLogger.cs`, `api/Controllers/SyncRunsController.cs`

### 6. `photo-sync-all` cron colliding with `sync-all`

**Symptom:** Hangfire's Recurring Jobs page showed `sync-all` and `photo-sync-all` both on `0 0 * * *` (same midnight UTC). Also `photo-sync-all` and `stale-run-cleanup` showed `Could not resolve assembly 'AFHSync.Worker'` — stale serialized job definitions from a past rename/restructure.

**Root cause:** Registration in `worker/Program.cs` always registered `photo-sync-all` whenever `photo_sync_mode == "separate_pass"`, regardless of whether `photo_sync_auto_trigger` was on. With auto-trigger on, the contact sync already chains photo sync post-finalization, so the standalone cron was duplicative and racing.

**Fix (Option A):**
- `worker/Program.cs` now skips the `photo-sync-all` registration (and removes any existing one) when `photo_sync_auto_trigger == "true"`. Auto-trigger owns scheduling; no standalone cron needed.
- `api/Controllers/SettingsController.cs` now tracks photo-sync-related setting changes and calls `RemoveIfExists("photo-sync-all")` on-the-fly when the user toggles auto-trigger on or switches mode. Re-registration when auto-trigger goes *off* still requires a worker restart (via `./deploy.sh`) because the API project deliberately doesn't reference `IPhotoSyncService` to avoid pulling the worker assembly into the API container.

Manual cleanup step (one-time, via Hangfire dashboard):
- Delete the stale `stale-run-cleanup` and `photo-sync-all` job definitions so `Program.cs` re-registers them cleanly on next worker start.

Files: `worker/Program.cs`, `api/Controllers/SettingsController.cs`

### Known follow-ups (deferred)

- **Batch-step retry in `ContactWriter.ExecuteBatchWithRetryAsync`:** The method name and comment imply it retries failed batch steps, but the implementation just records failures on first attempt. Graph `$batch` returns HTTP 200 with embedded per-step 429s that bypass Polly entirely. A proper fix would collect failed step IDs, honour step-level `Retry-After`, resubmit a smaller batch, and invoke the throttle callback so `ThrottleEvents` reflects reality. ~40–60 lines in `ContactWriter.cs`.
- **Hard cancellation during Graph retry loops:** Plumb `CancellationToken` through the resilience pipeline so Stop Sync can interrupt an in-flight retry wait rather than waiting for it to complete.
