# AFH Sync — Complete Developer Specification

## Version 1.0 | April 2026 | Atlanta Fine Homes Sotheby's International Realty

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Infrastructure](#2-infrastructure)
3. [Project Structure](#3-project-structure)
4. [Database Schema](#4-database-schema)
5. [Seed Data](#5-seed-data)
6. [API Specification](#6-api-specification)
7. [Frontend Specification](#7-frontend-specification)
8. [Sync Engine Specification](#8-sync-engine-specification)
9. [Microsoft Graph Integration](#9-microsoft-graph-integration)
10. [Authentication](#10-authentication)
11. [Docker Configuration](#11-docker-configuration)
12. [Environment Variables](#12-environment-variables)
13. [Deployment](#13-deployment)

---

## 1. System Overview

AFH Sync is an internal IT application that manages delivery of shared company contact lists to users' Outlook accounts and mobile phones. It replaces CiraSync with a tunnel-based sync platform.

### Core Concepts

- **Tunnel**: A routing definition from a source DDG (Dynamic Distribution Group) to one or more phone-visible contact lists
- **Phone List**: A shared contact folder visible to users in Outlook/iPhone
- **Field Profile**: Controls which contact fields sync and how they behave on update
- **Sync Run**: One execution of the sync engine (manual or scheduled)
- **Contact Sync State**: Per-contact tracking of what was last synced to each target

### Architecture

```
┌──────────┐     ┌──────────┐     ┌──────────┐
│  Next.js │────▶│ ASP.NET  │────▶│ Postgres │
│ Frontend │     │ Core API │     │    16    │
└──────────┘     └──────────┘     └──────────┘
                       │
                 ┌──────────┐     ┌──────────┐
                 │  Worker  │────▶│ Microsoft│
                 │ Service  │     │  Graph   │
                 └──────────┘     └──────────┘
```

All services run in Docker Compose behind nginx on a single Azure Ubuntu VM.

### Users

- **Admin**: IT team only. Single role with full access to everything.
- **End users**: Never log in. Only see synced contact lists on their devices.

---

## 2. Infrastructure

### Azure VM

- **Name**: sync
- **Size**: Standard D2as_v6 (2 vCPUs, 8 GB RAM)
- **OS**: Ubuntu Server 24.04 LTS
- **Disk**: 64 GB Premium SSD (P6)
- **Region**: East US
- **Public IP**: 20.42.104.135 (Static, Standard SKU)
- **SSH User**: nalbano

### Services on VM

| Container | Image | Internal Port | Purpose |
|-----------|-------|---------------|---------|
| afh-nginx | nginx:alpine | 80, 443 | Reverse proxy |
| afh-frontend | Custom (Next.js) | 3000 | Admin web UI |
| afh-api | Custom (ASP.NET Core) | 8080 | REST API |
| afh-worker | Custom (ASP.NET Core) | — | Background sync engine |
| afh-postgres | postgres:16 | 5432 (localhost only) | Database |

---

## 3. Project Structure

```
afh-sync/
├── docker-compose.yml
├── .env
├── nginx/
│   └── nginx.conf
├── frontend/
│   ├── Dockerfile
│   ├── package.json
│   ├── next.config.js
│   ├── tailwind.config.js
│   ├── tsconfig.json
│   └── src/
│       ├── app/
│       │   ├── layout.tsx              # Root layout with sidebar nav
│       │   ├── page.tsx                # Dashboard
│       │   ├── tunnels/
│       │   │   ├── page.tsx            # Tunnel list
│       │   │   └── [id]/
│       │   │       └── page.tsx        # Tunnel detail (view + edit)
│       │   ├── lists/
│       │   │   └── page.tsx            # Lists on Phones with phone preview
│       │   ├── fields/
│       │   │   └── page.tsx            # Field mapping editor
│       │   ├── runs/
│       │   │   ├── page.tsx            # Run list
│       │   │   └── [id]/
│       │   │       └── page.tsx        # Run detail
│       │   └── settings/
│       │       └── page.tsx            # Settings
│       ├── components/
│       │   ├── layout/
│       │   │   ├── Sidebar.tsx
│       │   │   └── Topbar.tsx
│       │   ├── tunnels/
│       │   │   ├── TunnelTable.tsx
│       │   │   ├── TunnelDetail.tsx
│       │   │   ├── TunnelEditor.tsx
│       │   │   ├── CreateTunnelModal.tsx
│       │   │   ├── DDGPicker.tsx       # Source DDG selector with search
│       │   │   └── TargetListPicker.tsx
│       │   ├── phone/
│       │   │   ├── PhonePreview.tsx     # iPhone contact list frame
│       │   │   ├── ContactCard.tsx      # iPhone contact detail card
│       │   │   └── ContactList.tsx      # Scrollable contact list
│       │   ├── fields/
│       │   │   ├── FieldProfileEditor.tsx
│       │   │   ├── FieldRow.tsx
│       │   │   └── BehaviorSelector.tsx
│       │   ├── runs/
│       │   │   ├── RunTable.tsx
│       │   │   └── RunDetail.tsx
│       │   ├── dashboard/
│       │   │   ├── KPICard.tsx
│       │   │   ├── TunnelSummary.tsx
│       │   │   └── RecentRuns.tsx
│       │   └── shared/
│       │       ├── StatusBadge.tsx
│       │       ├── ConfirmDialog.tsx
│       │       ├── ImpactPreview.tsx
│       │       └── Avatar.tsx
│       ├── lib/
│       │   ├── api.ts                  # API client functions
│       │   └── types.ts               # TypeScript interfaces
│       └── styles/
│           └── globals.css
├── api/
│   ├── Dockerfile
│   ├── AFHSync.Api.csproj
│   ├── Program.cs
│   ├── Controllers/
│   │   ├── DashboardController.cs
│   │   ├── TunnelsController.cs
│   │   ├── PhoneListsController.cs
│   │   ├── FieldProfilesController.cs
│   │   ├── SyncRunsController.cs
│   │   ├── SettingsController.cs
│   │   └── GraphController.cs          # Proxy for DDG/user lookups
│   ├── Models/
│   │   ├── Tunnel.cs
│   │   ├── PhoneList.cs
│   │   ├── TunnelPhoneList.cs
│   │   ├── FieldProfile.cs
│   │   ├── FieldProfileField.cs
│   │   ├── SourceUser.cs
│   │   ├── TargetMailbox.cs
│   │   ├── ContactSyncState.cs
│   │   ├── SyncRun.cs
│   │   ├── SyncRunItem.cs
│   │   └── AppSetting.cs
│   ├── DTOs/
│   │   ├── TunnelDto.cs
│   │   ├── CreateTunnelRequest.cs
│   │   ├── UpdateTunnelRequest.cs
│   │   ├── DashboardDto.cs
│   │   ├── SyncRunDto.cs
│   │   ├── FieldProfileDto.cs
│   │   ├── PhoneListDto.cs
│   │   ├── ImpactPreviewDto.cs
│   │   └── DDGDto.cs
│   ├── Data/
│   │   ├── AFHSyncDbContext.cs
│   │   └── Migrations/
│   └── Services/
│       ├── TunnelService.cs
│       ├── PhoneListService.cs
│       ├── FieldProfileService.cs
│       ├── SyncRunService.cs
│       ├── SettingsService.cs
│       ├── ImpactPreviewService.cs
│       └── GraphProxyService.cs
├── worker/
│   ├── Dockerfile
│   ├── AFHSync.Worker.csproj
│   ├── Program.cs
│   ├── Workers/
│   │   ├── SyncScheduler.cs            # Cron-based trigger
│   │   └── SyncOrchestrator.cs         # Main sync pipeline
│   ├── Services/
│   │   ├── DDGResolver.cs              # Resolves DDG membership
│   │   ├── MailboxContactsResolver.cs  # Reads mailbox contact folders
│   │   ├── SourceFilter.cs             # Excludes disabled/service accounts
│   │   ├── ContactPayloadBuilder.cs    # Normalizes fields, computes hash
│   │   ├── DeltaComparer.cs            # Compares against sync state
│   │   ├── ContactWriter.cs            # Graph create/update/delete
│   │   ├── PhotoSyncer.cs              # Photo hash + conditional write
│   │   ├── StaleContactHandler.cs      # Stale detection + policy
│   │   ├── ThrottleHandler.cs          # 429 retry with backoff
│   │   └── RunLogger.cs                # Logs run + item results
│   └── Graph/
│       ├── GraphClientFactory.cs
│       ├── UserReader.cs               # Read user profiles + photos
│       ├── GroupReader.cs              # Read DDG definitions
│       ├── ContactManager.cs           # CRUD contacts in mailboxes
│       └── HealthChecker.cs            # Validate permissions + access
└── shared/
    ├── AFHSync.Shared.csproj
    ├── Constants.cs                    # Office values, attribute names
    ├── Enums.cs                        # SyncBehavior, SourceType, etc.
    └── Hashing.cs                      # SHA-256 utility
```

---

## 4. Database Schema

### Complete SQL Schema

```sql
-- ============================================================
-- AFH SYNC DATABASE SCHEMA
-- PostgreSQL 16
-- ============================================================

-- ENUMS
CREATE TYPE source_type AS ENUM ('ddg', 'mailbox_contacts');
CREATE TYPE target_scope AS ENUM ('all_users', 'specific_users');
CREATE TYPE stale_policy AS ENUM ('auto_remove', 'flag_hold', 'leave');
CREATE TYPE sync_behavior AS ENUM ('nosync', 'add_missing', 'always', 'remove_blank');
CREATE TYPE sync_status AS ENUM ('pending', 'running', 'success', 'warning', 'failed', 'cancelled');
CREATE TYPE tunnel_status AS ENUM ('active', 'inactive');
CREATE TYPE run_type AS ENUM ('manual', 'scheduled', 'dry_run');

-- ============================================================
-- CONFIGURATION
-- ============================================================

CREATE TABLE tunnels (
    id                  SERIAL PRIMARY KEY,
    name                VARCHAR(100) NOT NULL,
    source_type         source_type NOT NULL DEFAULT 'ddg',
    source_identifier   VARCHAR(500) NOT NULL,       -- DDG address or mailbox\folder path
    source_display_name VARCHAR(200),                 -- Human-readable source name
    target_scope        target_scope NOT NULL DEFAULT 'all_users',
    target_user_filter  JSONB,                        -- Filter criteria or specific user list
    field_profile_id    INT REFERENCES field_profiles(id),
    stale_policy        stale_policy NOT NULL DEFAULT 'flag_hold',
    stale_hold_days     INT NOT NULL DEFAULT 14,
    status              tunnel_status NOT NULL DEFAULT 'active',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE phone_lists (
    id                  SERIAL PRIMARY KEY,
    name                VARCHAR(200) NOT NULL,        -- e.g., "All Atlanta Fine Homes"
    exchange_folder_id  VARCHAR(500),                  -- Graph folder ID
    description         TEXT,
    contact_count       INT NOT NULL DEFAULT 0,       -- Cached count
    user_count          INT NOT NULL DEFAULT 0,       -- Cached count
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE tunnel_phone_lists (
    id                  SERIAL PRIMARY KEY,
    tunnel_id           INT NOT NULL REFERENCES tunnels(id) ON DELETE CASCADE,
    phone_list_id       INT NOT NULL REFERENCES phone_lists(id) ON DELETE CASCADE,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(tunnel_id, phone_list_id)
);

CREATE TABLE field_profiles (
    id                  SERIAL PRIMARY KEY,
    name                VARCHAR(100) NOT NULL,        -- e.g., "Default", "Minimal"
    description         TEXT,
    is_default          BOOLEAN NOT NULL DEFAULT FALSE,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE field_profile_fields (
    id                  SERIAL PRIMARY KEY,
    field_profile_id    INT NOT NULL REFERENCES field_profiles(id) ON DELETE CASCADE,
    field_name          VARCHAR(100) NOT NULL,         -- e.g., "FirstName", "BusinessPhone"
    field_section       VARCHAR(50) NOT NULL,           -- e.g., "Identity", "Contact Info"
    display_name        VARCHAR(100) NOT NULL,          -- e.g., "First Name", "Business Phone"
    behavior            sync_behavior NOT NULL DEFAULT 'always',
    display_order       INT NOT NULL DEFAULT 0,
    UNIQUE(field_profile_id, field_name)
);

CREATE TABLE app_settings (
    id                  SERIAL PRIMARY KEY,
    key                 VARCHAR(100) NOT NULL UNIQUE,
    value               TEXT NOT NULL,
    description         TEXT,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================
-- SOURCE AND TARGET STATE
-- ============================================================

CREATE TABLE source_users (
    id                  SERIAL PRIMARY KEY,
    entra_id            VARCHAR(100) NOT NULL UNIQUE,  -- Azure AD Object ID
    display_name        VARCHAR(200),
    first_name          VARCHAR(100),
    last_name           VARCHAR(100),
    email               VARCHAR(300),
    business_phone      VARCHAR(50),
    mobile_phone        VARCHAR(50),
    job_title           VARCHAR(200),
    department          VARCHAR(200),
    office_location     VARCHAR(100),
    company_name        VARCHAR(200),
    street_address      VARCHAR(500),
    city                VARCHAR(100),
    state               VARCHAR(100),
    postal_code         VARCHAR(20),
    country             VARCHAR(100),
    notes               TEXT,
    photo_hash          VARCHAR(64),                    -- SHA-256 of last fetched photo
    extension_attr_1    VARCHAR(200),                    -- ClaytonCalendar / listings (DO NOT USE)
    extension_attr_2    VARCHAR(200),                    -- Brand: AFH, MSIR
    extension_attr_3    VARCHAR(200),                    -- Role: Advisor, Staff
    extension_attr_4    VARCHAR(200),                    -- Department: Marketer, Accounting, etc.
    is_enabled          BOOLEAN NOT NULL DEFAULT TRUE,
    mailbox_type        VARCHAR(50),                     -- UserMailbox, SharedMailbox, RoomMailbox, etc.
    last_fetched_at     TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE target_mailboxes (
    id                  SERIAL PRIMARY KEY,
    entra_id            VARCHAR(100) NOT NULL UNIQUE,
    email               VARCHAR(300) NOT NULL,
    display_name        VARCHAR(200),
    is_active           BOOLEAN NOT NULL DEFAULT TRUE,
    last_verified_at    TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================
-- SYNC RUNTIME STATE
-- ============================================================

CREATE TABLE contact_sync_state (
    id                  SERIAL PRIMARY KEY,
    source_user_id      INT NOT NULL REFERENCES source_users(id),
    phone_list_id       INT NOT NULL REFERENCES phone_lists(id),
    target_mailbox_id   INT NOT NULL REFERENCES target_mailboxes(id),
    tunnel_id           INT REFERENCES tunnels(id),
    graph_contact_id    VARCHAR(200),                    -- Graph API contact ID for updates
    data_hash           VARCHAR(64),                     -- SHA-256 of normalized field payload
    photo_hash          VARCHAR(64),                     -- SHA-256 of synced photo
    previous_data_hash  VARCHAR(64),                     -- For rollback
    previous_photo_hash VARCHAR(64),                     -- For rollback
    is_stale            BOOLEAN NOT NULL DEFAULT FALSE,
    stale_detected_at   TIMESTAMPTZ,
    last_synced_at      TIMESTAMPTZ,
    last_result         VARCHAR(50),                     -- created, updated, skipped, failed, removed
    last_error          TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(source_user_id, phone_list_id, target_mailbox_id)
);

CREATE TABLE sync_runs (
    id                  SERIAL PRIMARY KEY,
    run_type            run_type NOT NULL DEFAULT 'manual',
    status              sync_status NOT NULL DEFAULT 'pending',
    is_dry_run          BOOLEAN NOT NULL DEFAULT FALSE,
    started_at          TIMESTAMPTZ,
    completed_at        TIMESTAMPTZ,
    duration_ms         INT,
    tunnels_processed   INT NOT NULL DEFAULT 0,
    tunnels_warned      INT NOT NULL DEFAULT 0,
    tunnels_failed      INT NOT NULL DEFAULT 0,
    contacts_created    INT NOT NULL DEFAULT 0,
    contacts_updated    INT NOT NULL DEFAULT 0,
    contacts_removed    INT NOT NULL DEFAULT 0,
    contacts_skipped    INT NOT NULL DEFAULT 0,
    contacts_failed     INT NOT NULL DEFAULT 0,
    photos_updated      INT NOT NULL DEFAULT 0,
    photos_failed       INT NOT NULL DEFAULT 0,
    throttle_events     INT NOT NULL DEFAULT 0,
    error_summary       TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE sync_run_items (
    id                  SERIAL PRIMARY KEY,
    sync_run_id         INT NOT NULL REFERENCES sync_runs(id) ON DELETE CASCADE,
    tunnel_id           INT REFERENCES tunnels(id),
    phone_list_id       INT REFERENCES phone_lists(id),
    target_mailbox_id   INT REFERENCES target_mailboxes(id),
    source_user_id      INT REFERENCES source_users(id),
    action              VARCHAR(50) NOT NULL,           -- created, updated, skipped, failed, removed, photo_updated, photo_failed, stale_detected
    field_changes       JSONB,                          -- { "field": { "old": "x", "new": "y" } }
    error_message       TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================
-- INDEXES
-- ============================================================

CREATE INDEX idx_tunnels_status ON tunnels(status);
CREATE INDEX idx_contact_sync_state_source ON contact_sync_state(source_user_id);
CREATE INDEX idx_contact_sync_state_target ON contact_sync_state(target_mailbox_id);
CREATE INDEX idx_contact_sync_state_list ON contact_sync_state(phone_list_id);
CREATE INDEX idx_contact_sync_state_stale ON contact_sync_state(is_stale) WHERE is_stale = TRUE;
CREATE INDEX idx_contact_sync_state_composite ON contact_sync_state(source_user_id, phone_list_id, target_mailbox_id);
CREATE INDEX idx_sync_runs_status ON sync_runs(status);
CREATE INDEX idx_sync_runs_started ON sync_runs(started_at DESC);
CREATE INDEX idx_sync_run_items_run ON sync_run_items(sync_run_id);
CREATE INDEX idx_sync_run_items_tunnel ON sync_run_items(tunnel_id);
CREATE INDEX idx_source_users_entra ON source_users(entra_id);
CREATE INDEX idx_source_users_enabled ON source_users(is_enabled);
CREATE INDEX idx_target_mailboxes_entra ON target_mailboxes(entra_id);
```

---

## 5. Seed Data

### App Settings

```sql
INSERT INTO app_settings (key, value, description) VALUES
('sync_schedule_cron', '0 */4 * * *', 'Sync runs every 4 hours'),
('photo_sync_mode', 'included', 'included | separate_pass | disabled'),
('batch_size', '50', 'Contacts per batch for Graph writes'),
('parallelism', '4', 'Concurrent target mailbox processing'),
('stale_policy_default', 'flag_hold', 'Default stale policy for new tunnels'),
('stale_hold_days_default', '14', 'Default hold period before auto-remove'),
('graph_tenant_id', '', 'Azure AD Tenant ID'),
('graph_client_id', '', 'Entra App Registration Client ID'),
('graph_client_secret', '', 'Entra App Registration Client Secret (use Key Vault in production)');
```

### Default Field Profile

```sql
INSERT INTO field_profiles (name, description, is_default) VALUES
('Default', 'Standard field sync profile for all office tunnels', TRUE);

-- Get the ID (will be 1)
INSERT INTO field_profile_fields (field_profile_id, field_name, field_section, display_name, behavior, display_order) VALUES
-- Identity
(1, 'DisplayName',     'Identity',     'Display Name',     'always', 1),
(1, 'GivenName',       'Identity',     'First Name',       'always', 2),
(1, 'Surname',         'Identity',     'Last Name',        'always', 3),
(1, 'JobTitle',        'Identity',     'Job Title',        'always', 4),
(1, 'CompanyName',     'Identity',     'Company',          'always', 5),
-- Contact Info
(1, 'EmailAddresses',  'Contact Info', 'Email',            'always', 10),
(1, 'BusinessPhones',  'Contact Info', 'Business Phone',   'always', 11),
(1, 'MobilePhone',     'Contact Info', 'Mobile Phone',     'always', 12),
(1, 'HomeFax',         'Contact Info', 'Fax',              'nosync', 13),
-- Address
(1, 'BusinessStreet',  'Address',      'Business Street',  'always', 20),
(1, 'BusinessCity',    'Address',      'Business City',    'always', 21),
(1, 'BusinessState',   'Address',      'Business State',   'always', 22),
(1, 'BusinessPostalCode','Address',    'Business Zip',     'always', 23),
(1, 'HomeAddress',     'Address',      'Home Address',     'nosync', 24),
-- Organization
(1, 'OfficeLocation',  'Organization', 'Office Location',  'always', 30),
(1, 'Department',      'Organization', 'Department',       'add_missing', 31),
(1, 'Manager',         'Organization', 'Manager',          'nosync', 32),
-- Extras
(1, 'PersonalNotes',   'Extras',       'Notes',            'add_missing', 40),
(1, 'Birthday',        'Extras',       'Birthday',         'nosync', 41),
(1, 'NickName',        'Extras',       'Nickname',         'nosync', 42),
-- Photo
(1, 'Photo',           'Photo',        'Contact Photo',    'always', 50);
```

### Seed Tunnels (matches current CiraSync config)

```sql
-- Phone Lists
INSERT INTO phone_lists (name, description) VALUES
('All Atlanta Fine Homes', 'All AFH and MSIR contacts combined'),
('All Mountain', 'All Mountain SIR contacts'),
('AFHSIR', 'AFH Sotheby''s agents only'),
('MSIR', 'Mountain SIR contacts only'),
('Avalon Gate Code', 'Gate access codes for Avalon community');

-- Tunnels
INSERT INTO tunnels (name, source_type, source_identifier, source_display_name, target_scope, field_profile_id, stale_policy, stale_hold_days, status) VALUES
('Buckhead',       'ddg', 'buckhead-ddg@atlantafinehomes.com',       'Buckhead Office DDG',       'all_users', 1, 'flag_hold', 14, 'active'),
('North Atlanta',  'ddg', 'northatlanta-ddg@atlantafinehomes.com',   'North Atlanta Office DDG',  'all_users', 1, 'flag_hold', 14, 'active'),
('Intown',         'ddg', 'intown-ddg@atlantafinehomes.com',         'Intown Office DDG',         'all_users', 1, 'flag_hold', 14, 'active'),
('Blue Ridge',     'ddg', 'blueridge-ddg@atlantafinehomes.com',      'Blue Ridge Office DDG',     'all_users', 1, 'flag_hold', 14, 'active'),
('Cobb',           'ddg', 'cobb-ddg@atlantafinehomes.com',           'Cobb Office DDG',           'all_users', 1, 'flag_hold', 14, 'active'),
('Clayton',        'ddg', 'clayton-ddg@atlantafinehomes.com',        'Clayton Office DDG',        'all_users', 1, 'flag_hold', 14, 'active');

-- Tunnel -> Phone List mappings (each office tunnel targets both lists)
INSERT INTO tunnel_phone_lists (tunnel_id, phone_list_id) VALUES
(1, 1), (1, 2),  -- Buckhead -> All AFH, All Mountain
(2, 1), (2, 2),  -- North Atlanta -> All AFH, All Mountain
(3, 1), (3, 2),  -- Intown -> All AFH, All Mountain
(4, 1), (4, 2),  -- Blue Ridge -> All AFH, All Mountain
(5, 1), (5, 2),  -- Cobb -> All AFH, All Mountain
(6, 1), (6, 2);  -- Clayton -> All AFH, All Mountain
```

---

## 6. API Specification

### Base URL: `/api`

### 6.1 Dashboard

#### GET /api/dashboard

Returns system overview metrics.

```json
{
  "activeTunnels": 6,
  "totalPhoneLists": 5,
  "totalTargetUsers": 776,
  "lastSync": {
    "id": 42,
    "status": "success",
    "startedAt": "2026-04-03T14:00:00Z",
    "durationMs": 252000,
    "contactsCreated": 0,
    "contactsUpdated": 12,
    "contactsRemoved": 0,
    "photosUpdated": 3
  },
  "warnings": [
    { "type": "tunnel_warning", "tunnelId": 5, "message": "Cobb: 2 contacts failed on last run" }
  ],
  "recentRuns": [
    { "id": 42, "runType": "scheduled", "status": "success", "startedAt": "...", "durationMs": 252000, "contactsUpdated": 12 }
  ]
}
```

### 6.2 Tunnels

#### GET /api/tunnels

Returns all tunnels with summary info.

```json
[
  {
    "id": 1,
    "name": "Buckhead",
    "sourceType": "ddg",
    "sourceIdentifier": "buckhead-ddg@atlantafinehomes.com",
    "sourceDisplayName": "Buckhead Office DDG",
    "targetScope": "all_users",
    "status": "active",
    "stalePolicy": "flag_hold",
    "staleDays": 14,
    "fieldProfileId": 1,
    "fieldProfileName": "Default",
    "targetLists": [
      { "id": 1, "name": "All Atlanta Fine Homes" },
      { "id": 2, "name": "All Mountain" }
    ],
    "estimatedContacts": 318,
    "estimatedTargetUsers": 776,
    "lastSync": {
      "status": "success",
      "completedAt": "2026-04-03T14:04:12Z",
      "contactsUpdated": 4
    }
  }
]
```

#### GET /api/tunnels/:id

Returns full tunnel detail including source contacts preview.

#### POST /api/tunnels

Creates a new tunnel.

```json
{
  "name": "Buckhead",
  "sourceType": "ddg",
  "sourceIdentifier": "buckhead-ddg@atlantafinehomes.com",
  "sourceDisplayName": "Buckhead Office DDG",
  "targetScope": "all_users",
  "targetListIds": [1, 2],
  "fieldProfileId": 1,
  "stalePolicy": "flag_hold",
  "staleDays": 14
}
```

#### PUT /api/tunnels/:id

Updates an existing tunnel. Same body as POST.

#### PUT /api/tunnels/:id/status

Activate or deactivate a tunnel.

```json
{ "status": "inactive" }
```

#### DELETE /api/tunnels/:id

Deletes a tunnel. Returns 409 if tunnel has active sync state (must deactivate first).

#### GET /api/tunnels/:id/impact

Returns preview impact analysis for a tunnel change.

```json
{
  "tunnelId": 1,
  "currentSourceContacts": 318,
  "newSourceContacts": 142,
  "targetLists": 2,
  "targetUsers": 776,
  "estimatedCreates": 0,
  "estimatedUpdates": 142,
  "estimatedRemovals": 176,
  "estimatedPhotoUpdates": 0,
  "totalWriteOperations": 220688
}
```

### 6.3 Phone Lists

#### GET /api/phone-lists

```json
[
  {
    "id": 1,
    "name": "All Atlanta Fine Homes",
    "contactCount": 652,
    "userCount": 776,
    "sourceTunnels": [
      { "id": 1, "name": "Buckhead" },
      { "id": 2, "name": "North Atlanta" }
    ],
    "lastSyncStatus": "success"
  }
]
```

#### GET /api/phone-lists/:id

Returns full list detail with sample contacts.

#### GET /api/phone-lists/:id/contacts?page=1&pageSize=20

Returns paginated contacts for a specific list (for phone preview).

### 6.4 Field Profiles

#### GET /api/field-profiles

Returns all profiles.

#### GET /api/field-profiles/:id

Returns full profile with all field settings.

```json
{
  "id": 1,
  "name": "Default",
  "isDefault": true,
  "sections": [
    {
      "name": "Identity",
      "fields": [
        { "fieldName": "DisplayName", "displayName": "Display Name", "behavior": "always" },
        { "fieldName": "GivenName", "displayName": "First Name", "behavior": "always" }
      ]
    }
  ]
}
```

#### PUT /api/field-profiles/:id

Updates field behaviors.

```json
{
  "fields": [
    { "fieldName": "DisplayName", "behavior": "always" },
    { "fieldName": "HomeFax", "behavior": "nosync" }
  ]
}
```

### 6.5 Sync Runs

#### GET /api/sync-runs?page=1&pageSize=20

Returns paginated run history.

#### GET /api/sync-runs/:id

Returns full run detail with item-level breakdown.

```json
{
  "id": 42,
  "runType": "scheduled",
  "status": "success",
  "isDryRun": false,
  "startedAt": "2026-04-03T14:00:00Z",
  "completedAt": "2026-04-03T14:04:12Z",
  "durationMs": 252000,
  "tunnelsProcessed": 6,
  "tunnelsWarned": 1,
  "contactsCreated": 0,
  "contactsUpdated": 12,
  "contactsRemoved": 0,
  "contactsSkipped": 782,
  "contactsFailed": 0,
  "photosUpdated": 3,
  "throttleEvents": 0,
  "tunnelSummaries": [
    { "tunnelId": 1, "tunnelName": "Buckhead", "created": 0, "updated": 4, "skipped": 314, "failed": 0 }
  ],
  "failures": [],
  "warnings": [
    { "tunnelId": 5, "message": "2 contacts could not be updated: mailbox unavailable" }
  ]
}
```

#### POST /api/sync-runs

Starts a new sync run.

```json
{
  "runType": "manual",
  "isDryRun": false,
  "tunnelIds": null
}
```

- `tunnelIds`: null = all active tunnels, or array of specific tunnel IDs
- Returns the created run ID immediately. Run executes async.

#### GET /api/sync-runs/:id/items?page=1&pageSize=50&action=failed

Returns paginated run items with optional action filter.

### 6.6 Settings

#### GET /api/settings

Returns all settings as key-value pairs.

#### PUT /api/settings

Updates one or more settings.

```json
{
  "settings": [
    { "key": "sync_schedule_cron", "value": "0 */2 * * *" },
    { "key": "photo_sync_mode", "value": "separate_pass" }
  ]
}
```

### 6.7 Graph Proxy (for DDG lookups from frontend)

#### GET /api/graph/ddgs

Returns all Dynamic Distribution Groups from the tenant.

```json
[
  {
    "id": "abc-123",
    "displayName": "Buckhead",
    "recipientFilter": "(Office -eq 'Buckhead') -and (CustomAttribute2 -eq 'AFH')",
    "recipientFilterPlain": "Office = Buckhead AND Brand = AFH",
    "memberCount": 318,
    "type": "Office"
  }
]
```

#### GET /api/graph/ddgs/:id/members?top=10

Returns sample members of a DDG for preview.

#### GET /api/graph/contact-folders

Returns available shared contact folders for target list selection.

---

## 7. Frontend Specification

### Technology

- **Framework**: Next.js 14+ with App Router
- **Language**: TypeScript
- **Styling**: Tailwind CSS with custom theme
- **State**: React hooks (useState, useEffect, useContext)
- **API calls**: fetch with custom wrapper in `lib/api.ts`

### Design System

#### Colors (Sotheby's-aligned)

```css
--navy: #1B2A4A;          /* Primary brand, headings, sidebar */
--navy-hover: #2d4170;    /* Button hover */
--gold: #C9A84C;          /* Accent, active states */
--gold-soft: #d4b96a;     /* Gold hover */
--gold-bg: rgba(201,168,76,0.08);  /* Gold tint backgrounds */
--bg: #FAF9F7;            /* Page background */
--card: #FFFFFF;           /* Card backgrounds */
--border: #E8E4DE;        /* Borders */
--text: #2C2C2C;          /* Body text */
--text-secondary: #6B6560; /* Secondary text */
--text-muted: #9B9590;    /* Muted labels */
--success: #2E7D32;       /* Green status */
--warning: #E65100;       /* Orange warnings */
--danger: #C62828;        /* Red errors */
--blue: #1565C0;          /* Info/active status */
```

#### Typography

```css
/* Headings */
font-family: 'Cormorant Garamond', serif;

/* Body */
font-family: 'DM Sans', sans-serif;
```

#### Key UX Patterns

1. **Phone Preview**: Every page that shows contacts includes an iPhone frame preview showing exactly what users see
2. **Live Preview**: Field mapping changes update the contact card preview in real time
3. **Impact Preview**: Tunnel edits show affected contacts/users/lists before saving
4. **Confirmation Dialogs**: High-impact changes (source swap, target removal, disable, delete) require confirmation
5. **Plain Language**: Field behaviors use "Always keep updated" not "Overwrite"
6. **DDG Picker**: Source selection includes search, type filters (Office/Role/Brand), live member counts, and plain-language recipient filter display

### Page Specifications

#### Dashboard
- 4 KPI cards: active tunnels, phone lists, target users, last sync
- Active tunnel summary (clickable to detail)
- Recent runs list (last 5)
- "Run Sync Now" and "Dry Run" buttons in topbar

#### Tunnels List
- Table: name, source DDG, contacts, target lists, users, last run, status
- Rows clickable to detail
- "Add Tunnel" button

#### Tunnel Detail (View + Edit Mode)
- View mode: KPI cards (source DDG, contacts, target lists, users), target list cards, sample contacts, danger zone (disable/delete)
- Edit mode: name input, DDG picker with search, target list checkboxes, stale policy selector, live impact diff
- Phone preview sidebar showing source contacts
- Confirmation dialog on save for high-impact changes

#### Create Tunnel Modal
- 4-step wizard: Name → Source DDG → Target Lists → Review
- DDG step has search bar and type filter pills
- Review step shows source→target summary and estimated write count
- Steps are clickable to go back

#### Lists on Phones
- Left side: list of phone-visible lists (clickable)
- Right side: iPhone preview showing contacts for selected list
- Click contact in phone to see full contact card detail

#### Field Mapping
- Left side: grouped field rows with behavior dropdown selectors
- Right side: iPhone contact card preview that updates as behaviors change
- Sections: Identity, Contact Info, Address, Organization, Extras, Photo

#### Runs & Logs
- Table: timestamp, type, duration, created/updated/removed/photos/skipped, status
- Clickable rows to run detail
- Run detail: tunnel summaries, failure list, warning list

#### Settings
- Sync schedule (cron dropdown)
- Photo sync mode
- Batch size
- Stale contact policy defaults
- Parallelism

---

## 8. Sync Engine Specification

### Sync Pipeline (SyncOrchestrator)

The sync engine runs as a BackgroundService in the worker container. It can be triggered manually via API or on schedule via cron.

```
1. Create SyncRun record (status: running)
2. Resolve active tunnels
3. For each tunnel:
   a. Resolve source membership
      - DDG: Call Exchange/Graph to expand DDG recipient filter
      - Mailbox: Call Graph to read contact folder
   b. If member count = 0, warn and skip tunnel (do NOT delete existing synced contacts)
   c. Apply source filters (exclude disabled, shared mailboxes, service accounts)
   d. Resolve target mailboxes
      - all_users: Query all enabled user mailboxes
      - specific_users: Use target_user_filter list
   e. Resolve target phone lists (from tunnel_phone_lists)
   f. For each target mailbox (parallel, bounded by semaphore):
      For each target phone list:
        For each source contact:
          i.   Build normalized contact payload from source fields + field profile
          ii.  Compute SHA-256 data hash of payload
          iii. Look up existing contact_sync_state record
          iv.  Compare hashes:
               - No existing record → CREATE contact via Graph, store sync state
               - Hash changed → UPDATE contact via Graph, store previous hash, update sync state
               - Hash same → SKIP
               - Source contact no longer in DDG → Mark STALE per policy
          v.   If photo behavior is 'always':
               - Compute photo hash
               - Compare against stored photo_hash
               - If changed → UPDATE photo via Graph
   g. Handle stale contacts per tunnel's stale_policy:
      - auto_remove: DELETE from Graph, remove sync state
      - flag_hold: Mark is_stale=true, set stale_detected_at
        - If stale_detected_at + stale_hold_days < now → DELETE and remove
      - leave: Do nothing
   h. Log tunnel summary to sync_run_items
4. Finalize SyncRun record (status, counts, duration)
```

### Delta Logic

The core performance optimization. Never rewrite unchanged contacts.

```csharp
// Build normalized payload (sorted keys, trimmed values)
var payload = new SortedDictionary<string, string>
{
    ["DisplayName"] = source.DisplayName?.Trim(),
    ["GivenName"] = source.GivenName?.Trim(),
    ["Surname"] = source.Surname?.Trim(),
    // ... all fields where behavior != nosync
};

// Compute hash
var json = JsonSerializer.Serialize(payload);
var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
var dataHash = Convert.ToHexString(hash);

// Compare
var existing = await db.ContactSyncStates
    .FirstOrDefaultAsync(s =>
        s.SourceUserId == sourceUser.Id &&
        s.PhoneListId == phoneList.Id &&
        s.TargetMailboxId == targetMailbox.Id);

if (existing == null)
    // CREATE
else if (existing.DataHash != dataHash)
    // UPDATE (store previous hash first)
else
    // SKIP
```

### Field Behavior Application

When building the contact payload, each field is filtered by its behavior:

```
nosync       → Field excluded from payload entirely
add_missing  → Include only if sync state has no prior value for this field
always       → Always include current value
remove_blank → Include current value; if blank, explicitly clear the field on the contact
```

### Photo Sync

```
1. Fetch photo from Graph: GET /users/{id}/photo/$value
2. Compute SHA-256 of photo bytes
3. Compare against source_users.photo_hash
4. If changed:
   a. Update source_users.photo_hash
   b. For each contact_sync_state where this source user has a synced contact:
      - PUT photo to Graph: PUT /users/{mailboxId}/contacts/{contactId}/photo/$value
      - Update contact_sync_state.photo_hash
      - Log to sync_run_items
```

### Throttle Handling

```csharp
public class ThrottleHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        int retries = 0;
        while (retries < 5)
        {
            var response = await base.SendAsync(request, ct);

            if (response.StatusCode == (HttpStatusCode)429)
            {
                retries++;
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Pow(2, retries));

                // Add jitter
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
                await Task.Delay(retryAfter + jitter, ct);

                // Log throttle event
                _logger.LogWarning("Graph throttled. Retry {n} after {delay}s", retries, retryAfter.TotalSeconds);
                continue;
            }

            return response;
        }

        throw new Exception("Graph API throttle limit exceeded after 5 retries");
    }
}
```

### Parallelism Model

```csharp
var semaphore = new SemaphoreSlim(parallelism); // Default: 4

var tasks = targetMailboxes.Select(async mailbox =>
{
    await semaphore.WaitAsync(ct);
    try
    {
        await ProcessMailbox(mailbox, sourceContacts, phoneLists, fieldProfile, run, ct);
    }
    finally
    {
        semaphore.Release();
    }
});

await Task.WhenAll(tasks);
```

### Source Filtering

Applied after DDG membership resolution, before sync processing:

```csharp
public IEnumerable<SourceUser> ApplyFilters(IEnumerable<SourceUser> users)
{
    return users.Where(u =>
        u.IsEnabled &&                                    // Exclude disabled accounts
        u.MailboxType == "UserMailbox" &&                 // Exclude shared/room/equipment
        !IsServiceAccount(u) &&                           // Exclude service accounts
        !string.IsNullOrEmpty(u.Email)                   // Must have email
    );
}

private bool IsServiceAccount(SourceUser u)
{
    // Match by naming convention or OU
    var servicePatterns = new[] { "svc_", "service.", "admin_", "noreply" };
    return servicePatterns.Any(p =>
        u.Email?.StartsWith(p, StringComparison.OrdinalIgnoreCase) == true);
}
```

---

## 9. Microsoft Graph Integration

### Entra App Registration

#### Required Application Permissions

| Permission | Type | Purpose |
|-----------|------|---------|
| User.Read.All | Application | Read user profiles, attributes, photos, office field |
| Group.Read.All | Application | Read DDG definitions and filters |
| GroupMember.Read.All | Application | Enumerate group members |
| Contacts.ReadWrite | Application | Create, update, delete contacts in target mailboxes |
| MailboxSettings.Read | Application | Verify target mailbox reachability |

#### Optional (for DDG filter resolution via Exchange)

| Permission | Type | Purpose |
|-----------|------|---------|
| Exchange.ManageAsApp | Application role | Run Exchange Online PowerShell for Get-Recipient -RecipientPreviewFilter |

### Graph API Calls

#### Read Users

```
GET /users?$select=id,displayName,givenName,surname,mail,businessPhones,
mobilePhone,jobTitle,department,officeLocation,companyName,streetAddress,
city,state,postalCode,country,accountEnabled,userType
&$filter=accountEnabled eq true and userType eq 'Member'
&$top=999
```

#### Read User Photo

```
GET /users/{userId}/photo/$value
→ Returns binary image data
→ 404 if no photo
```

#### Read DDG Members (via Exchange Online PowerShell)

```powershell
$ddg = Get-DynamicDistributionGroup -Identity "buckhead-ddg@atlantafinehomes.com"
Get-Recipient -RecipientPreviewFilter $ddg.RecipientFilter -ResultSize Unlimited
```

#### Create Contact in Mailbox

```
POST /users/{mailboxId}/contactFolders/{folderId}/contacts
{
  "givenName": "Jenny",
  "surname": "Pruitt",
  "displayName": "Jenny Pruitt",
  "jobTitle": "Broker-Owner",
  "companyName": "Atlanta Fine Homes SIR",
  "emailAddresses": [{ "address": "jenny.pruitt@atlantafinehomes.com", "name": "Jenny Pruitt" }],
  "businessPhones": ["(404) 237-5000"],
  "mobilePhone": "(404) 555-0101",
  "officeLocation": "Buckhead",
  "businessAddress": {
    "street": "3290 Northside Pkwy NW Suite 200",
    "city": "Atlanta",
    "state": "GA",
    "postalCode": "30327"
  }
}
```

#### Update Contact

```
PATCH /users/{mailboxId}/contacts/{contactId}
{ ...changed fields only... }
```

#### Update Contact Photo

```
PUT /users/{mailboxId}/contacts/{contactId}/photo/$value
Content-Type: image/jpeg
[binary photo data]
```

#### Delete Contact

```
DELETE /users/{mailboxId}/contacts/{contactId}
```

#### List Contact Folders in Mailbox

```
GET /users/{mailboxId}/contactFolders
```

#### Health Check

```
GET /organization
→ Validates app has access to the tenant
```

---

## 10. Authentication

### Admin Auth (v1 - Simple)

For v1, use Microsoft Entra ID authentication via MSAL:

1. Frontend redirects to Microsoft login
2. User authenticates with their AFH Entra ID account
3. Backend validates the JWT token
4. Check that the user's email is in an allowed admin list (app_settings)

```sql
INSERT INTO app_settings (key, value, description) VALUES
('admin_emails', 'nick.albano@atlantafinehomes.com,kevin.goldfinger@atlantafinehomes.com', 'Comma-separated list of admin email addresses');
```

### Alternative: Simple shared secret for v1

If Entra auth is too complex for v1, use a simple username/password stored in environment variables. The frontend sends credentials, the API issues a JWT token for subsequent requests.

---

## 11. Docker Configuration

### docker-compose.yml

```yaml
services:
  postgres:
    image: postgres:16
    container_name: afh-postgres
    restart: unless-stopped
    environment:
      POSTGRES_DB: afhsync
      POSTGRES_USER: afhsync
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./db/init.sql:/docker-entrypoint-initdb.d/init.sql:ro
    ports:
      - "127.0.0.1:5432:5432"
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U afhsync"]
      interval: 10s
      timeout: 5s
      retries: 5

  api:
    build: ./api
    container_name: afh-api
    restart: unless-stopped
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:8080
      - ConnectionStrings__Default=Host=postgres;Database=afhsync;Username=afhsync;Password=${POSTGRES_PASSWORD}
      - Graph__TenantId=${GRAPH_TENANT_ID}
      - Graph__ClientId=${GRAPH_CLIENT_ID}
      - Graph__ClientSecret=${GRAPH_CLIENT_SECRET}
      - Auth__JwtSecret=${JWT_SECRET}
    depends_on:
      postgres:
        condition: service_healthy
    ports:
      - "127.0.0.1:5000:8080"

  worker:
    build: ./worker
    container_name: afh-worker
    restart: unless-stopped
    environment:
      - DOTNET_ENVIRONMENT=Production
      - ConnectionStrings__Default=Host=postgres;Database=afhsync;Username=afhsync;Password=${POSTGRES_PASSWORD}
      - Graph__TenantId=${GRAPH_TENANT_ID}
      - Graph__ClientId=${GRAPH_CLIENT_ID}
      - Graph__ClientSecret=${GRAPH_CLIENT_SECRET}
    depends_on:
      postgres:
        condition: service_healthy

  frontend:
    build: ./frontend
    container_name: afh-frontend
    restart: unless-stopped
    environment:
      - NEXT_PUBLIC_API_URL=http://api:8080
    ports:
      - "127.0.0.1:3000:3000"
    depends_on:
      - api

  nginx:
    image: nginx:alpine
    container_name: afh-nginx
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./nginx/nginx.conf:/etc/nginx/nginx.conf:ro
      - ./nginx/certs:/etc/nginx/certs:ro
    depends_on:
      - frontend
      - api

volumes:
  pgdata:
```

### API Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["AFHSync.Api.csproj", "."]
COPY ["../shared/AFHSync.Shared.csproj", "../shared/"]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "AFHSync.Api.dll"]
```

### Worker Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["AFHSync.Worker.csproj", "."]
COPY ["../shared/AFHSync.Shared.csproj", "../shared/"]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "AFHSync.Worker.dll"]
```

### Frontend Dockerfile

```dockerfile
FROM node:20-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM node:20-alpine
WORKDIR /app
COPY --from=build /app/.next ./.next
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/package.json ./
COPY --from=build /app/public ./public
EXPOSE 3000
CMD ["npm", "start"]
```

### nginx.conf

```nginx
events {
    worker_connections 1024;
}

http {
    upstream frontend {
        server frontend:3000;
    }

    upstream api {
        server api:8080;
    }

    server {
        listen 80;
        server_name _;

        client_max_body_size 10M;

        # API routes
        location /api/ {
            proxy_pass http://api/api/;
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
            proxy_read_timeout 300s;
        }

        # Frontend
        location / {
            proxy_pass http://frontend;
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection "upgrade";
        }
    }
}
```

---

## 12. Environment Variables

### .env file (on VM at ~/afh-sync/.env)

```bash
# Database
POSTGRES_PASSWORD=<generated-strong-password>

# Microsoft Graph
GRAPH_TENANT_ID=<your-entra-tenant-id>
GRAPH_CLIENT_ID=<app-registration-client-id>
GRAPH_CLIENT_SECRET=<app-registration-client-secret>

# Auth
JWT_SECRET=<generated-strong-secret>

# Optional
LOG_LEVEL=Information
```

Generate secure values:

```bash
openssl rand -base64 24  # For POSTGRES_PASSWORD
openssl rand -base64 32  # For JWT_SECRET
```

---

## 13. Deployment

### Initial Setup (already done)

```bash
ssh sync
sudo apt update && sudo apt upgrade -y
sudo apt install docker.io docker-compose-v2 -y
sudo usermod -aG docker nalbano
# logout and back in
```

### Deploy Application

```bash
cd ~/afh-sync
# Clone or pull from GitHub
git clone https://github.com/<your-org>/afh-sync.git .

# Set environment variables
nano .env

# Build and start
docker compose build
docker compose up -d

# Verify
docker compose ps
docker compose logs -f

# Run database migrations
docker compose exec api dotnet ef database update
# OR run init.sql directly:
docker compose exec -T postgres psql -U afhsync -d afhsync < db/init.sql
```

### Update Application

```bash
cd ~/afh-sync
git pull
docker compose build
docker compose up -d
```

### View Logs

```bash
docker compose logs -f api        # API logs
docker compose logs -f worker     # Sync engine logs
docker compose logs -f frontend   # Frontend logs
docker compose logs -f postgres   # Database logs
```

### Database Backup (cron job)

```bash
# Add to crontab: crontab -e
0 2 * * * docker compose -f ~/afh-sync/docker-compose.yml exec -T postgres pg_dump -U afhsync afhsync | gzip > ~/backups/afhsync_$(date +\%Y\%m\%d).sql.gz

# Create backup directory
mkdir -p ~/backups
```

### Monitor Resources

```bash
docker stats            # Container CPU/memory
htop                    # System resources
df -h                   # Disk usage
```

---

## Environment-Specific Constants

These are the known attribute values from the AFH environment audit:

| Attribute | Exchange Equivalent | Values |
|-----------|-------------------|--------|
| extensionAttribute1 | CustomAttribute1 | Reserved (ClaytonCalendar / listings) — DO NOT USE |
| extensionAttribute2 | CustomAttribute2 | AFH, MSIR |
| extensionAttribute3 | CustomAttribute3 | Advisor, Staff |
| extensionAttribute4 | CustomAttribute4 | Marketer, Accounting, Design, etc. |
| Office | Office | Buckhead, North Atlanta, Intown, Blue Ridge, Cobb, Clayton |

### Known User Counts

| Segment | Count |
|---------|-------|
| AFH users | ~723 |
| MSIR users | ~149 |
| Advisors | ~594 |
| Staff | ~231 |
| Total target mailboxes | ~776 |

### Current CiraSync Config (to replicate)

| Tunnel | Source DDG | Source Count | Target Lists | Target Users |
|--------|-----------|-------------|-------------|-------------|
| Blue Ridge | Blue Ridge | 121 | All AFH; All Mountain | 776 |
| Buckhead | Buckhead | 318 | All AFH; All Mountain | 776 |
| Clayton | Clayton | 17 | All AFH; All Mountain | 776 |
| Cobb | Cobb | 67 | All AFH; All Mountain | 776 |
| Intown | Intown | 129 | All AFH; All Mountain | 776 |
| North Atlanta | North Atlanta | ~142 | All AFH; All Mountain | 776 |
| DB Personal | David@Atlantafinehomes.com\Contacts | 2,945 | Jessica Curran | 1 |

---

## Build Order (Speed-Optimized)

### Sprint 1: Foundation (Week 1–2)
- [x] Set up Azure VM with Docker Compose
- [ ] Scaffold Next.js frontend with nav shell
- [ ] Scaffold ASP.NET Core API with health endpoint
- [ ] Create PostgreSQL schema (run init.sql)
- [ ] Register Entra app with Graph permissions
- [ ] Build Graph client wrapper
- [ ] Seed tunnel data from CiraSync config

### Sprint 2: Sync Engine (Week 3–4)
- [ ] DDG membership resolver
- [ ] Source user filtering
- [ ] Contact payload normalizer + SHA-256 hashing
- [ ] Delta comparison against contact_sync_state
- [ ] Contact writer (Graph create/update/skip)
- [ ] Throttle handler with backoff
- [ ] Sync run logging
- [ ] Dry-run mode
- [ ] Test with Clayton tunnel (smallest, 17 contacts)

### Sprint 3: Photos & Stale (Week 5)
- [ ] Photo hash comparison + conditional write
- [ ] Separate photo sync pass option
- [ ] Stale contact detection
- [ ] Stale policies (auto-remove, flag-hold, leave)
- [ ] Full sync test across all tunnels

### Sprint 4: Admin UI (Week 6–7)
- [ ] Dashboard with KPIs and recent runs
- [ ] Tunnel list + detail with edit mode
- [ ] Create tunnel wizard with DDG picker
- [ ] Lists on Phones with iPhone preview
- [ ] Field Mapping with live contact card preview
- [ ] Runs & Logs table + detail
- [ ] Settings page

### Sprint 5: Hardening (Week 8)
- [ ] Scheduled sync via cron
- [ ] Safe editing: preview impact + confirmation dialogs
- [ ] Error handling + partial failure resilience
- [ ] Admin auth (Entra or simple JWT)
- [ ] Production deployment + CiraSync cutover
