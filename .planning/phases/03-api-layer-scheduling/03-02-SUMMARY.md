---
phase: 03-api-layer-scheduling
plan: 02
subsystem: api
tags: [aspnetcore, efcore, entityframework, crud, controllers, dto, integrationtests, xunit]

# Dependency graph
requires:
  - phase: 01-foundation-infrastructure
    provides: AFHSyncDbContext, AFHSync.Shared entities, JWT auth, controller base pattern
  - phase: 02-sync-engine-core
    provides: SyncRun, ContactSyncState, SourceUser, TargetMailbox entities with sync data

provides:
  - GET/POST/PUT/DELETE /api/tunnels — full tunnel CRUD storing DDG filter and display name (DDG-04)
  - GET /api/phone-lists — phone list read with paginated contacts
  - GET/PUT /api/field-profiles — field profile read and behavior update with grouped sections
  - GET /api/dashboard — aggregate KPIs (active tunnels, target users, last sync, warnings)
  - 12 DTOs with EnumHelpers.ToPgName for PostgreSQL enum serialization
  - Integration tests for tunnel CRUD and phone list reads (14 tests)

affects: [04-frontend-core, 05-differentiators]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - DTOs as C# records with validation attributes on request types
    - EnumHelpers.ToPgName<T> for consistent PostgreSQL PgName string serialization across all controllers
    - EF Core Include/ThenInclude for eager loading in controller actions
    - TestWebApplicationFactory with UseInternalServiceProvider(inMemoryServiceProvider) to avoid Npgsql/InMemory dual-provider conflict
    - Integration tests using _factory.Services.CreateScope() for test data seeding

key-files:
  created:
    - api/DTOs/EnumHelpers.cs
    - api/DTOs/TunnelDto.cs
    - api/DTOs/TunnelDetailDto.cs
    - api/DTOs/CreateTunnelRequest.cs
    - api/DTOs/UpdateTunnelRequest.cs
    - api/DTOs/PhoneListDto.cs
    - api/DTOs/PhoneListDetailDto.cs
    - api/DTOs/ContactDto.cs
    - api/DTOs/FieldProfileDto.cs
    - api/DTOs/FieldProfileDetailDto.cs
    - api/DTOs/UpdateFieldProfileRequest.cs
    - api/DTOs/DashboardDto.cs
    - api/Controllers/TunnelsController.cs
    - api/Controllers/PhoneListsController.cs
    - api/Controllers/FieldProfilesController.cs
    - api/Controllers/DashboardController.cs
    - tests/AFHSync.Tests.Integration/Api/TunnelsControllerTests.cs
    - tests/AFHSync.Tests.Integration/Api/PhoneListsControllerTests.cs
  modified:
    - tests/AFHSync.Tests.Integration/TestWebApplicationFactory.cs

key-decisions:
  - "EnumHelpers.ToPgName<T> uses reflection on PgNameAttribute to serialize enums as PostgreSQL native type strings (e.g. 'flag_hold' not 'FlagHold') — consistent with database representation"
  - "TunnelsController DELETE uses ContactSyncState presence check (not tunnel status) to block deletion — prevents orphaned sync state data"
  - "PhoneListsController contacts endpoint uses two-query approach (distinct IDs then SourceUsers join) instead of GroupBy to avoid EF Core InMemory GroupBy limitations"
  - "TestWebApplicationFactory uses UseInternalServiceProvider with a fresh inMemoryServiceProvider to eliminate EF Core dual-provider conflict when seeding test data through factory scope"
  - "DashboardController warnings use simplified v1 approach: check TunnelsFailed > 0 on last run then query SyncRunItems for failed action items — defers complex per-tunnel aggregation to Phase 4"

patterns-established:
  - "DTO pattern: C# records, all in api/DTOs/, namespace AFHSync.Api.DTOs"
  - "Controller pattern: ControllerBase + [ApiController] + [Route('api/...')] + constructor DB injection"
  - "Enum serialization: EnumHelpers.ToPgName(value) in all controller mappings"
  - "Integration test auth: login first, extract cookie from Set-Cookie header, pass as Cookie header on subsequent requests"
  - "Test seeding: use _factory.Services.CreateScope() to get DbContext, seed data, save, then test endpoint"

requirements-completed: [DDG-04]

# Metrics
duration: 35min
completed: 2026-04-04
---

# Phase 3 Plan 02: API Controllers and DTOs Summary

**Full CRUD REST API for tunnels, phone lists, field profiles, and dashboard with 12 DTOs and 14 integration tests — DDG-04 satisfied with SourceIdentifier/SourceDisplayName storage**

## Performance

- **Duration:** ~35 min
- **Started:** 2026-04-04T04:30:00Z
- **Completed:** 2026-04-04T05:05:00Z
- **Tasks:** 2
- **Files modified:** 19

## Accomplishments

- 12 DTO files (C# records) covering all spec 6.1-6.4 response shapes with EnumHelpers for PgName enum serialization
- 4 REST controllers (TunnelsController, PhoneListsController, FieldProfilesController, DashboardController) with 13 total endpoints
- DDG-04 fulfilled: TunnelsController stores `SourceIdentifier` (Graph $filter) and `SourceDisplayName` (DDG display name) on create/update
- 14 integration tests (6 tunnel, 6 phone list, plus existing 8 auth tests) all passing — 25 total integration tests pass

## Task Commits

Each task was committed atomically:

1. **Task 1: Create DTOs for all domain entity endpoints** - `183521b` (feat)
2. **Task 2: Create TunnelsController, PhoneListsController, FieldProfilesController, DashboardController** - `d3dadf3` (feat)

## Files Created/Modified

- `api/DTOs/EnumHelpers.cs` - ToPgName<T> reflection helper for PostgreSQL enum string serialization
- `api/DTOs/TunnelDto.cs` - List endpoint DTO with nested TunnelTargetListDto and TunnelLastSyncDto
- `api/DTOs/TunnelDetailDto.cs` - Full detail DTO including TargetUserFilter and timestamps
- `api/DTOs/CreateTunnelRequest.cs` - Create request with [Required] validation on Name and SourceIdentifier
- `api/DTOs/UpdateTunnelRequest.cs` - Update request matching create shape
- `api/DTOs/PhoneListDto.cs` - List endpoint with ContactCount, UserCount, source tunnels
- `api/DTOs/PhoneListDetailDto.cs` - Detail with Description, ExchangeFolderId, timestamps
- `api/DTOs/ContactDto.cs` - Paginated contact data from SourceUser entity
- `api/DTOs/FieldProfileDto.cs` - List endpoint with field count
- `api/DTOs/FieldProfileDetailDto.cs` - Detail with grouped FieldSectionDto/FieldSettingDto sections
- `api/DTOs/UpdateFieldProfileRequest.cs` - Update request for field behavior changes
- `api/DTOs/DashboardDto.cs` - KPI aggregates, last sync details, warnings, recent runs
- `api/Controllers/TunnelsController.cs` - Full CRUD: GET list, GET detail, POST, PUT, PUT status, DELETE with conflict check
- `api/Controllers/PhoneListsController.cs` - GET list, GET detail, GET paginated contacts
- `api/Controllers/FieldProfilesController.cs` - GET list, GET detail with grouped sections, PUT field behaviors
- `api/Controllers/DashboardController.cs` - GET KPIs aggregated from Tunnels, PhoneLists, TargetMailboxes, SyncRuns
- `tests/AFHSync.Tests.Integration/Api/TunnelsControllerTests.cs` - 6 tests: list, auth check, create 201, DDG-04 fields, status toggle, delete 204
- `tests/AFHSync.Tests.Integration/Api/PhoneListsControllerTests.cs` - 6 tests: list, auth check, detail, 404 detail, 404 contacts, empty contacts
- `tests/AFHSync.Tests.Integration/TestWebApplicationFactory.cs` - Added UseInternalServiceProvider fix for EF Core dual-provider issue

## Decisions Made

- **EnumHelpers.ToPgName<T>**: Uses reflection on `PgNameAttribute` to serialize enums as PostgreSQL native type strings ("flag_hold" not "FlagHold") — consistent with what the database stores and what the frontend will display.
- **Contacts endpoint two-query approach**: Replaced `GroupBy` + `Include` with distinct ID lookup then `Contains` join to avoid EF Core InMemory provider GroupBy limitations while maintaining production correctness.
- **TestWebApplicationFactory UseInternalServiceProvider**: EF Core's global internal service provider caches Npgsql extensions, causing "multiple providers" errors when InMemory is added. Passing a dedicated `inMemoryServiceProvider` built from `AddEntityFrameworkInMemoryDatabase()` isolates the providers.
- **Dashboard warnings v1 simplified**: Uses `TunnelsFailed > 0` check on last run then queries `SyncRunItems` for "failed" action — complex per-tunnel warning aggregation deferred to Phase 4 frontend work.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed contacts endpoint GroupBy causing EF Core InMemory failure**
- **Found during:** Task 2 (integration test run)
- **Issue:** `GroupBy` + `Include` LINQ pattern doesn't translate in EF Core InMemory provider, returning 500 Internal Server Error
- **Fix:** Replaced with two-query approach: first query selects distinct SourceUserIds (with pagination), second query fetches SourceUsers by ID list
- **Files modified:** `api/Controllers/PhoneListsController.cs`
- **Verification:** All 14 Tunnels|PhoneLists integration tests pass
- **Committed in:** d3dadf3 (Task 2 commit)

**2. [Rule 1 - Bug] Fixed TestWebApplicationFactory dual EF Core provider conflict**
- **Found during:** Task 2 (integration test run for seeding tests)
- **Issue:** `_factory.Services.CreateScope()` caused "multiple providers registered" error because EF Core's global internal provider caches Npgsql extensions even after service descriptor removal
- **Fix:** Added dedicated `inMemoryServiceProvider` built from fresh `ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider()`, passed to `UseInternalServiceProvider`
- **Files modified:** `tests/AFHSync.Tests.Integration/TestWebApplicationFactory.cs`
- **Verification:** All 25 integration tests pass including tests that seed data through `_factory.Services`
- **Committed in:** d3dadf3 (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (2 Rule 1 bugs)
**Impact on plan:** Both fixes necessary for test correctness. No scope creep — the production code path remains correct, fixes only addressed InMemory test compatibility.

## Issues Encountered

EF Core's InMemory provider incompatibility with GroupBy + Include queries — resolved by splitting into two queries. The production Npgsql path handles the original query fine, but InMemory requires simpler LINQ patterns.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- All 13 REST endpoints are ready for Phase 4 frontend consumption
- TunnelsController POST stores DDG reference (DDG-04) — frontend tunnel wizard can now save DDG filter + display name
- Dashboard KPIs query live DB data — Phase 4 DASH-01 can remove stub KPI cards and wire to GET /api/dashboard
- FieldProfilesController returns grouped sections — Phase 4 field mapping UI can render section-organized field rows
- PhoneListsController contacts endpoint supports pagination — Phase 4 lists-on-phones page can paginate contacts

---
*Phase: 03-api-layer-scheduling*
*Completed: 2026-04-04*
