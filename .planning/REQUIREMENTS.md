# Requirements: AFH Sync

**Defined:** 2026-04-03
**Core Value:** Every AFH employee sees up-to-date office contact lists on their phone without manual effort

## v1 Requirements

Requirements for initial release. Each maps to roadmap phases.

### Infrastructure

- [x] **INFRA-01**: Docker Compose configuration runs all 5 services (nginx, frontend, API, worker, Postgres) on single Azure VM
- [x] **INFRA-02**: PostgreSQL 16 database initialized with complete schema (enums, tables, indexes)
- [x] **INFRA-03**: Seed data loaded (app settings, default field profile, phone lists, tunnels with CiraSync-equivalent config)
- [x] **INFRA-04**: nginx reverse proxy routes /api/* to API and /* to frontend
- [x] **INFRA-05**: Entra app registration created with Graph permissions (User.Read.All, Group.Read.All, GroupMember.Read.All, Contacts.ReadWrite, MailboxSettings.Read)

### Authentication

- [x] **AUTH-01**: Admin can log in with username/password (credentials from environment variables)
- [x] **AUTH-02**: API issues JWT token on successful login
- [x] **AUTH-03**: All API endpoints require valid JWT (except login)
- [x] **AUTH-04**: Session persists across browser refresh (token stored client-side)

### DDG Resolution & Filter Conversion

- [x] **DDG-01**: API can list all Dynamic Distribution Groups from Exchange Online PowerShell (one-time call during tunnel setup)
- [x] **DDG-02**: API reads the RecipientFilter from a selected DDG
- [x] **DDG-03**: RecipientFilter is parsed and converted to a Microsoft Graph-compatible $filter query
- [x] **DDG-04**: Both original DDG reference (name, address, filter) and converted Graph filter are stored on the tunnel
- [x] **DDG-05**: DDG picker in UI shows DDG list with search, type filters (Office/Role/Brand), and live member counts (via Graph filter)
- [x] **DDG-06**: Tunnel detail displays source DDG name and plain-language filter translation
- [ ] **DDG-07**: "Refresh from DDG" button re-reads Exchange filter and updates stored Graph filter

### Tunnel Management

- [ ] **TUNL-01**: Admin can create a tunnel via 4-step wizard (Name -> Source DDG -> Target Lists -> Review)
- [x] **TUNL-02**: Admin can view tunnel detail with KPI cards (source contacts, target lists, target users)
- [x] **TUNL-03**: Admin can edit tunnel (name, source DDG, target lists, field profile, stale policy)
- [x] **TUNL-04**: Admin can activate/deactivate a tunnel
- [x] **TUNL-05**: Admin can delete a tunnel (with confirmation, blocked if active sync state exists)
- [x] **TUNL-06**: Tunnel list page shows table with name, source, contacts, target lists, users, last run, status
- [ ] **TUNL-07**: Impact preview shows estimated creates/updates/removals before saving tunnel changes
- [ ] **TUNL-08**: High-impact changes (source swap, target removal, disable, delete) require confirmation dialog

### Phone List Management

- [ ] **LIST-01**: Admin can view all phone lists with contact counts, user counts, and source tunnels
- [ ] **LIST-02**: Admin can view contacts in a phone list with pagination
- [ ] **LIST-03**: Lists on Phones page shows list selector (left) and iPhone frame preview (right)
- [ ] **LIST-04**: Clicking a contact in phone preview shows full contact card detail

### Field Profiles

- [ ] **FILD-01**: Admin can view field profiles with all field settings grouped by section
- [ ] **FILD-02**: Admin can edit field behavior for each field (always, add_missing, nosync, remove_blank)
- [ ] **FILD-03**: Field mapping page shows grouped field rows (left) and live contact card preview (right)
- [ ] **FILD-04**: Contact card preview updates in real-time as field behaviors change

### Sync Engine

- [x] **SYNC-01**: Sync engine resolves tunnel source members using stored Graph filter ($filter query against /users endpoint)
- [x] **SYNC-02**: Source filtering excludes disabled accounts, shared/room/equipment mailboxes, and service accounts
- [x] **SYNC-03**: Contact payload is built from source fields filtered by field profile behaviors
- [x] **SYNC-04**: SHA-256 hash computed for normalized contact payload; compared against stored contact_sync_state
- [x] **SYNC-05**: New contacts (no existing state) are created via Graph API in target mailbox contact folder
- [x] **SYNC-06**: Changed contacts (hash mismatch) are updated via Graph API; previous hash stored for rollback
- [x] **SYNC-07**: Unchanged contacts (hash match) are skipped — no Graph call made
- [x] **SYNC-08**: Contact folders are created per-mailbox if they don't exist (lazy creation with per-run caching)
- [x] **SYNC-09**: Stale contacts (no longer in source) handled per tunnel policy: auto-remove, flag-hold (with configurable hold days), or leave
- [x] **SYNC-10**: Throttle handler retries on 429 with exponential backoff + jitter, respecting Retry-After header (max 5 retries)
- [x] **SYNC-11**: Parallelism bounded by semaphore (configurable, default 4 concurrent mailboxes)

### Photo Sync

- [ ] **PHOT-01**: Source user photos fetched from Graph with SHA-256 hash comparison
- [ ] **PHOT-02**: Photo only written to target contacts when hash changes
- [ ] **PHOT-03**: Photo sync runs as separate pass with lower concurrency to manage API load
- [ ] **PHOT-04**: Photo sync mode configurable (included, separate_pass, disabled)

### Scheduling & Triggers

- [x] **SCHD-01**: Sync engine runs automatically on cron schedule (configurable, default every 4 hours)
- [x] **SCHD-02**: Admin can trigger manual sync from dashboard ("Run Sync Now" button)
- [x] **SCHD-03**: Admin can trigger dry-run sync that simulates without writing to Graph
- [x] **SCHD-04**: Admin can run sync for specific tunnel(s) or all active tunnels
- [x] **SCHD-05**: Concurrent sync runs are prevented (new run blocked while one is in progress)

### Sync Run Logging

- [x] **LOGS-01**: Each sync run creates a record with type, status, timing, and aggregate counts
- [x] **LOGS-02**: Per-item results logged (created, updated, skipped, failed, removed, photo_updated, stale_detected)
- [x] **LOGS-03**: Field-level changes tracked in JSONB (old value -> new value)
- [x] **LOGS-04**: Error messages captured per failed item

### Dashboard

- [x] **DASH-01**: Dashboard shows 4 KPI cards: active tunnels, phone lists, target users, last sync status
- [x] **DASH-02**: Active tunnel summary with clickable rows to tunnel detail
- [x] **DASH-03**: Recent runs list (last 5) with status, timing, and counts
- [x] **DASH-04**: "Run Sync Now" and "Dry Run" buttons in topbar

### Runs & Logs UI

- [ ] **RLOG-01**: Run history table with timestamp, type, duration, creates/updates/removals/photos/skipped, status
- [ ] **RLOG-02**: Clickable rows open run detail with per-tunnel summaries
- [ ] **RLOG-03**: Run detail shows failure list and warning list
- [ ] **RLOG-04**: Run items filterable by action type (created, updated, failed, etc.)

### Settings

- [ ] **SETT-01**: Admin can configure sync schedule (cron expression or dropdown)
- [ ] **SETT-02**: Admin can configure photo sync mode
- [ ] **SETT-03**: Admin can configure batch size and parallelism
- [ ] **SETT-04**: Admin can configure default stale contact policy and hold days

### Design System

- [x] **DSGN-01**: Sotheby's-aligned color palette (navy #1B2A4A, gold #C9A84C, warm neutrals)
- [x] **DSGN-02**: Typography: Cormorant Garamond headings, DM Sans body
- [x] **DSGN-03**: Consistent UI patterns: status badges, confirmation dialogs, impact previews

## v2 Requirements

Deferred to future release. Tracked but not in current roadmap.

### Enhanced Auth

- **EAUTH-01**: Admin auth via Microsoft Entra ID (MSAL) instead of simple JWT
- **EAUTH-02**: Role-based access (viewer vs admin) if team grows

### Notifications

- **NOTF-01**: Email notification on sync failure
- **NOTF-02**: Email digest of sync activity (daily/weekly)

### Advanced Sources

- **ASRC-01**: Mailbox contacts as tunnel source type (sync from a user's contact folder)
- **ASRC-02**: Static Distribution Group support as tunnel source
- **ASRC-03**: Extension attribute-based smart filtering for tunnel target scoping

### Monitoring

- **MONR-01**: Health check endpoint for external monitoring
- **MONR-02**: Database backup automation via cron

## Out of Scope

Explicitly excluded. Documented to prevent scope creep.

| Feature | Reason |
|---------|--------|
| Two-way sync | AFH's use case is strictly one-way (DDG to phones). Two-way adds conflict resolution complexity with zero value. |
| Multi-tenant support | Single internal IT tool for one organization. No current or planned need. |
| End-user self-service | All 776 users get the same lists. No user choice needed. Admin-only tool. |
| Real-time push sync | Contact data changes slowly. 4-hour schedule + manual trigger is sufficient. |
| CRM integration | Source is Exchange DDGs only. If CRM contacts needed, add them to DDGs in Exchange first. |
| MDM/Intune integration | Phones sync from Exchange natively via ActiveSync. No app deployment needed. |
| Calendar sync | Different data model, different endpoints. Contacts only. |
| Google Workspace sync | AFH is 100% Microsoft 365. No other platforms. |
| HTTPS/SSL certificates | Can be added post-launch. Not blocking v1 functionality. |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| INFRA-01 | Phase 1 | Complete |
| INFRA-02 | Phase 1 | Complete |
| INFRA-03 | Phase 1 | Complete |
| INFRA-04 | Phase 1 | Complete |
| INFRA-05 | Phase 1 | Complete |
| AUTH-01 | Phase 1 | Complete |
| AUTH-02 | Phase 1 | Complete |
| AUTH-03 | Phase 1 | Complete |
| AUTH-04 | Phase 1 | Complete |
| SYNC-01 | Phase 2 | Complete |
| SYNC-02 | Phase 2 | Complete |
| SYNC-03 | Phase 2 | Complete |
| SYNC-04 | Phase 2 | Complete |
| SYNC-05 | Phase 2 | Complete |
| SYNC-06 | Phase 2 | Complete |
| SYNC-07 | Phase 2 | Complete |
| SYNC-08 | Phase 2 | Complete |
| SYNC-09 | Phase 2 | Complete |
| SYNC-10 | Phase 2 | Complete |
| SYNC-11 | Phase 2 | Complete |
| LOGS-01 | Phase 2 | Complete |
| LOGS-02 | Phase 2 | Complete |
| LOGS-03 | Phase 2 | Complete |
| LOGS-04 | Phase 2 | Complete |
| DDG-01 | Phase 3 | Complete |
| DDG-02 | Phase 3 | Complete |
| DDG-03 | Phase 3 | Complete |
| DDG-04 | Phase 3 | Complete |
| SCHD-01 | Phase 3 | Complete |
| SCHD-02 | Phase 3 | Complete |
| SCHD-03 | Phase 3 | Complete |
| SCHD-04 | Phase 3 | Complete |
| SCHD-05 | Phase 3 | Complete |
| TUNL-02 | Phase 4 | Complete |
| TUNL-03 | Phase 4 | Complete |
| TUNL-04 | Phase 4 | Complete |
| TUNL-05 | Phase 4 | Complete |
| TUNL-06 | Phase 4 | Complete |
| DDG-05 | Phase 4 | Complete |
| DDG-06 | Phase 4 | Complete |
| LIST-01 | Phase 4 | Pending |
| LIST-02 | Phase 4 | Pending |
| FILD-01 | Phase 4 | Pending |
| FILD-02 | Phase 4 | Pending |
| DASH-01 | Phase 4 | Complete |
| DASH-02 | Phase 4 | Complete |
| DASH-03 | Phase 4 | Complete |
| DASH-04 | Phase 4 | Complete |
| RLOG-01 | Phase 4 | Pending |
| RLOG-02 | Phase 4 | Pending |
| RLOG-03 | Phase 4 | Pending |
| RLOG-04 | Phase 4 | Pending |
| SETT-01 | Phase 4 | Pending |
| SETT-02 | Phase 4 | Pending |
| SETT-03 | Phase 4 | Pending |
| SETT-04 | Phase 4 | Pending |
| DSGN-01 | Phase 4 | Complete |
| DSGN-02 | Phase 4 | Complete |
| DSGN-03 | Phase 4 | Complete |
| TUNL-01 | Phase 5 | Pending |
| TUNL-07 | Phase 5 | Pending |
| TUNL-08 | Phase 5 | Pending |
| DDG-07 | Phase 5 | Pending |
| LIST-03 | Phase 5 | Pending |
| LIST-04 | Phase 5 | Pending |
| FILD-03 | Phase 5 | Pending |
| FILD-04 | Phase 5 | Pending |
| PHOT-01 | Phase 6 | Pending |
| PHOT-02 | Phase 6 | Pending |
| PHOT-03 | Phase 6 | Pending |
| PHOT-04 | Phase 6 | Pending |

**Coverage:**
- v1 requirements: 71 total
- Mapped to phases: 71
- Unmapped: 0

---
*Requirements defined: 2026-04-03*
*Last updated: 2026-04-03 after roadmap creation*
