---
phase: 04-admin-frontend
plan: 06
subsystem: frontend, api
tags: [gap-closure, ddg-filter, run-detail, per-tunnel-summary]
dependency_graph:
  requires: [04-03, 04-04]
  provides: [ddg-06-filter-display, rlog-02-tunnel-summaries, rlog-03-failure-display]
  affects: [tunnel-detail-page, run-detail-page, tunnels-api, sync-runs-api]
tech_stack:
  added: []
  patterns: [per-tunnel-groupby-aggregation, display-only-filter-field]
key_files:
  created:
    - api/DTOs/TunnelRunSummaryDto.cs
    - api/Migrations/20260404155410_AddSourceFilterPlain.cs
    - api/Migrations/20260404155410_AddSourceFilterPlain.Designer.cs
  modified:
    - shared/Entities/Tunnel.cs
    - api/DTOs/TunnelDetailDto.cs
    - api/DTOs/CreateTunnelRequest.cs
    - api/DTOs/UpdateTunnelRequest.cs
    - api/DTOs/SyncRunDetailDto.cs
    - api/Controllers/TunnelsController.cs
    - api/Controllers/SyncRunsController.cs
    - api/Data/Configurations/TunnelConfiguration.cs
    - api/Migrations/AFHSyncDbContextModelSnapshot.cs
    - frontend/src/types/tunnel.ts
    - frontend/src/types/sync-run.ts
    - frontend/src/app/(app)/tunnels/[id]/page.tsx
    - frontend/src/app/(app)/runs/[id]/page.tsx
decisions:
  - SourceFilterPlain stored as nullable text column (max 500 chars) -- display-only field populated from DDG picker
  - Per-tunnel summaries computed server-side via GroupBy on SyncRunItems -- avoids client-side aggregation
  - Error list in per-tunnel breakdown capped at 5 per tunnel with overflow indicator
metrics:
  duration: 7min
  completed: 2026-04-04
  tasks: 2
  files: 16
---

# Phase 04 Plan 06: Verification Gap Closure Summary

Close DDG-06 plain-language filter and RLOG-02/RLOG-03 per-tunnel run summaries -- adding SourceFilterPlain to the Tunnel entity with EF Core migration, and computing per-tunnel action breakdowns from SyncRunItems GroupBy aggregation.

## Completed Tasks

| # | Task | Commit | Key Changes |
|---|------|--------|-------------|
| 1 | Add plain-language filter to Tunnel entity, API DTO, and frontend display (DDG-06) | 373750e | Added SourceFilterPlain property, EF migration, DTO updates, frontend filter description display |
| 2 | Add per-tunnel summaries to run detail API and frontend (RLOG-02, RLOG-03) | 9075b5e | Created TunnelRunSummaryDto, GroupBy aggregation in controller, Per-Tunnel Breakdown card |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added TunnelConfiguration column mapping for SourceFilterPlain**
- **Found during:** Task 1
- **Issue:** The plan did not mention updating `api/Data/Configurations/TunnelConfiguration.cs`, but without the column mapping EF Core would not properly scaffold the migration with the correct snake_case column name.
- **Fix:** Added `builder.Property(e => e.SourceFilterPlain).HasColumnName("source_filter_plain").HasMaxLength(500);` to TunnelConfiguration.
- **Files modified:** api/Data/Configurations/TunnelConfiguration.cs
- **Commit:** 373750e

## What Was Built

### Task 1: DDG-06 Plain-Language Filter

**Backend:**
- Added `SourceFilterPlain` nullable string property to `Tunnel` entity
- Created EF Core migration `20260404155410_AddSourceFilterPlain` adding `source_filter_plain` column to tunnels table
- Updated `TunnelDetailDto`, `CreateTunnelRequest`, `UpdateTunnelRequest` to include the field
- Injected `IFilterConverter` into `TunnelsController` for future filter conversion use
- `GetById`, `Create`, and `Update` methods now handle `SourceFilterPlain`

**Frontend:**
- Added `sourceFilterPlain` to `TunnelDetailDto` and `UpdateTunnelRequest` TypeScript types
- Tunnel detail page displays "Filter Description" label with plain-language text in both view and edit modes
- DDG picker selection now captures `recipientFilterPlain` and stores it in the edit form
- All state management functions (useEffect, enterEditMode, discardChanges) include the new field

### Task 2: RLOG-02/RLOG-03 Per-Tunnel Run Summaries

**Backend:**
- Created `TunnelRunSummaryDto` record with per-tunnel action counts (created, updated, removed, skipped, failed, photos) and error messages array
- Added `TunnelSummaries` array to `SyncRunDetailDto`
- `SyncRunsController.GetRun` now queries `SyncRunItems`, groups by `TunnelId`, resolves tunnel names from the Tunnels table, and returns per-tunnel summaries

**Frontend:**
- Added `TunnelRunSummaryDto` interface and `tunnelSummaries` field to `SyncRunDetailDto`
- Run detail page renders a "Per-Tunnel Breakdown" card between the Summary Card and Action Filter Tabs
- Each tunnel row shows a 6-column grid of action counts, red failure indicator badge, and up to 5 error messages with overflow count

## Verification Results

- `dotnet build` in api/: 0 warnings, 0 errors
- `tsc --noEmit` in frontend/: 0 errors
- EF Core migration `AddSourceFilterPlain` exists and adds correct column
- Tunnel entity has `SourceFilterPlain` property
- TunnelDetailDto includes `SourceFilterPlain` in response
- Tunnel detail page renders "Filter Description" with plain-language text
- SyncRunDetailDto includes `TunnelSummaries` array
- Run detail page renders "Per-Tunnel Breakdown" section with per-tunnel counts

## Known Stubs

None -- all data flows are fully wired. SourceFilterPlain values will be null for existing tunnels until a DDG is re-selected via the picker (which provides the recipientFilterPlain value).

## Self-Check: PASSED

All 9 key files verified on disk. Both commits (373750e, 9075b5e) found in git log.
