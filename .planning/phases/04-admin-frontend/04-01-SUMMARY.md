---
phase: 04-admin-frontend
plan: 01
subsystem: ui
tags: [react, tanstack-query, tanstack-table, shadcn, typescript, next.js]

# Dependency graph
requires:
  - phase: 03-api-layer-scheduling
    provides: REST API endpoints and DTOs for all 7 resource domains
provides:
  - TanStack Query/Table + sonner installed
  - 14 shadcn components (table, dialog, select, badge, tabs, separator, skeleton, sonner, dropdown-menu, popover, command, switch, tooltip, pagination)
  - 8 TypeScript type files mirroring all backend DTOs
  - Typed API client with 7 domain method groups
  - QueryClientProvider wrapping app layout with Toaster
  - 7 query hook files with correct stale times and cache invalidation
  - 7 reusable components (StatusBadge, KPICard, PageHeader, DataTable, ConfirmDialog, EmptyState, SettingsCard)
affects: [04-02, 04-03, 04-04, 04-05]

# Tech tracking
tech-stack:
  added: ["@tanstack/react-query ^5", "@tanstack/react-table ^8", "sonner ^2", "14 shadcn base-nova components"]
  patterns: ["N+1 pagination (fetch pageSize+1 for hasNextPage)", "browser QueryClient singleton for SSR safety", "query key convention: domain-resource-params", "status badge color mapping table"]

key-files:
  created:
    - frontend/src/lib/query-client.ts
    - frontend/src/types/common.ts
    - frontend/src/types/dashboard.ts
    - frontend/src/types/tunnel.ts
    - frontend/src/types/sync-run.ts
    - frontend/src/types/phone-list.ts
    - frontend/src/types/field-profile.ts
    - frontend/src/types/settings.ts
    - frontend/src/types/ddg.ts
    - frontend/src/hooks/use-dashboard.ts
    - frontend/src/hooks/use-tunnels.ts
    - frontend/src/hooks/use-sync-runs.ts
    - frontend/src/hooks/use-phone-lists.ts
    - frontend/src/hooks/use-field-profiles.ts
    - frontend/src/hooks/use-settings.ts
    - frontend/src/hooks/use-ddgs.ts
    - frontend/src/components/StatusBadge.tsx
    - frontend/src/components/KPICard.tsx
    - frontend/src/components/PageHeader.tsx
    - frontend/src/components/DataTable.tsx
    - frontend/src/components/ConfirmDialog.tsx
    - frontend/src/components/EmptyState.tsx
    - frontend/src/components/SettingsCard.tsx
  modified:
    - frontend/package.json
    - frontend/src/lib/api.ts
    - frontend/src/app/(app)/layout.tsx

key-decisions:
  - "base-nova Button does not support asChild -- used Link wrapping Button instead of asChild for EmptyState CTA"
  - "204 No Content handling added to fetchApi for DELETE endpoints that return no body"

patterns-established:
  - "QueryClient singleton: getQueryClient() returns server-fresh or browser-cached QueryClient"
  - "N+1 pagination: hooks fetch pageSize+1 items, pass hasNextPage based on result length > pageSize"
  - "Status badge mapping: statusConfig lookup table with bg/text/dot/pulse per status string"
  - "Query invalidation: mutations invalidate all affected query keys (e.g., tunnel update invalidates tunnels + tunnel + dashboard)"
  - "Type mirroring: each backend DTO has a matching TypeScript interface using camelCase properties"

requirements-completed: [DSGN-01, DSGN-02, DSGN-03]

# Metrics
duration: 5min
completed: 2026-04-04
---

# Phase 4 Plan 01: Shared Infrastructure Summary

**TanStack Query/Table with 14 shadcn components, 8 DTO type files, typed API client, 7 query hooks, and 7 reusable Sotheby's-themed components**

## Performance

- **Duration:** 5 min
- **Started:** 2026-04-04T14:59:44Z
- **Completed:** 2026-04-04T15:04:34Z
- **Tasks:** 2
- **Files modified:** 43

## Accomplishments
- Installed TanStack Query, TanStack Table, sonner, and 14 shadcn base-nova components as the UI toolkit foundation
- Created 8 TypeScript type files mirroring every backend DTO with correct camelCase mapping, plus typed API client covering all 7 resource domains and 7 query hook files with tuned stale times
- Built 7 reusable components (StatusBadge, KPICard, PageHeader, DataTable, ConfirmDialog, EmptyState, SettingsCard) following Sotheby's design system with navy/gold palette, Cormorant Garamond headings, and consistent status badge colors

## Task Commits

Each task was committed atomically:

1. **Task 1: Install dependencies and shadcn components, create TypeScript types and API client** - `4ffdd8a` (feat)
2. **Task 2: Build reusable components** - `80e763a` (feat)

## Files Created/Modified
- `frontend/package.json` - Added @tanstack/react-query, @tanstack/react-table, sonner
- `frontend/src/lib/query-client.ts` - QueryClient singleton factory with browser caching
- `frontend/src/lib/api.ts` - Extended with 7 domain method groups (dashboard, tunnels, syncRuns, phoneLists, fieldProfiles, settings, ddgs)
- `frontend/src/app/(app)/layout.tsx` - Wrapped with QueryClientProvider and Toaster
- `frontend/src/types/*.ts` - 8 type files mirroring all backend DTOs
- `frontend/src/hooks/*.ts` - 7 query hook files with stale times and cache invalidation
- `frontend/src/components/StatusBadge.tsx` - Status-to-color badge with 6 status categories
- `frontend/src/components/KPICard.tsx` - Metric display card (label + value)
- `frontend/src/components/PageHeader.tsx` - Display heading with description and action slot
- `frontend/src/components/DataTable.tsx` - Generic TanStack Table wrapper with pagination and skeleton loading
- `frontend/src/components/ConfirmDialog.tsx` - Destructive/default confirmation modal
- `frontend/src/components/EmptyState.tsx` - Zero-data placeholder with icon, heading, CTA
- `frontend/src/components/SettingsCard.tsx` - Settings form card with gold Save button
- `frontend/src/components/ui/*.tsx` - 14 shadcn components (table, dialog, select, badge, tabs, separator, skeleton, sonner, dropdown-menu, popover, command, switch, tooltip, pagination)

## Decisions Made
- base-nova shadcn Button uses @base-ui/react and does not support the `asChild` prop -- EmptyState CTA uses Link wrapping Button instead
- Added 204 No Content handling to fetchApi for DELETE endpoints that return no response body

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed EmptyState asChild incompatibility with base-nova Button**
- **Found during:** Task 2 (EmptyState component)
- **Issue:** Plan specified `Button asChild` with Link child, but shadcn base-nova preset uses @base-ui/react Button which does not support asChild
- **Fix:** Changed to Link wrapping Button instead of Button asChild with Link child
- **Files modified:** frontend/src/components/EmptyState.tsx
- **Verification:** TypeScript compiles with zero errors
- **Committed in:** 80e763a (Task 2 commit)

**2. [Rule 2 - Missing Critical] Added 204 No Content handling to fetchApi**
- **Found during:** Task 1 (API client extension)
- **Issue:** DELETE endpoints return 204 with no body; calling res.json() on 204 throws an error
- **Fix:** Added early return for 204 status before calling res.json()
- **Files modified:** frontend/src/lib/api.ts
- **Verification:** TypeScript compiles, build succeeds
- **Committed in:** 4ffdd8a (Task 1 commit)

---

**Total deviations:** 2 auto-fixed (1 bug, 1 missing critical)
**Impact on plan:** Both fixes necessary for correctness. No scope creep.

## Issues Encountered
None beyond the auto-fixed deviations.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- All shared infrastructure is in place for plans 04-02 through 04-05
- TypeScript compiles with zero errors and Next.js build succeeds
- Every subsequent page plan can import types, API methods, query hooks, and reusable components directly

## Self-Check: PASSED

All 38 key files verified present. Both task commits (4ffdd8a, 80e763a) verified in git log.

---
*Phase: 04-admin-frontend*
*Completed: 2026-04-04*
