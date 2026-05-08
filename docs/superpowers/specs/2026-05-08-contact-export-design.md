# Contact Export — Design

**Date:** 2026-05-08
**Status:** Approved (design)
**Owner:** Nick

## Goal

Let an admin download an Excel workbook listing every contact currently being synced, broken down by tunnel. One sheet per tunnel.

## Why

Today there is no way to see, in one view, "which contacts are actually landing on phones, by tunnel." Admins want a portable artifact (Excel) for spot-checks, audits, and sharing with stakeholders without giving them admin UI access.

## Scope

### In scope
- Single `.xlsx` download containing one sheet per tunnel
- Each sheet lists the distinct source contacts currently synced through that tunnel (live, non-stale)
- Core contact fields only
- Sidebar entry + dedicated page that triggers the download
- Synchronous generation and streaming response

### Out of scope (v1)
- Date range / filter controls
- Per-tunnel export from the row dropdown
- Async/email delivery
- Including stale or hard-removed contacts
- Including the target mailbox / phone list breakdown (which mailboxes received the contact)

## Data model

The "contacts actually being synced" is computed from existing tables:

- `Tunnel` — one row per tunnel
- `ContactSyncState (TunnelId, SourceUserId, IsStale, ...)` — the join table that records what was actually written
- `SourceUser` — the contact fields

A contact is "live in tunnel T" iff there exists a `ContactSyncState` row with `TunnelId = T.Id` and `IsStale = false`. The sheet for tunnel T lists the **distinct** `SourceUser`s satisfying that.

## Backend

### New endpoint
`GET /api/exports/contacts.xlsx`

- Auth: same JWT auth as other API routes (uses existing controller conventions; no new auth path)
- Response: `FileStreamResult`
  - Content-Type: `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`
  - `Content-Disposition: attachment; filename="afh-sync-contacts-{yyyy-MM-dd}.xlsx"` (UTC date) — set explicitly so the frontend can read it back
- Errors: 500 with `{ message }` JSON body if generation fails

### New controller
`api/Controllers/ExportsController.cs`. Thin — delegates to the service, sets headers, streams the bytes.

### New service
`api/Services/IContactExportService` + `ContactExportService`.

```csharp
public interface IContactExportService
{
    Task<byte[]> BuildContactsWorkbookAsync(CancellationToken ct);
}
```

Logic:
1. Load all tunnels (any status — see Open Questions) ordered by name.
2. Single grouped EF query: for each tunnel, the distinct `SourceUser` records where `ContactSyncState.TunnelId = tunnel.Id` and `IsStale = false`. Project only the columns we need.
3. Build a `ClosedXML.Excel.XLWorkbook` with one sheet per tunnel (in the same order):
   - Sheet name = `SanitizeSheetName(tunnel.Name)`
   - Row 1: bold header row, frozen
   - Rows 2+: contact rows, sorted by Display Name
   - `worksheet.Columns().AdjustToContents()` after population
4. Save the workbook to a `MemoryStream`, return its byte array.

DI: register the service as scoped in `Program.cs`.

### Sheet schema (columns, in order)
| Column         | Source                       |
|----------------|------------------------------|
| Display Name   | `SourceUser.DisplayName`     |
| First Name     | `SourceUser.FirstName`       |
| Last Name      | `SourceUser.LastName`        |
| Email          | `SourceUser.Email`           |
| Job Title      | `SourceUser.JobTitle`        |
| Department     | `SourceUser.Department`      |
| Office         | `SourceUser.OfficeLocation`  |
| Business Phone | `SourceUser.BusinessPhone`   |
| Mobile Phone   | `SourceUser.MobilePhone`     |
| Company        | `SourceUser.CompanyName`     |

Empty tunnels get a sheet with just the header row so the breakdown is complete.

### Sheet-name sanitization
Excel sheet name rules: ≤ 31 chars, no `: \ / ? * [ ]`, can't be empty, must be unique in the workbook.

```csharp
static string SanitizeSheetName(string name)
{
    var cleaned = Regex.Replace(name, @"[:\\/\?\*\[\]]", "-");
    if (cleaned.Length > 31) cleaned = cleaned[..31];
    if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Tunnel";
    return cleaned;
}
```

If sanitization produces a duplicate, append ` (2)`, ` (3)`, etc., respecting the 31-char limit.

### Dependency
Add to `api/AFHSync.Api.csproj`:
```xml
<PackageReference Include="ClosedXML" Version="0.105.*" />
```
ClosedXML is MIT-licensed, .NET 10 compatible, and already the de-facto choice for this kind of work.

## Frontend

### Sidebar entry
Add to `frontend/src/components/layout/Sidebar.tsx` nav array:
```ts
{ label: 'Export', href: '/exports', icon: Download }
```
Place it between "User Lookup" and "Cleanup" (admin-tools grouping).

### New page
`frontend/src/app/(app)/exports/page.tsx`

Layout:
- `PageHeader` — title "Export", subtitle "Download data from the sync platform."
- One card-style section: "Synced contacts by tunnel"
  - Short paragraph: "Downloads an Excel workbook with one sheet per tunnel. Each sheet lists the contacts currently being synced through that tunnel (live contacts only, excluding stale)."
  - Primary button: "Download .xlsx" (with `Download` icon and a loading spinner state)

Behavior:
- On click: `fetch('/api/exports/contacts.xlsx', { credentials: 'include' })`
- Response → `blob()` → create object URL → trigger browser download with the filename from the `Content-Disposition` header (fall back to `afh-sync-contacts.xlsx`)
- While in flight: button is disabled and shows a spinner
- On non-2xx: parse JSON error and show `toast.error(...)` (use existing `sonner` toaster)
- On success: `toast.success('Download started')`

The download helper can live inline in the page component for v1; promote to `lib/download.ts` only if a second downloader appears.

## Error handling

| Failure mode                              | Behavior                                                    |
|-------------------------------------------|-------------------------------------------------------------|
| DB unavailable                            | 500 + JSON error → toast on frontend                        |
| ClosedXML throws during build             | 500 + JSON error → toast on frontend                        |
| Tunnel has zero live contacts             | Sheet exists with header row only; not an error             |
| Tunnel name produces invalid sheet name   | Sanitized; duplicates suffixed with ` (2)`                   |
| User cancels (closes tab) mid-stream      | `CancellationToken` propagates; service abandons workbook   |

## Performance

Bounded: ~776 mailboxes total, but the export ranges over **distinct source users per tunnel**, not per mailbox. Distinct source users across the org is in the low thousands at most. ClosedXML can build a workbook this size in well under a second; memory usage is negligible. No streaming/chunking needed.

The single grouped EF query avoids N+1 across tunnels.

## Testing

- Unit test: `ContactExportServiceTests`
  - Builds a workbook against an InMemory DB seeded with two tunnels and a handful of contacts (one shared between tunnels, one stale, one live-only)
  - Asserts: sheet count = tunnel count; sheet names match (sanitized); stale contacts absent; shared contact appears in both sheets; column headers present; row counts correct
- Controller test (existing patterns): `GET /api/exports/contacts.xlsx` returns 200 with the right content type
- Manual smoke test: open the resulting file in Excel; verify columns auto-fit and header row is frozen+bold

## Open questions (intentionally deferred)

- **Active vs all tunnels:** v1 includes all tunnels regardless of `Status`. If admins find inactive tunnels noisy in the workbook we can filter to `Active` later.
- **Field Profile-aware columns:** v1 uses a fixed core set. If needed, a future enhancement can render the columns each tunnel actually pushes.

## File-touch summary

**New files:**
- `api/Controllers/ExportsController.cs`
- `api/Services/IContactExportService.cs`
- `api/Services/ContactExportService.cs`
- `frontend/src/app/(app)/exports/page.tsx`
- `tests/.../ContactExportServiceTests.cs` (under existing test project)

**Modified files:**
- `api/AFHSync.Api.csproj` — add ClosedXML
- `api/Program.cs` — register `IContactExportService`
- `frontend/src/components/layout/Sidebar.tsx` — add nav entry
