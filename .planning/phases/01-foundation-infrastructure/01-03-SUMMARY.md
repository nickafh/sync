---
phase: 01-foundation-infrastructure
plan: 03
subsystem: auth
tags: [jwt, httponly-cookie, jwt-bearer, graph-api, entra, health-check, webapplicationfactory]

# Dependency graph
requires:
  - phase: 01-foundation-infrastructure/01
    provides: ".NET solution skeleton with NuGet packages (JwtBearer, Graph SDK, Azure.Identity)"
  - phase: 01-foundation-infrastructure/02
    provides: "DbContext, entity types, EF Core migrations, test project infrastructure"
provides:
  - "JWT authentication with httpOnly cookie (login/logout/me endpoints)"
  - "Global auth filter requiring authentication on all endpoints by default"
  - "Graph health check service validating Entra credentials"
  - "Health controller with DB and Graph checks (AllowAnonymous)"
  - "TestWebApplicationFactory for integration testing with InMemory DB"
  - "Entra app registration documentation"
affects: [04-frontend-shell, 02-sync-engine, 03-api-endpoints]

# Tech tracking
tech-stack:
  added: [Microsoft.EntityFrameworkCore.InMemory (test)]
  patterns: [httpOnly JWT cookie auth, global auth filter with AllowAnonymous opt-out, WebApplicationFactory with InMemory DB override]

key-files:
  created:
    - api/Controllers/AuthController.cs
    - api/Controllers/HealthController.cs
    - api/Services/GraphHealthService.cs
    - tests/AFHSync.Tests.Integration/TestWebApplicationFactory.cs
    - docs/entra-setup.md
  modified:
    - api/Program.cs
    - tests/AFHSync.Tests.Integration/AuthTests.cs
    - tests/AFHSync.Tests.Unit/AuthMiddlewareTests.cs
    - tests/AFHSync.Tests.Integration/AFHSync.Tests.Integration.csproj
    - tests/AFHSync.Tests.Unit/AFHSync.Tests.Unit.csproj

key-decisions:
  - "Removed outer try/catch in Program.cs for WebApplicationFactory compatibility -- migration errors caught locally instead"
  - "HealthController uses IServiceProvider for graceful DbContext resolution when not registered"
  - "TestWebApplicationFactory with unique InMemory DB per instance avoids test cross-contamination"
  - "Unit test project references integration project to share TestWebApplicationFactory"

patterns-established:
  - "AllowAnonymous opt-out: global auth filter + [AllowAnonymous] on public endpoints"
  - "Cookie-based JWT: JwtBearer OnMessageReceived reads from afh_auth cookie"
  - "Test infrastructure: TestWebApplicationFactory replaces Npgsql with InMemory for isolated integration tests"

requirements-completed: [INFRA-05, AUTH-01, AUTH-02, AUTH-03]

# Metrics
duration: 9min
completed: 2026-04-03
---

# Phase 01 Plan 03: Authentication & Health Checks Summary

**JWT auth with httpOnly cookie, global route protection, Graph health service, and 17 passing tests replacing all auth stubs**

## Performance

- **Duration:** 9 min
- **Started:** 2026-04-03T20:48:21Z
- **Completed:** 2026-04-03T20:57:11Z
- **Tasks:** 2
- **Files modified:** 10

## Accomplishments

- JWT authentication system with login/logout/me endpoints, httpOnly cookie with SameSite=Strict, 24-hour token lifetime
- Global auth filter (AUTH-03) requiring authentication on all endpoints except health and login/logout
- Graph health check service that validates Entra credentials and User.Read.All permission via GraphServiceClient
- Health controller with database connectivity check (/health) and Graph permission check (/health/graph)
- Complete Entra app registration guide with all 5 required Graph permissions
- All 10 auth stub tests replaced with 17 real assertions using WebApplicationFactory

## Task Commits

Each task was committed atomically:

1. **Task 1: Create JWT authentication, health controller, Graph service, and Entra docs** - `9232baf` (feat)
2. **Task 2: Replace auth stub tests with real WebApplicationFactory assertions** - `64623c0` (test)

## Files Created/Modified

- `api/Controllers/AuthController.cs` - Login/logout/me endpoints with JWT cookie auth (D-05, D-06, D-07, D-08)
- `api/Controllers/HealthController.cs` - Health check endpoints with [AllowAnonymous] (D-11)
- `api/Services/GraphHealthService.cs` - Entra credential validation via Graph SDK
- `api/Program.cs` - JwtBearer middleware, global auth filter, GraphHealthService registration
- `docs/entra-setup.md` - Step-by-step Entra app registration guide with all 5 permissions
- `tests/AFHSync.Tests.Integration/TestWebApplicationFactory.cs` - InMemory DB override for testing
- `tests/AFHSync.Tests.Integration/AuthTests.cs` - 7 integration tests (login, cookie, session, logout)
- `tests/AFHSync.Tests.Unit/AuthMiddlewareTests.cs` - 5 unit tests (global filter, AllowAnonymous)
- `tests/AFHSync.Tests.Integration/AFHSync.Tests.Integration.csproj` - Added EF Core InMemory package
- `tests/AFHSync.Tests.Unit/AFHSync.Tests.Unit.csproj` - Added Mvc.Testing, InMemory, integration project reference

## Decisions Made

- **Removed outer try/catch in Program.cs:** The original Program.cs wrapped all startup in try/catch which swallowed exceptions and prevented WebApplicationFactory from detecting startup failures. Moved error handling to only wrap the migration step, allowing the host builder to propagate errors correctly.
- **HealthController uses IServiceProvider:** Instead of directly injecting DbContext (which fails if not registered), the health endpoint uses IServiceProvider.GetService to gracefully handle missing DbContext registration.
- **Unique InMemory DB per factory instance:** Each TestWebApplicationFactory creates a uniquely-named InMemory database to prevent state leakage between test classes.
- **Unit project references integration project:** AuthMiddlewareTests (unit project) uses the shared TestWebApplicationFactory from the integration project rather than duplicating it.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed Program.cs try/catch swallowing WebApplicationFactory startup**
- **Found during:** Task 2 (test implementation)
- **Issue:** The outer try/catch in Program.cs (from Plan 01 template) caught all exceptions during host building, preventing WebApplicationFactory from detecting that the server started. All tests failed with "The server has not been started or no web application was configured."
- **Fix:** Removed the outer try/catch. Migration errors are now caught locally. The host builder exceptions propagate correctly for both production (crash-on-startup) and testing (factory sees the error).
- **Files modified:** api/Program.cs
- **Verification:** All 17 tests pass after fix
- **Committed in:** 64623c0 (Task 2 commit)

**2. [Rule 3 - Blocking] Added IsInMemory() guard for migration step**
- **Found during:** Task 2 (test implementation)
- **Issue:** The `MigrateAsync()` call in Program.cs would fail with InMemory database provider since InMemory doesn't support migrations.
- **Fix:** Added `if (!db.Database.IsInMemory())` guard before `MigrateAsync()` call.
- **Files modified:** api/Program.cs
- **Verification:** Tests pass; production behavior unchanged (Npgsql is not InMemory)
- **Committed in:** 64623c0 (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (1 bug, 1 blocking)
**Impact on plan:** Both fixes necessary for test infrastructure to function. No scope creep.

## Issues Encountered

- **Parallel execution with Plan 01-02:** Plan 01-02 (database layer) was executing simultaneously and modified Program.cs to add DbContext registration with Npgsql MapEnum. The merged Program.cs includes both plans' additions. Task 2's commit also captured Plan 01-02's untracked files (shared/Entities, api/Data, api/Migrations) because they were on disk in the working tree. This is expected behavior during parallel execution and will be reconciled by the orchestrator.

## Known Stubs

None. All authentication endpoints, health checks, and tests contain real implementations with no placeholder data.

## User Setup Required

**Entra app registration required for Graph health check.** See [docs/entra-setup.md](/Users/nick/Documents/Code/AFHsync/docs/entra-setup.md) for:
- Creating Entra app registration in Azure Portal
- Configuring 5 required Graph API permissions
- Setting Graph:TenantId, Graph:ClientId, Graph:ClientSecret in configuration
- Verifying with GET /health/graph endpoint

## Next Phase Readiness

- Authentication system complete and tested -- frontend (Plan 04) can integrate with login page
- Health endpoints provide operational monitoring for DB and Graph connectivity
- Entra documentation ready for manual provisioning (may take 1-3 days for permission propagation)
- TestWebApplicationFactory established for all future integration tests

## Self-Check: PASSED

- All 8 created/modified files verified present on disk
- Commit 9232baf (Task 1) verified in git log
- Commit 64623c0 (Task 2) verified in git log
- Build: 0 errors, 0 warnings (excluding MSB3277 version conflict warnings)
- Tests: 17 passed, 0 failed (12 integration + 5 unit)

---
*Phase: 01-foundation-infrastructure*
*Completed: 2026-04-03*
