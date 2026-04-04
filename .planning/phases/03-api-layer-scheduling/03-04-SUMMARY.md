---
phase: 03-api-layer-scheduling
plan: 04
subsystem: api
tags: [ef-core, entity-framework, migration, dto, tunnel, smtp, ddg]

# Dependency graph
requires:
  - phase: 03-02
    provides: TunnelsController CRUD, DTOs, and integration tests
  - phase: 03-03
    provides: DDGResolver with PrimarySmtpAddress in DdgInfo record
provides:
  - SourceSmtpAddress property on Tunnel entity
  - EF Core migration AddSourceSmtpAddress for source_smtp_address column
  - Updated DTOs and controller mappings for SMTP address round-trip
  - DDG-04 requirement fully satisfied
affects: [04-frontend-core, tunnel-detail-view, create-tunnel-wizard]

# Tech tracking
tech-stack:
  added: []
  patterns: [RFC 5321 MaxLength(320) for email address columns]

key-files:
  created:
    - api/Migrations/20260404054801_AddSourceSmtpAddress.cs
    - api/Migrations/20260404054801_AddSourceSmtpAddress.Designer.cs
  modified:
    - shared/Entities/Tunnel.cs
    - api/Data/Configurations/TunnelConfiguration.cs
    - api/DTOs/CreateTunnelRequest.cs
    - api/DTOs/UpdateTunnelRequest.cs
    - api/DTOs/TunnelDto.cs
    - api/DTOs/TunnelDetailDto.cs
    - api/Controllers/TunnelsController.cs
    - tests/AFHSync.Tests.Integration/Api/TunnelsControllerTests.cs
    - .planning/REQUIREMENTS.md

key-decisions:
  - "MaxLength(320) for SourceSmtpAddress per RFC 5321 maximum email address length"

patterns-established:
  - "RFC 5321 email column sizing: HasMaxLength(320) for SMTP address fields"

requirements-completed: [DDG-04]

# Metrics
duration: 7min
completed: 2026-04-04
---

# Phase 03 Plan 04: DDG-04 Gap Closure Summary

**SourceSmtpAddress added to Tunnel entity completing the DDG reference triad (name, address, filter) with EF Core migration and integration test coverage**

## Performance

- **Duration:** 7 min
- **Started:** 2026-04-04T05:46:12Z
- **Completed:** 2026-04-04T05:54:02Z
- **Tasks:** 2
- **Files modified:** 12

## Accomplishments
- Added SourceSmtpAddress nullable string property to Tunnel entity with RFC 5321 MaxLength(320) EF Core column mapping
- Updated all 4 DTOs (CreateTunnelRequest, UpdateTunnelRequest, TunnelDto, TunnelDetailDto) and TunnelsController to wire the field through CRUD
- Generated EF Core migration AddSourceSmtpAddress with seed data for all 6 office tunnels
- Updated integration test PostTunnel_StoresDdgReference_DDG04 to verify SMTP address round-trips
- Marked DDG-04 requirement as complete in REQUIREMENTS.md with traceability table updated

## Task Commits

Each task was committed atomically:

1. **Task 1: Add SourceSmtpAddress to entity, configuration, DTOs, controller, and seed data** - `c101b59` (feat)
2. **Task 2: Update integration tests and mark DDG-04 complete in REQUIREMENTS.md** - `5ae217c` (feat)

## Files Created/Modified
- `shared/Entities/Tunnel.cs` - Added SourceSmtpAddress nullable string property
- `api/Data/Configurations/TunnelConfiguration.cs` - Added source_smtp_address column mapping with MaxLength(320) and seed data
- `api/DTOs/CreateTunnelRequest.cs` - Added SourceSmtpAddress parameter
- `api/DTOs/UpdateTunnelRequest.cs` - Added SourceSmtpAddress parameter
- `api/DTOs/TunnelDto.cs` - Added SourceSmtpAddress parameter for list response
- `api/DTOs/TunnelDetailDto.cs` - Added SourceSmtpAddress parameter for detail response
- `api/Controllers/TunnelsController.cs` - Wired SourceSmtpAddress in Create, Update, GetAll, GetById
- `api/Migrations/20260404054801_AddSourceSmtpAddress.cs` - EF Core migration adding column and updating seed data
- `api/Migrations/20260404054801_AddSourceSmtpAddress.Designer.cs` - EF Core migration designer
- `api/Migrations/AFHSyncDbContextModelSnapshot.cs` - Updated model snapshot
- `tests/AFHSync.Tests.Integration/Api/TunnelsControllerTests.cs` - Updated DDG-04 test with SMTP address assertion
- `.planning/REQUIREMENTS.md` - DDG-04 marked [x] Complete

## Decisions Made
- Used MaxLength(320) for SourceSmtpAddress per RFC 5321 maximum email address length (64 local + 1 @ + 255 domain)

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- All Phase 3 plans complete (4/4)
- DDG reference triad (name, address, filter) fully stored on tunnels
- Phase 4 frontend can display SourceSmtpAddress in tunnel detail views
- All 132 tests pass (32 integration + 100 unit, 1 pre-existing skip)

---
*Phase: 03-api-layer-scheduling*
*Completed: 2026-04-04*
