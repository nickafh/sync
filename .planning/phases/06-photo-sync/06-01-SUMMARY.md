---
phase: 06-photo-sync
plan: 01
subsystem: sync-engine
tags: [graph-api, photo-sync, sha256, hangfire, semaphore, tdd]

# Dependency graph
requires:
  - phase: 02-sync-engine-core
    provides: "SyncEngine orchestrator, GraphClientFactory, GraphResilienceHandler, RunLogger, ThrottleCounter"
provides:
  - "IPhotoSyncService interface and PhotoSyncService implementation"
  - "Photo sync trailing pass in SyncEngine (included mode)"
  - "Hangfire photo-sync-all recurring job (separate_pass mode)"
  - "App settings: photo_sync_cron, photo_sync_auto_trigger"
  - "Tunnel.PhotoSyncEnabled property for per-tunnel opt-out"
affects: [06-photo-sync, api-layer, frontend-settings]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Protected virtual methods for Graph SDK testability (FetchUserPhotoAsync, WriteContactPhotoAsync)"
    - "Fetch-once write-many pattern for photo distribution across mailboxes"
    - "ReadAppSettingAsync helper for runtime config from app_settings table"

key-files:
  created:
    - worker/Services/IPhotoSyncService.cs
    - worker/Services/PhotoSyncService.cs
    - tests/AFHSync.Tests.Unit/Sync/PhotoSyncServiceTests.cs
  modified:
    - worker/Services/SyncEngine.cs
    - worker/Program.cs
    - api/Data/Configurations/AppSettingConfiguration.cs
    - shared/Entities/Tunnel.cs
    - tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs

key-decisions:
  - "Protected virtual methods for Graph SDK photo operations (FetchUserPhotoAsync, WriteContactPhotoAsync) enable test subclassing without complex Graph SDK mock chains"
  - "SemaphoreSlim(2) for photo concurrency, lower than contact sync's configurable parallelism (default 4)"
  - "Photo removal logged as photo_removal_skipped since Graph has no DELETE endpoint for contact photos"
  - "Photo size validated < 4MB before PUT (T-06-02 threat mitigation)"

patterns-established:
  - "TestablePhotoSyncService subclass pattern: override protected virtual Graph methods for unit testing"
  - "ReadAppSettingAsync pattern in SyncEngine for reading runtime config from app_settings"
  - "LoadSourceUsersForTunnelAsync pattern for joining SourceUsers via ContactSyncStates"

requirements-completed: [PHOT-01, PHOT-02, PHOT-03, PHOT-04]

# Metrics
duration: 21min
completed: 2026-04-05
---

# Phase 06 Plan 01: Photo Sync Engine Summary

**PhotoSyncService with fetch-once write-many photo distribution, SHA-256 delta comparison, SemaphoreSlim(2) concurrency, batched backfill, and three-mode integration (included/separate_pass/disabled)**

## Performance

- **Duration:** 21 min
- **Started:** 2026-04-05T17:10:50Z
- **Completed:** 2026-04-05T17:32:48Z
- **Tasks:** 2
- **Files modified:** 8

## Accomplishments
- Built PhotoSyncService with fetch-once write-many pattern: each source user photo fetched once from Graph, hashed with SHA-256, then distributed to all target contacts where hash differs
- Integrated photo sync into SyncEngine as a trailing pass (included mode) that runs after all contact creates/updates per tunnel, with auto-trigger option for separate_pass mode
- Registered Hangfire photo-sync-all recurring job that dynamically registers/removes based on photo_sync_mode setting
- Added 14 unit tests (11 PhotoSyncService + 3 SyncEngine) covering hash computation, skip-on-match, write-on-diff, dry-run, failure handling, and mode-based integration

## Task Commits

Each task was committed atomically:

1. **Task 1: Create PhotoSyncService with tests (TDD)** - `65b3588` (feat)
2. **Task 2: Integrate PhotoSyncService into SyncEngine and worker Program.cs** - `4205eb8` (feat)

_Note: Task 1 used TDD flow - tests written first (RED confirmed), then implementation (GREEN)_

## Files Created/Modified
- `worker/Services/IPhotoSyncService.cs` - Photo sync service interface with SyncPhotosForTunnelAsync and RunAllAsync
- `worker/Services/PhotoSyncService.cs` - Full implementation: fetch-once write-many, SHA-256 delta, SemaphoreSlim(2), batched backfill, dry-run, failure handling
- `worker/Services/SyncEngine.cs` - Added IPhotoSyncService injection, photo trailing pass (included mode), auto-trigger (separate_pass), ReadAppSettingAsync helper
- `worker/Program.cs` - DI registration for IPhotoSyncService, Hangfire photo-sync-all job registration/removal
- `shared/Entities/Tunnel.cs` - Added PhotoSyncEnabled property (default true) for per-tunnel opt-out
- `api/Data/Configurations/AppSettingConfiguration.cs` - Seeded photo_sync_cron (Id=10) and photo_sync_auto_trigger (Id=11)
- `tests/AFHSync.Tests.Unit/Sync/PhotoSyncServiceTests.cs` - 11 tests covering all photo sync behaviors
- `tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs` - 3 new tests for photo mode integration (included, disabled, separate_pass+auto_trigger)

## Decisions Made
- Used protected virtual methods for Graph SDK calls (FetchUserPhotoAsync, WriteContactPhotoAsync) following ContactFolderManager pattern -- enables test subclassing without NSubstitute/Moq for Graph SDK
- Photo removal logged as "photo_removal_skipped" per RESEARCH.md finding that Graph has no DELETE endpoint for contact photos -- safe approach for v1
- Photo size validation (< 4MB) added as T-06-02 threat mitigation even though the plan didn't explicitly specify it in the action steps
- Added Tunnel.PhotoSyncEnabled property in Task 1 (plan said Plan 02, but both tasks reference it and code wouldn't compile without it)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added Tunnel.PhotoSyncEnabled property early**
- **Found during:** Task 1 (PhotoSyncService implementation)
- **Issue:** Plan frontmatter says "PhotoSyncEnabled will be added in Plan 02" but both Task 1 (RunAllAsync) and Task 2 (SyncEngine) reference tunnel.PhotoSyncEnabled -- code would not compile without it
- **Fix:** Added `public bool PhotoSyncEnabled { get; set; } = true;` to Tunnel entity in Task 1
- **Files modified:** shared/Entities/Tunnel.cs
- **Verification:** Build succeeds, all tests pass
- **Committed in:** 65b3588 (Task 1 commit)

**2. [Rule 2 - Missing Critical] Added photo size validation (T-06-02)**
- **Found during:** Task 1 (PhotoSyncService implementation)
- **Issue:** Threat model T-06-02 requires "Validate photo byte length < 4MB before PUT" but the plan action steps didn't include this check
- **Fix:** Added MaxPhotoSizeBytes constant (4MB) and size check in SyncPhotosForTunnelAsync before PUT
- **Files modified:** worker/Services/PhotoSyncService.cs
- **Verification:** Oversized photos logged as warning and skipped
- **Committed in:** 65b3588 (Task 1 commit)

---

**Total deviations:** 2 auto-fixed (1 blocking, 1 missing critical)
**Impact on plan:** Both auto-fixes necessary for correctness. PhotoSyncEnabled was needed for compilation; photo size validation was a threat model requirement. No scope creep.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- PhotoSyncService engine complete and tested, ready for Plan 02 (API/DTO extensions, Tunnel.PhotoSyncEnabled EF migration, frontend photo toggle/stats)
- Tunnel.PhotoSyncEnabled property already added (deviation from plan boundary) -- Plan 02 needs to add the EF migration and TunnelConfiguration column mapping
- photo_sync_cron and photo_sync_auto_trigger app_settings seeded -- Plan 02 frontend settings page can expose these

## Self-Check: PASSED

- All created files verified present on disk
- Both commit hashes (65b3588, 4205eb8) verified in git log
- All acceptance criteria patterns verified in source code
- 115 unit tests pass (114 passed, 1 skipped), 0 failures

---
*Phase: 06-photo-sync*
*Completed: 2026-04-05*
