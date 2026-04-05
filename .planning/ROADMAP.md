# Roadmap: AFH Sync

## Overview

AFH Sync replaces CiraSync with a self-hosted contact sync platform that routes DDG members to phone-visible contact folders across 776 mailboxes. The build proceeds foundation-up: infrastructure and auth first, then the sync engine (the hardest and most critical component), then the API surface and scheduling, then the admin frontend, then differentiator features that go beyond CiraSync parity, and finally photo sync isolated to its own phase due to its radically different API load profile.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [x] **Phase 1: Foundation & Infrastructure** - Docker Compose, PostgreSQL schema, Entra registration, JWT auth (completed 2026-04-04)
- [x] **Phase 2: Sync Engine Core** - DDG resolution via Graph filter, delta sync, stale handling, throttle retry, logging (completed 2026-04-04)
- [x] **Phase 3: API Layer & Scheduling** - REST endpoints, DDG proxy with Exchange PowerShell, Hangfire scheduling, sync triggers (completed 2026-04-04)
- [ ] **Phase 4: Admin Frontend** - Dashboard, tunnel management, lists, field profiles, runs/logs, settings pages
- [ ] **Phase 5: Differentiator Features** - Tunnel wizard, impact preview, iPhone frame preview, live contact card preview, confirmation dialogs
- [ ] **Phase 6: Photo Sync** - Hash-based photo writes, separate pass, configurable mode

## Phase Details

### Phase 1: Foundation & Infrastructure
**Goal**: All services run in Docker Compose with a fully migrated database, working JWT auth, and validated Entra app registration -- the platform every subsequent phase builds on
**Depends on**: Nothing (first phase)
**Requirements**: INFRA-01, INFRA-02, INFRA-03, INFRA-04, INFRA-05, AUTH-01, AUTH-02, AUTH-03, AUTH-04
**Success Criteria** (what must be TRUE):
  1. Running `docker compose up` starts all 5 containers (nginx, frontend, API, worker, Postgres) and they pass health checks
  2. Admin can log in with username/password at the frontend login page and receives a JWT that persists across browser refresh
  3. API rejects requests without a valid JWT (returns 401) on all endpoints except login
  4. PostgreSQL contains complete schema (all tables, enums, indexes) and seed data (default field profile, phone lists, tunnel configs matching the 6 CiraSync offices)
  5. Entra app registration exists with all required Graph permissions and admin consent granted
**Plans**: 4 plans

Plans:
- [x] 01-01-PLAN.md -- .NET solution skeleton, Docker infrastructure (compose, Dockerfiles, nginx), environment template
- [x] 01-02-PLAN.md -- Wave 0 test infrastructure, EF Core entities, enums, DbContext, migrations, seed data
- [x] 01-03-PLAN.md -- JWT auth, health controller, Graph health check, Entra docs, real auth tests
- [x] 01-04-PLAN.md -- Next.js frontend with login page, dashboard, sidebar, auth middleware, Sotheby's design system

### Phase 2: Sync Engine Core
**Goal**: The worker can execute a full sync run for any tunnel -- resolving source members via stored Graph filter, building contact payloads, delta-comparing via SHA-256, writing creates/updates to Graph, handling stale contacts, and logging every action
**Depends on**: Phase 1
**Requirements**: SYNC-01, SYNC-02, SYNC-03, SYNC-04, SYNC-05, SYNC-06, SYNC-07, SYNC-08, SYNC-09, SYNC-10, SYNC-11, LOGS-01, LOGS-02, LOGS-03, LOGS-04
**Success Criteria** (what must be TRUE):
  1. Worker resolves tunnel source members using stored Graph $filter query and correctly excludes disabled accounts, shared/room mailboxes, and service accounts
  2. Contacts that have not changed (SHA-256 hash match) generate zero Graph API calls -- only new and changed contacts trigger writes
  3. Stale contacts (removed from source DDG) are handled according to tunnel policy: auto-removed, flagged with hold timer, or left in place
  4. Graph 429 throttle responses trigger automatic retry with exponential backoff and jitter, respecting the Retry-After header
  5. Every sync run produces a complete audit trail: run-level summary (counts, timing, status) and per-item results (action taken, field-level changes for updates, error messages for failures)
**Plans**: 4 plans

Plans:
- [x] 02-01-PLAN.md -- Worker DI bootstrap, GraphClientFactory, source member resolution with Graph $filter + PageIterator, contact payload builder with SHA-256 hashing
- [x] 02-02-PLAN.md -- Graph contact write pipeline (ContactWriter CRUD, ContactFolderManager lazy creation), Polly 8 GraphResilienceHandler for 429 throttling
- [x] 02-03-PLAN.md -- StaleContactHandler (set-difference + policy execution), RunLogger (batch SyncRunItem insert), SyncEngine orchestrator with bounded parallelism
- [x] 02-04-PLAN.md -- Gap closure: Wire ThrottleCounter from GraphResilienceHandler to SyncEngine for accurate SyncRun.ThrottleEvents tracking

### Phase 3: API Layer & Scheduling
**Goal**: The API exposes all CRUD endpoints the frontend needs, the DDG proxy resolves Exchange DDGs for the picker, and sync runs execute automatically on schedule or on demand
**Depends on**: Phase 2
**Requirements**: DDG-01, DDG-02, DDG-03, DDG-04, SCHD-01, SCHD-02, SCHD-03, SCHD-04, SCHD-05
**Success Criteria** (what must be TRUE):
  1. API can list DDGs from Exchange Online, read a DDG's RecipientFilter, convert it to a Graph-compatible $filter, and store both the original DDG reference and the converted filter on the tunnel
  2. Sync runs execute automatically on a configurable cron schedule (default every 4 hours) without manual intervention
  3. Admin can trigger a manual sync (real or dry-run) for specific tunnels or all active tunnels via API, and concurrent runs are blocked
  4. Dry-run mode executes the full sync pipeline but writes nothing to Graph, producing the same audit trail as a real run
**Plans**: 4 plans

Plans:
- [x] 03-01-PLAN.md -- Hangfire infrastructure (API client + Worker server), SyncRunsController (trigger, polling, concurrent guard), SettingsController with dynamic cron reschedule
- [x] 03-02-PLAN.md -- CRUD controllers and DTOs for tunnels, phone lists, field profiles, dashboard
- [x] 03-03-PLAN.md -- DDG proxy layer: Exchange PowerShell DDG resolution, OPATH-to-OData filter conversion, GraphController endpoints
- [x] 03-04-PLAN.md -- DDG-04 gap closure: SourceSmtpAddress on Tunnel entity, DTOs, controller, EF Core migration

### Phase 4: Admin Frontend
**Goal**: Admins can manage the entire sync platform through a polished web UI -- viewing dashboard KPIs, browsing and editing tunnels, managing phone lists, configuring field profiles, reviewing run history, and adjusting settings
**Depends on**: Phase 3
**Requirements**: TUNL-02, TUNL-03, TUNL-04, TUNL-05, TUNL-06, DDG-05, DDG-06, LIST-01, LIST-02, FILD-01, FILD-02, DASH-01, DASH-02, DASH-03, DASH-04, RLOG-01, RLOG-02, RLOG-03, RLOG-04, SETT-01, SETT-02, SETT-03, SETT-04, DSGN-01, DSGN-02, DSGN-03
**Success Criteria** (what must be TRUE):
  1. Dashboard displays 4 KPI cards (active tunnels, phone lists, target users, last sync status), a clickable active tunnel summary, the last 5 runs, and working "Run Sync Now" / "Dry Run" buttons
  2. Admin can view the tunnel list table and click into any tunnel to see its detail page with KPI cards, source DDG name with plain-language filter translation, and edit capabilities for name, source, targets, field profile, and stale policy
  3. Admin can view all phone lists with contact counts and paginate through contacts in any list; field profiles display grouped field rows with editable behavior selectors (always, add_missing, nosync, remove_blank)
  4. Run history page shows a sortable table of past runs, clicking a row opens per-tunnel summaries with failure/warning lists and items filterable by action type
  5. All pages follow the Sotheby's design system: navy/gold palette, Cormorant Garamond headings, DM Sans body, consistent status badges and UI patterns
**Plans**: 6 plans
**UI hint**: yes

Plans:
- [x] 04-01-PLAN.md -- Shared infrastructure: npm installs (TanStack Query/Table, sonner), 14 shadcn components, TypeScript types mirroring all DTOs, typed API client, QueryClientProvider, 7 reusable components (StatusBadge, KPICard, PageHeader, DataTable, ConfirmDialog, EmptyState, SettingsCard)
- [x] 04-02-PLAN.md -- Dashboard page: live KPI cards, active tunnel summary with clickable rows, recent runs, Run Sync Now / Dry Run buttons with polling and toast notifications
- [x] 04-03-PLAN.md -- Tunnel pages: tunnel list with DataTable and row actions, tunnel detail with inline edit, DDG picker, activate/deactivate/delete with confirmation dialogs
- [x] 04-04-PLAN.md -- Runs & Logs pages: run history with paginated DataTable, run detail with KPI cards, summary, action filter tabs, paginated items table
- [x] 04-05-PLAN.md -- Phone Lists, Field Profiles, Settings pages: expandable contact browsing, auto-save field behavior dropdowns, grouped settings cards with per-card save
- [x] 04-06-PLAN.md -- Gap closure: DDG-06 plain-language filter on tunnel detail, RLOG-02/RLOG-03 per-tunnel summaries on run detail

### Phase 5: Differentiator Features
**Goal**: The admin experience goes beyond CiraSync parity with a guided tunnel creation wizard, impact previews before destructive changes, an iPhone frame contact preview, and live contact card previews during field profile editing
**Depends on**: Phase 4
**Requirements**: TUNL-01, TUNL-07, TUNL-08, DDG-07, LIST-03, LIST-04, FILD-03, FILD-04
**Success Criteria** (what must be TRUE):
  1. Admin can create a tunnel through a 4-step wizard (Name, Source DDG with picker/search/filters/live member count, Target Lists, Review) that validates each step before proceeding
  2. Saving tunnel changes shows an impact preview with estimated creates/updates/removals, and high-impact changes (source swap, target removal, disable, delete) require explicit confirmation
  3. Lists on Phones page shows a list selector alongside an iPhone frame preview, and clicking a contact in the preview opens a full contact card detail view
  4. Field mapping page displays grouped field rows alongside a live contact card preview that updates in real-time as field behaviors are changed
  5. "Refresh from DDG" button on tunnel detail re-reads the Exchange filter and updates the stored Graph filter
**Plans**: 5 plans
**UI hint**: yes

Plans:
- [x] 05-01-PLAN.md -- Shared foundation: backend preview + refresh-ddg endpoints, enriched ContactDto, shared ContactCard component, frontend types/hooks/API, shadcn Checkbox + ScrollArea
- [x] 05-02-PLAN.md -- Tunnel creation wizard: DDGSearchList extraction, WizardStepper, 4-step TunnelWizard dialog, "Create Tunnel" button on tunnel list
- [x] 05-03-PLAN.md -- Impact preview + DDG refresh: ImpactPreviewDialog, DDGRefreshButton, high-impact change detection on tunnel detail save flow
- [x] 05-04-PLAN.md -- iPhone frame phone preview: IPhoneFrame CSS component, ContactList with alphabetical grouping, Lists on Phones page rewrite with split layout and contact card slide-over
- [x] 05-05-PLAN.md -- Live field profile preview: ContactCardPreview with sample data and field-to-nosync mapping, field profiles page split layout with sticky preview

### Phase 6: Photo Sync
**Goal**: Contact photos sync to target mailboxes with hash-based conditional writes, isolated from the core sync pipeline to manage API load independently
**Depends on**: Phase 2
**Requirements**: PHOT-01, PHOT-02, PHOT-03, PHOT-04
**Success Criteria** (what must be TRUE):
  1. Source user photos are fetched from Graph and only written to target contacts when the photo's SHA-256 hash has changed
  2. Photo sync runs as a separate pass with lower concurrency than contact sync to manage API load
  3. Admin can configure photo sync mode (included with contact sync, separate pass, or disabled) from the settings page
**Plans**: 2 plans

Plans:
- [x] 06-01-PLAN.md -- PhotoSyncService engine (fetch-once write-many, ETag-first optimization, SHA-256 delta, lower concurrency), SyncEngine trailing pass integration, Hangfire separate_pass job, unit tests
- [x] 06-02-PLAN.md -- Tunnel.PhotoSyncEnabled entity + migration, API DTO extensions (PhotosFailed), frontend photo stats on run detail, tunnel detail photo toggle, settings page cron + auto-trigger for separate_pass mode

## Progress

**Execution Order:**
Phases execute in numeric order: 1 -> 2 -> 3 -> 4 -> 5 -> 6

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Foundation & Infrastructure | 4/4 | Complete   | 2026-04-04 |
| 2. Sync Engine Core | 4/4 | Complete   | 2026-04-04 |
| 3. API Layer & Scheduling | 4/4 | Complete | 2026-04-04 |
| 4. Admin Frontend | 6/6 | Complete | 2026-04-04 |
| 5. Differentiator Features | 0/5 | Not started | - |
| 6. Photo Sync | 0/2 | Not started | - |
