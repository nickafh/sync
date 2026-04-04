---
phase: 03-api-layer-scheduling
plan: 01
subsystem: scheduling
tags: [hangfire, sync-trigger, settings, api]
dependency_graph:
  requires: [02-03, 02-04]
  provides: [hangfire-infrastructure, sync-trigger-api, settings-api, recurring-job]
  affects: [worker/Program.cs, api/Program.cs, shared/Services/ISyncEngine.cs]
tech_stack:
  added: [Hangfire.AspNetCore 1.8.23, Hangfire.PostgreSql 1.21.1]
  patterns: [fire-and-forget job enqueue, concurrent run prevention via status check, cron reschedule on settings update]
key_files:
  created:
    - api/Controllers/SyncRunsController.cs
    - api/Controllers/SettingsController.cs
    - api/DTOs/TriggerSyncRequest.cs
    - api/DTOs/SyncRunDto.cs
    - api/DTOs/SyncRunDetailDto.cs
    - api/DTOs/SyncRunItemDto.cs
    - api/DTOs/SettingsDto.cs
    - api/DTOs/SettingsUpdateRequest.cs
    - api/Filters/HangfireDashboardAuthFilter.cs
    - shared/Services/ISyncEngine.cs
    - tests/AFHSync.Tests.Integration/Api/SyncRunsControllerTests.cs
    - tests/AFHSync.Tests.Integration/Api/SettingsControllerTests.cs
  modified:
    - api/AFHSync.Api.csproj
    - api/Program.cs
    - worker/AFHSync.Worker.csproj
    - worker/Program.cs
    - worker/Services/SyncEngine.cs
    - tests/AFHSync.Tests.Integration/TestWebApplicationFactory.cs
    - tests/AFHSync.Tests.Integration/AFHSync.Tests.Integration.csproj
decisions:
  - ISyncEngine moved from worker to shared project to avoid circular API-Worker dependency for Hangfire enqueue
  - NoOp Hangfire stubs (NoOpBackgroundJobClient, NoOpRecurringJobManager) in TestWebApplicationFactory for integration tests
  - Hangfire dashboard protected by HangfireDashboardAuthFilter checking JWT IsAuthenticated
metrics:
  duration: 12min
  completed: 2026-04-04
---

# Phase 03 Plan 01: Hangfire Scheduling & Sync Trigger API Summary

Hangfire infrastructure wired across API (client + dashboard) and worker (server + recurring job), with SyncRunsController for fire-and-forget sync triggers with 409 concurrent guard, and SettingsController with dynamic cron reschedule on sync_schedule_cron update.

## What Was Built

### Task 1: Hangfire Infrastructure (API + Worker)

Installed Hangfire.AspNetCore 1.8.23 and Hangfire.PostgreSql 1.21.1 in both API and Worker projects. API is client-only (enqueues jobs, hosts dashboard at /hangfire) while Worker runs the Hangfire server with 2 workers on sync/default queues.

**API Program.cs:** Added `AddHangfire()` with PostgreSQL storage and `UseHangfireDashboard("/hangfire")` with JWT auth filter. No `AddHangfireServer()` in API per anti-pattern guidance.

**Worker Program.cs:** Added `AddHangfire()` + `AddHangfireServer()`. At startup, reads `sync_schedule_cron` from app_settings and registers `"sync-all"` recurring job via `IRecurringJobManager.AddOrUpdate<ISyncEngine>()`.

**HangfireDashboardAuthFilter:** Checks `httpContext.User.Identity?.IsAuthenticated == true` to protect the dashboard with JWT auth.

### Task 2: SyncRunsController

Four endpoints implementing the sync trigger and polling API:

| Endpoint | Purpose |
|----------|---------|
| `POST /api/sync-runs` | Create pending run, enqueue Hangfire job, return runId. 409 if run already in progress. |
| `GET /api/sync-runs` | Paginated run history ordered by StartedAt descending |
| `GET /api/sync-runs/{id}` | Run detail with full aggregate counts, or 404 |
| `GET /api/sync-runs/{id}/items` | Per-item log with optional action filter and pagination |

DTOs: TriggerSyncRequest, SyncRunDto (list), SyncRunDetailDto (detail), SyncRunItemDto (items). Enum values serialized as PostgreSQL snake_case names via existing EnumHelpers.ToPgName().

### Task 3: SettingsController

Two endpoints for application settings management:

| Endpoint | Purpose |
|----------|---------|
| `GET /api/settings` | All settings ordered by key |
| `PUT /api/settings` | Batch update with 404 for unknown keys; reschedules Hangfire "sync-all" job when sync_schedule_cron changes |

DTOs: SettingsDto, SettingsUpdateRequest with SettingEntry[] array.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Moved ISyncEngine to shared project**
- **Found during:** Task 2
- **Issue:** API project cannot reference Worker project (circular dependency: Worker already references API for DbContext). SyncRunsController needs ISyncEngine for Hangfire `Enqueue<ISyncEngine>()`.
- **Fix:** Moved ISyncEngine interface from `worker/Services/ISyncEngine.cs` to `shared/Services/ISyncEngine.cs`. Updated all imports in Worker (SyncEngine.cs, Program.cs) to use `AFHSync.Shared.Services.ISyncEngine`.
- **Files modified:** shared/Services/ISyncEngine.cs (created), worker/Services/ISyncEngine.cs (deleted), worker/Services/SyncEngine.cs, worker/Program.cs
- **Commit:** 5e30c4a

**2. [Rule 3 - Blocking] Added Hangfire stubs to TestWebApplicationFactory**
- **Found during:** Task 2
- **Issue:** Hangfire's `AddHangfire()` in API Program.cs registers services that try to connect to PostgreSQL. Integration tests use InMemory DB, causing failures.
- **Fix:** Added NoOpBackgroundJobClient and NoOpRecurringJobManager stubs. TestWebApplicationFactory removes real Hangfire service registrations and adds stubs. Also added Hangfire.Core package to test project.
- **Files modified:** tests/AFHSync.Tests.Integration/TestWebApplicationFactory.cs, tests/AFHSync.Tests.Integration/AFHSync.Tests.Integration.csproj
- **Commit:** 5e30c4a

## Verification Results

- Both API and Worker projects build with 0 errors
- 8 integration tests pass (4 SyncRuns + 3 Settings + 1 SeedData)
- 81 unit tests pass (ISyncEngine move did not break existing tests)
- Hangfire dashboard wired at /hangfire with JWT auth
- Recurring sync-all job registered from app_settings at worker startup
- Concurrent sync run prevention via SyncStatus.Running check returns 409
- Settings update reschedules Hangfire cron dynamically

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | a5d6c22 | Hangfire infrastructure in API + Worker |
| 2 | 5e30c4a | SyncRunsController with trigger, polling, concurrent guard |
| 3 | 226da37 | SettingsController with cron reschedule |
