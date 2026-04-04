---
phase: 05-differentiator-features
plan: 02
subsystem: ui
tags: [react, wizard, dialog, tunnel-creation, ddg-picker, stepper, form-validation]

# Dependency graph
requires:
  - phase: 05-differentiator-features
    plan: 01
    provides: CreateTunnelRequest type, useCreateTunnel hook, shadcn Checkbox/ScrollArea, DDGPicker, api.tunnels.create
  - phase: 04-admin-frontend
    provides: Tunnels list page, PageHeader, DataTable, ConfirmDialog, Dialog, Command, hooks, types
provides:
  - TunnelWizard 4-step dialog component for guided tunnel creation
  - WizardStepper horizontal step indicator bar component
  - DDGSearchList reusable inline DDG search/filter/list component
  - StepName, StepSource, StepTargets, StepReview wizard step components
  - "Create Tunnel" button on tunnel list page
affects: [05-03, 05-04, 05-05]

# Tech tracking
tech-stack:
  added: []
  patterns: [wizard step extraction pattern, DDGSearchList reuse between DDGPicker dialog and wizard inline]

key-files:
  created:
    - frontend/src/components/TunnelWizard.tsx
    - frontend/src/components/WizardStepper.tsx
    - frontend/src/components/DDGSearchList.tsx
    - frontend/src/components/wizard/StepName.tsx
    - frontend/src/components/wizard/StepSource.tsx
    - frontend/src/components/wizard/StepTargets.tsx
    - frontend/src/components/wizard/StepReview.tsx
  modified:
    - frontend/src/components/DDGPicker.tsx
    - frontend/src/app/(app)/tunnels/page.tsx

key-decisions:
  - "DDGSearchList extracted as shared component used by both DDGPicker (dialog wrapper) and StepSource (inline)"
  - "WizardStepper uses self-start mt-4 on connecting lines to vertically center between step circles"
  - "Discard confirmation uses existing ConfirmDialog component rather than custom dialog"

patterns-established:
  - "Wizard step components: each step is a separate 'use client' component with typed props for data + onChange + error"
  - "DDGSearchList reuse: DDGPicker wraps it in Dialog, StepSource renders it inline with selected summary below"

requirements-completed: [TUNL-01]

# Metrics
duration: 4min
completed: 2026-04-04
---

# Phase 05 Plan 02: Tunnel Creation Wizard Summary

**4-step tunnel creation wizard with inline DDG picker, phone list checkboxes, review summary, and discard confirmation dialog**

## Performance

- **Duration:** 4 min
- **Started:** 2026-04-04T21:38:39Z
- **Completed:** 2026-04-04T21:42:43Z
- **Tasks:** 2
- **Files modified:** 9

## Accomplishments
- Extracted DDGSearchList as reusable component shared between DDGPicker dialog and wizard step 2 inline view
- Built 4-step TunnelWizard dialog with step validation, discard confirmation, and form submission via useCreateTunnel hook
- Added "Create Tunnel" gold button to tunnel list PageHeader that opens the wizard
- WizardStepper renders horizontal step indicator with gold active/completed states and clickable completed steps

## Task Commits

Each task was committed atomically:

1. **Task 1: Extract DDGSearchList and refactor DDGPicker, build WizardStepper** - `f55e584` (feat)
2. **Task 2: TunnelWizard dialog with 4 steps and tunnel list integration** - `1df1dbc` (feat)

## Files Created/Modified
- `frontend/src/components/DDGSearchList.tsx` - Reusable DDG search/filter/list with type pills, Command search, selected row highlighting
- `frontend/src/components/DDGPicker.tsx` - Refactored to wrap DDGSearchList in Dialog (no internal filter/search state)
- `frontend/src/components/WizardStepper.tsx` - Horizontal step indicator bar with gold active/completed circles and connecting lines
- `frontend/src/components/TunnelWizard.tsx` - Full wizard dialog with step state management, validation, discard confirm, form submission
- `frontend/src/components/wizard/StepName.tsx` - Step 1: tunnel name input with validation error display
- `frontend/src/components/wizard/StepSource.tsx` - Step 2: inline DDGSearchList with selected DDG summary
- `frontend/src/components/wizard/StepTargets.tsx` - Step 3: phone list checkboxes with Select All/Deselect All toggle
- `frontend/src/components/wizard/StepReview.tsx` - Step 4: review card with Edit links per section, configuration defaults note
- `frontend/src/app/(app)/tunnels/page.tsx` - Added Create Tunnel button and TunnelWizard rendering

## Decisions Made
- DDGSearchList extracted as shared component used by both DDGPicker (dialog wrapper) and StepSource (inline) -- avoids code duplication and ensures DDGPicker on tunnel detail page still works after refactoring
- WizardStepper uses self-start mt-4 on connecting lines to vertically center between step circles while allowing labels below
- Discard confirmation uses existing ConfirmDialog component rather than building a custom dialog, maintaining consistency with the delete tunnel pattern

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Tunnel creation wizard is fully functional, ready for end-to-end testing with live API
- DDGSearchList is available for reuse in any future component needing inline DDG selection
- Plans 05-03 through 05-05 can proceed independently

## Self-Check: PASSED

All 9 files verified present. Both commit hashes (f55e584, 1df1dbc) verified in git log.

---
*Phase: 05-differentiator-features*
*Completed: 2026-04-04*
