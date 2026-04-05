---
phase: 06-photo-sync
plan: 02
subsystem: api, ui, database
tags: [ef-core, migration, photo-sync, react, switch-toggle, cron, settings]

# Dependency graph
requires:
  - phase: 06-01
    provides: PhotoSyncService engine, IPhotoSyncService interface, SyncRun.PhotosFailed entity field
provides:
  - Tunnel.PhotoSyncEnabled EF column with migration
  - API DTOs exposing PhotoSyncEnabled on tunnels and PhotosFailed on sync runs
  - TunnelsController CRUD mapping for PhotoSyncEnabled
  - SyncRunsController PhotosFailed in run list and detail with per-tunnel breakdown
  - Frontend run detail Photo Sync card with per-tunnel photo stats
  - Frontend tunnel detail photo sync toggle switch
  - Frontend settings page photo cron and auto-trigger for separate_pass mode
affects: [photo-sync, admin-frontend, sync-engine]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Photo sync toggle pattern: boolean column with default true, toggle in detail page, sent in update request"
    - "Conditional settings UI: separate_pass mode reveals photo cron + auto-trigger fields"

key-files:
  created:
    - api/Migrations/20260405173915_AddPhotoSyncEnabled.cs
    - api/Migrations/20260405173915_AddPhotoSyncEnabled.Designer.cs
  modified:
    - api/Data/Configurations/TunnelConfiguration.cs
    - api/DTOs/TunnelDetailDto.cs
    - api/DTOs/TunnelDto.cs
    - api/DTOs/CreateTunnelRequest.cs
    - api/DTOs/UpdateTunnelRequest.cs
    - api/DTOs/SyncRunDetailDto.cs
    - api/DTOs/SyncRunDto.cs
    - api/DTOs/TunnelRunSummaryDto.cs
    - api/Controllers/TunnelsController.cs
    - api/Controllers/SyncRunsController.cs
    - frontend/src/types/tunnel.ts
    - frontend/src/types/sync-run.ts
    - frontend/src/app/(app)/runs/[id]/page.tsx
    - frontend/src/app/(app)/tunnels/[id]/page.tsx
    - frontend/src/app/(app)/settings/page.tsx

key-decisions:
  - "PhotoSyncEnabled defaults to true for all tunnels -- opt-out rather than opt-in"
  - "Photo Sync toggle visible in both view and edit mode (disabled when not editing) for discoverability"
  - "Per-tunnel photo breakdown shown conditionally only when photo activity exists"
  - "Photo cron and auto-trigger settings only shown when mode is separate_pass"

patterns-established:
  - "Conditional settings sections: show/hide based on mode selection"
  - "Photo sync per-tunnel stats: filter tunnel summaries to only those with photo activity"

requirements-completed: [PHOT-03, PHOT-04]

# Metrics
duration: 10min
completed: 2026-04-05
---

# Phase 6 Plan 2: Photo Sync UI & API Extensions Summary

**Tunnel.PhotoSyncEnabled column with EF migration, API DTO extensions for photo stats, and frontend photo sync UI (run detail card, tunnel toggle, settings cron/auto-trigger)**

## Performance

- **Duration:** 10 min
- **Started:** 2026-04-05T17:37:19Z
- **Completed:** 2026-04-05T17:47:00Z
- **Tasks:** 2
- **Files modified:** 18

## Accomplishments
- Added Tunnel.PhotoSyncEnabled boolean column with EF Core migration and seed data for all 6 tunnels
- Extended all API DTOs (TunnelDto, TunnelDetailDto, SyncRunDto, SyncRunDetailDto, TunnelRunSummaryDto) with photo sync fields
- Updated TunnelsController CRUD operations to map PhotoSyncEnabled and SyncRunsController to expose PhotosFailed with per-tunnel photo_failed counts
- Added Photo Sync card to run detail page with per-tunnel photo breakdown (D-10)
- Added photo sync toggle switch to tunnel detail page with full edit form integration (D-13)
- Extended settings page with photo cron and auto-trigger controls for separate_pass mode (D-02)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add Tunnel.PhotoSyncEnabled entity, EF config, migration, and API extensions** - `da2fe67` (feat)
2. **Task 2: Frontend photo sync UI -- run detail section, tunnel detail toggle, settings extensions** - `98d4c53` (feat)

## Files Created/Modified
- `api/Migrations/20260405173915_AddPhotoSyncEnabled.cs` - EF migration adding photo_sync_enabled column
- `api/Migrations/20260405173915_AddPhotoSyncEnabled.Designer.cs` - Migration designer file
- `api/Data/Configurations/TunnelConfiguration.cs` - Added PhotoSyncEnabled column config and seed data
- `api/DTOs/TunnelDetailDto.cs` - Added PhotoSyncEnabled parameter
- `api/DTOs/TunnelDto.cs` - Added PhotoSyncEnabled parameter
- `api/DTOs/CreateTunnelRequest.cs` - Added PhotoSyncEnabled with default true
- `api/DTOs/UpdateTunnelRequest.cs` - Added PhotoSyncEnabled with default true
- `api/DTOs/SyncRunDetailDto.cs` - Added PhotosFailed parameter
- `api/DTOs/SyncRunDto.cs` - Added PhotosFailed parameter
- `api/DTOs/TunnelRunSummaryDto.cs` - Added PhotosFailed parameter
- `api/Controllers/TunnelsController.cs` - Map PhotoSyncEnabled in Create, Update, GetAll, GetById
- `api/Controllers/SyncRunsController.cs` - Map PhotosFailed in GetRuns, GetRun with per-tunnel photo_failed counts
- `frontend/src/types/tunnel.ts` - Added photoSyncEnabled to DTO and request interfaces
- `frontend/src/types/sync-run.ts` - Added photosFailed to SyncRunDto, SyncRunDetailDto, TunnelRunSummaryDto
- `frontend/src/app/(app)/runs/[id]/page.tsx` - Photo Sync card, Photos Failed KPI, photo action tabs
- `frontend/src/app/(app)/tunnels/[id]/page.tsx` - Photo sync toggle switch card with edit form wiring
- `frontend/src/app/(app)/settings/page.tsx` - Photo cron input and auto-trigger switch for separate_pass mode

## Decisions Made
- PhotoSyncEnabled defaults to true (opt-out) -- matches expectation that photo sync is enabled by default
- Photo Sync toggle is visible in both view and edit mode but disabled when not editing, improving discoverability
- Per-tunnel photo breakdown only renders when at least one tunnel has photo activity, avoiding empty sections
- Photo cron and auto-trigger fields only appear when photo_sync_mode is "separate_pass", keeping the UI clean

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Added PhotosFailed to TunnelRunSummaryDto**
- **Found during:** Task 1 (API DTO extensions)
- **Issue:** Plan specified PhotosFailed on SyncRunDto and SyncRunDetailDto but did not explicitly mention TunnelRunSummaryDto, which is needed for per-tunnel photo breakdown in the frontend
- **Fix:** Added PhotosFailed parameter to TunnelRunSummaryDto and computed photo_failed count in SyncRunsController GetRun per-tunnel summary query
- **Files modified:** api/DTOs/TunnelRunSummaryDto.cs, api/Controllers/SyncRunsController.cs
- **Verification:** dotnet build succeeds, frontend references ts.photosFailed correctly
- **Committed in:** da2fe67 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 missing critical)
**Impact on plan:** Essential for per-tunnel photo failure display in run detail. No scope creep.

## Issues Encountered
- Next.js Turbopack build failed in worktree due to workspace root resolution -- node_modules not present in worktree. Resolved by running npm install in the worktree frontend directory. This is a worktree-specific issue, not a code problem.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Photo sync UI and API extensions are complete
- All photo sync configuration (per-tunnel toggle, cron schedule, auto-trigger) is accessible from the admin UI
- Photo sync stats (updated, failed) are visible in run detail with per-tunnel breakdown
- Ready for end-to-end testing and production deployment

## Self-Check: PASSED

- All 19 files verified present
- Both task commits (da2fe67, 98d4c53) verified in git log
- dotnet build succeeded for api, worker, shared (0 errors)
- Next.js build succeeded (11 static pages, 0 errors)

---
*Phase: 06-photo-sync*
*Completed: 2026-04-05*
