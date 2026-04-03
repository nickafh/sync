# AFH Sync

## What This Is

AFH Sync is an internal IT application for Atlanta Fine Homes Sotheby's International Realty that manages delivery of shared company contact lists to users' Outlook accounts and mobile phones. It replaces CiraSync with a self-hosted, tunnel-based sync platform that routes contacts from Exchange Dynamic Distribution Groups (DDGs) through Microsoft Graph into phone-visible contact folders across ~776 target mailboxes.

## Core Value

Every AFH employee sees up-to-date office contact lists on their phone without manual effort — contacts sync automatically, delta-only, with no duplicates or stale entries.

## Requirements

### Validated

(None yet — ship to validate)

### Active

- [ ] Tunnel CRUD — create, edit, activate/deactivate, delete tunnels that route DDG members to phone lists
- [ ] Phone list management — define shared contact folders that tunnels target
- [ ] Field profiles — control which contact fields sync and how (always, add_missing, nosync, remove_blank)
- [ ] Sync engine — resolve DDG membership, build normalized payloads, delta-compare via SHA-256 hashing, write via Graph
- [ ] Photo sync — hash-based conditional photo writes to avoid unnecessary Graph calls
- [ ] Stale contact handling — detect contacts no longer in source DDG, apply policy (auto-remove, flag-hold, leave)
- [ ] Throttle handling — retry with exponential backoff + jitter on Graph 429 responses
- [ ] Dry-run mode — simulate a sync run without writing to Graph
- [ ] Scheduled sync — cron-based automatic sync runs (default every 4 hours)
- [ ] Manual sync trigger — start a sync run on demand from the admin UI
- [ ] Sync run logging — per-run and per-item tracking of creates, updates, skips, failures
- [ ] Dashboard — KPI cards, active tunnel summary, recent runs, quick-action buttons
- [ ] Tunnel detail UI — view/edit mode with DDG picker, target list selection, impact preview
- [ ] Create tunnel wizard — 4-step modal (Name → Source DDG → Target Lists → Review)
- [ ] Lists on Phones page — phone list selector with iPhone frame preview of contacts
- [ ] Field mapping UI — grouped field rows with behavior selectors and live contact card preview
- [ ] Runs & logs UI — run history table with drill-down to per-tunnel and per-item detail
- [ ] Settings page — sync schedule, photo mode, batch size, parallelism, stale defaults
- [ ] Admin auth — simple JWT auth with username/password from environment variables
- [ ] DDG proxy — API endpoint to query DDGs from Graph for the frontend picker
- [ ] Impact preview — show estimated creates/updates/removals before saving tunnel changes
- [ ] Source filtering — exclude disabled accounts, shared/room mailboxes, service accounts
- [ ] Docker Compose deployment — all services (nginx, frontend, API, worker, Postgres) on single Azure VM
- [ ] Entra app registration — configure Graph permissions for the sync engine

### Out of Scope

- Real-time push notifications — sync runs on schedule or manual trigger, not real-time
- Multi-tenant support — single tenant (Atlanta Fine Homes) only
- End-user self-service — admins only, end users never log in
- Entra ID (MSAL) admin auth — deferred, using simple JWT for v1
- HTTPS/SSL certificates — can be added post-launch, not blocking v1
- Mailbox contacts source type — spec includes it but DDG sources are the priority; mailbox source can be added later

## Context

- **Replaces CiraSync**: Current vendor tool that syncs DDG contacts to phones. AFH wants to own this capability.
- **Azure VM already provisioned**: Standard D2as_v6 (2 vCPU, 8GB RAM), Ubuntu 24.04, Docker installed. IP: 20.42.104.135.
- **Entra app registration TODO**: Need to create app registration with User.Read.All, Group.Read.All, GroupMember.Read.All, Contacts.ReadWrite, MailboxSettings.Read permissions.
- **~776 target mailboxes**: All AFH + MSIR users receive synced contacts.
- **6 office tunnels to replicate**: Buckhead, North Atlanta, Intown, Blue Ridge, Cobb, Clayton — each targeting "All Atlanta Fine Homes" and "All Mountain" phone lists.
- **Extension attributes**: CustomAttribute2 = Brand (AFH/MSIR), CustomAttribute3 = Role (Advisor/Staff), CustomAttribute4 = Department. CustomAttribute1 is reserved (DO NOT USE).
- **Design system**: Sotheby's-aligned — navy (#1B2A4A), gold (#C9A84C), warm neutrals. Cormorant Garamond headings, DM Sans body.
- **Developer spec**: Full specification in `AFH_Sync_Developer_Spec.md` including database schema, API spec, sync engine pipeline, Graph integration details, Docker config, and seed data.

## Constraints

- **Tech stack**: Next.js 14+ (App Router, TypeScript, Tailwind), ASP.NET Core 8 (API + Worker), PostgreSQL 16, Docker Compose, nginx
- **Infrastructure**: Single Azure VM — no Kubernetes, no managed services beyond the VM
- **Graph API**: Application permissions only (no delegated), bounded by Microsoft throttling limits
- **Parallelism**: Semaphore-bounded concurrent mailbox processing (default 4)
- **Auth**: Simple JWT for v1 (username/password from env vars)

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Replace CiraSync with self-hosted solution | Own the capability, avoid vendor lock-in and recurring costs | -- Pending |
| Simple JWT over Entra ID auth for v1 | Faster to build, can upgrade to MSAL later | -- Pending |
| SHA-256 delta sync | Never rewrite unchanged contacts — critical for Graph API quota management with ~776 mailboxes | -- Pending |
| Single Azure VM with Docker Compose | Simplicity over scalability — workload is bounded and predictable | -- Pending |
| ASP.NET Core for API + Worker | Strong Microsoft Graph SDK support, good performance for background processing | -- Pending |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd:transition`):
1. Requirements invalidated? -> Move to Out of Scope with reason
2. Requirements validated? -> Move to Validated with phase reference
3. New requirements emerged? -> Add to Active
4. Decisions to log? -> Add to Key Decisions
5. "What This Is" still accurate? -> Update if drifted

**After each milestone** (via `/gsd:complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-04-03 after initialization*
