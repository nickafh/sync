<!-- GSD:project-start source:PROJECT.md -->
## Project

**AFH Sync**

AFH Sync is an internal IT application for Atlanta Fine Homes Sotheby's International Realty that manages delivery of shared company contact lists to users' Outlook accounts and mobile phones. It replaces CiraSync with a self-hosted, tunnel-based sync platform that routes contacts from Exchange Dynamic Distribution Groups (DDGs) through Microsoft Graph into phone-visible contact folders across ~776 target mailboxes.

**Core Value:** Every AFH employee sees up-to-date office contact lists on their phone without manual effort — contacts sync automatically, delta-only, with no duplicates or stale entries.

### Constraints

- **Tech stack**: Next.js 14+ (App Router, TypeScript, Tailwind), ASP.NET Core 8 (API + Worker), PostgreSQL 16, Docker Compose, nginx
- **Infrastructure**: Single Azure VM — no Kubernetes, no managed services beyond the VM
- **Graph API**: Application permissions only (no delegated), bounded by Microsoft throttling limits
- **Parallelism**: Semaphore-bounded concurrent mailbox processing (default 4)
- **Auth**: Simple JWT for v1 (username/password from env vars)
<!-- GSD:project-end -->

<!-- GSD:stack-start source:research/STACK.md -->
## Technology Stack

## Critical Decision: .NET 10 over .NET 8
- .NET 8 reaches end of support on **November 10, 2026** -- this project will still be in active development or early production by then
- .NET 10 is the current LTS (supported until November 2028), released November 2025
- Migration from 8 to 10 is trivial (change target framework moniker) -- starting on 10 avoids a forced mid-project upgrade
- 15% higher request throughput and significantly lower memory usage vs .NET 8
- All key dependencies (Microsoft.Graph SDK 5.x, Hangfire 1.8.x, Npgsql 10.x, Polly 8.x) are confirmed compatible with .NET 10
- C# 14 language features improve code quality (primary constructors refinements, extension types)
## Critical Decision: Next.js 15 over Next.js 16
- Next.js 16 introduced significant breaking changes: middleware renamed to "proxy", Turbopack as default bundler, async request APIs fully enforced, `next lint` removed
- Next.js 15 is in active LTS with continued security patches and bug fixes
- For an internal admin tool with ~5-10 pages, the new features in 16 (AI tooling, adapter API) provide zero value
- Stable, well-documented, massive ecosystem support -- every tutorial and Stack Overflow answer targets 15.x
- 14 is falling behind on security patches; 15 is the sweet spot
## Recommended Stack
### Core Framework
| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| .NET 10 | 10.0 (LTS) | Runtime for API + Worker | Current LTS, supported until Nov 2028. .NET 8 EOL is Nov 2026 -- too close for a greenfield project. | HIGH |
| ASP.NET Core 10 | 10.0 | Web API framework | Built-in DI, middleware pipeline, minimal APIs. Tight integration with Microsoft.Graph and Azure.Identity. | HIGH |
| Next.js | 15.x (latest patch) | Admin frontend | App Router, Server Components, stable LTS. Internal tool does not need bleeding-edge 16.x features. | HIGH |
| TypeScript | 5.x | Frontend language | Type safety for the admin UI. Use strict mode. | HIGH |
| Tailwind CSS | 4.x | Styling | Utility-first CSS. Matches the Sotheby's design system requirements (custom colors, fonts). Fast iteration for admin UI. | HIGH |
### Microsoft Graph Integration
| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| Microsoft.Graph | 5.103.0+ | Graph API client | Kiota-generated, strongly-typed SDK. Covers contacts, contact folders, users, groups, DDG membership. Pin to 5.x minor -- patch versions release biweekly. | HIGH |
| Azure.Identity | 1.20.0+ | Authentication | `ClientSecretCredential` for client credentials flow. Application permissions (no user interaction). Works seamlessly with `GraphServiceClient`. | HIGH |
### Database
| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| PostgreSQL | 16.x | Primary database | LTS, proven stability. The project spec already defines the schema. PG 17 is fine too but 16 has broader production track record. | HIGH |
| Npgsql | 10.0.2+ | ADO.NET driver | Direct PostgreSQL driver for .NET. Foundation for EF Core provider. Version 10.x aligns with .NET 10. | HIGH |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.1+ | EF Core provider | EF Core on top of Npgsql. See database access strategy below. | HIGH |
| EF Core | 10.0.x | ORM | Migrations, change tracking, LINQ queries. See rationale below. | HIGH |
- Schema migrations (critical for Docker deployment -- `dotnet ef database update` at startup)
- CRUD operations on tunnels, phone lists, field profiles, sync runs, settings
- Change tracking for admin UI operations
- Bulk hash comparison queries during sync (SELECT contact_hash WHERE mailbox_id = X)
- Batch inserts of sync log entries (thousands per run)
- Any query where EF Core's overhead matters (the sync engine hot path)
### Background Job Processing
| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| Hangfire.AspNetCore | 1.8.23+ | Job scheduling + execution | Cron-based recurring jobs (sync every 4 hours), manual trigger support, built-in retry with backoff, persistent job state in PostgreSQL, dashboard for monitoring. | HIGH |
| Hangfire.PostgreSql | 1.21.1+ | Hangfire storage backend | Uses the same PostgreSQL instance -- no additional infrastructure. Creates its own schema tables. | HIGH |
- Built-in dashboard (accessible at `/hangfire`) -- free monitoring for sync runs
- Fire-and-forget jobs for manual sync triggers
- Persistent job storage in PostgreSQL -- survives Docker restarts
- Simpler API: `RecurringJob.AddOrUpdate("sync-all", () => syncService.RunAsync(), "0 */4 * * *")`
- Quartz.NET is overkill -- we don't need clustering, calendar exclusions, or complex trigger chains
- `BackgroundService` has no persistence -- if the container restarts mid-sync, the job is lost
- No built-in retry semantics
- No dashboard or job history
- No cron scheduling without manual implementation
### Resilience & Retry
| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| Polly | 8.6.6+ | Retry policies, circuit breaker | Exponential backoff + jitter for Graph 429 responses. Integrates with `HttpClient` via `Microsoft.Extensions.Http.Polly`. | HIGH |
### Hashing
| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| System.Security.Cryptography (built-in) | .NET 10 BCL | SHA-256 delta hashing | Built into .NET. No external dependency needed. `SHA256.HashData(bytes)` is the modern API (static, allocation-free). | HIGH |
- Hardware-accelerated (uses CPU AES-NI/SHA extensions)
- Thread-safe via the static `HashData` method
- Zero allocation with `Span<byte>` overloads
### Logging
| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| Serilog | 4.3.1+ | Structured logging | Structured logging with property enrichment. Critical for sync run tracing (correlate by RunId, TunnelId, MailboxId). | HIGH |
| Serilog.AspNetCore | 10.0.0+ | ASP.NET Core integration | Request logging, DI integration, host builder extension. Version 10.x aligns with .NET 10. | HIGH |
| Serilog.Sinks.Console | latest | Console output | Docker logs via `docker logs` command. JSON-formatted for structured queries. | HIGH |
| Serilog.Sinks.PostgreSQL | latest | Database sink | Optional: write logs to PostgreSQL for the Runs & Logs UI. Alternative: write to a `sync_logs` table via EF Core and use Serilog only for console/file. | MEDIUM |
### Infrastructure / Docker
| Technology | Version | Purpose | Why | Confidence |
|------------|---------|---------|-----|------------|
| Docker Compose | v2 (compose.yaml) | Service orchestration | Single-file definition of all 5 services. Use `compose.yaml` (not `docker-compose.yml` -- the v1 filename is deprecated). | HIGH |
| mcr.microsoft.com/dotnet/aspnet:10.0 | 10.0 | API + Worker runtime image | Official Microsoft ASP.NET Core runtime. Use `10.0-noble` (Ubuntu 24.04) or `10.0-alpine` for smaller size. | HIGH |
| mcr.microsoft.com/dotnet/sdk:10.0 | 10.0 | Build stage only | Multi-stage build: SDK for `dotnet publish`, aspnet for runtime. Never ship the SDK image. | HIGH |
| node:22-alpine | 22 LTS | Frontend build + runtime | Node 22 is current LTS. Alpine for minimal image size. Use multi-stage build with `output: 'standalone'` in next.config. | HIGH |
| postgres:16-alpine | 16.x | Database | Official PostgreSQL image. Alpine variant is ~80MB vs ~400MB for Debian. PG 16 is stable LTS. | HIGH |
| nginx:1.27-alpine | 1.27.x | Reverse proxy | Routes `/api/*` to ASP.NET, `/*` to Next.js. Handles static file caching, gzip, request buffering. Alpine for minimal size. | HIGH |
### Supporting Libraries
| Library | Version | Purpose | When to Use | Confidence |
|---------|---------|---------|-------------|------------|
| Microsoft.Extensions.Http.Polly | latest | Polly + HttpClientFactory | Register resilience policies on the `GraphServiceClient`'s HTTP pipeline | HIGH |
| System.Threading.Channels | .NET 10 BCL | Producer-consumer queue | Internal queue between sync engine stages (resolve -> normalize -> write). Built-in, no NuGet needed. | HIGH |
| Microsoft.Extensions.Caching.Memory | .NET 10 BCL | In-memory caching | Cache DDG membership resolution results within a sync run. No Redis needed for single-VM. | HIGH |
| FluentValidation | latest | Request validation | Validate tunnel creation/update payloads, settings changes. Cleaner than data annotations for complex rules. | MEDIUM |
| BCrypt.Net-Next | latest | Password hashing | Hash the admin password stored in env vars. JWT auth for v1. | MEDIUM |
## Alternatives Considered
| Category | Recommended | Alternative | Why Not |
|----------|-------------|-------------|---------|
| Runtime | .NET 10 | .NET 8 | EOL Nov 2026 -- too soon for a greenfield project |
| Frontend | Next.js 15 | Next.js 16 | Breaking changes (proxy rename, Turbopack default) with no benefit for an admin tool |
| Frontend | Next.js 15 | Next.js 14 | Falling behind on security patches. 15 is the active LTS. |
| ORM | EF Core + raw SQL hot paths | Dapper everywhere | Lose migrations, change tracking, type safety for CRUD. Marginal perf gain on non-hot paths. |
| ORM | EF Core + raw SQL hot paths | EF Core everywhere | Sync engine bulk operations need raw SQL performance |
| Jobs | Hangfire | Quartz.NET | Heavier setup, no built-in dashboard, overkill for simple cron + fire-and-forget |
| Jobs | Hangfire | Plain BackgroundService | No persistence, no retry, no dashboard, no cron |
| Jobs | Hangfire | Hangfire Pro | Paid. Community edition handles the ~776 mailbox workload fine. |
| Graph Auth | Azure.Identity (ClientSecretCredential) | Microsoft.Identity.Web | MSAL/Identity.Web is for delegated user auth. This is a daemon app. |
| Database | PostgreSQL 16 | PostgreSQL 17 | 17 is fine but 16 has longer production track record. Either works. |
| Logging | Serilog | NLog | Both work. Serilog has better structured logging ergonomics and broader .NET ecosystem adoption. |
| Hashing | System.Security.Cryptography | BouncyCastle / third-party | BCL SHA-256 is hardware-accelerated and allocation-free. No reason to add a dependency. |
| Retry | Polly 8 | Custom retry loops | Polly is battle-tested, integrates with HttpClientFactory, handles Retry-After headers. Don't reinvent this. |
| Container | Docker Compose | Kubernetes | Single VM, bounded workload, 5 services. K8s is massive overkill. |
| Node image | node:22-alpine | node:20-alpine | Node 20 LTS ends April 2026. Node 22 LTS is supported until April 2027. |
## What NOT to Use
| Technology | Why Not |
|------------|---------|
| Microsoft.Identity.Web | Designed for delegated auth (user sign-in). This is a daemon app using client credentials. Use `Azure.Identity` directly. |
| MediatR | Over-engineering for an admin tool with ~10 endpoints. Direct service injection is simpler and debuggable. |
| AutoMapper | Manual mapping is clearer for a small domain. AutoMapper hides bugs and adds startup cost. |
| Redis | Single VM, single worker instance. `IMemoryCache` is sufficient. Redis adds operational complexity with zero benefit here. |
| RabbitMQ / Kafka | No inter-service messaging needed. Hangfire handles job queuing. The sync engine is a single process. |
| SignalR | No real-time push needed (explicitly out of scope). Polling or page refresh is fine for admin UI. |
| Blazor | Next.js is already decided. Blazor would duplicate the frontend stack and fragment expertise. |
| gRPC | REST/JSON is simpler for the admin UI to consume. No inter-service communication that benefits from gRPC. |
| Ocelot / YARP | nginx handles reverse proxy at the Docker level. No need for an in-process .NET API gateway. |
## Installation
# Backend project setup (.NET 10)
# Core dependencies
# API dependencies
# Worker dependencies
# Frontend project setup
# Ensure package.json pins Next.js to 15.x:
# "next": "^15.0.0"
## Version Pinning Strategy
| Package | Pin Strategy | Rationale |
|---------|-------------|-----------|
| .NET SDK | `10.0.x` in global.json | Lock SDK version across dev machines and CI |
| Microsoft.Graph | `5.*` (float minor) | Biweekly releases with new Graph API models. Minor bumps are safe. |
| Azure.Identity | `1.*` (float minor) | Stable API, backward compatible |
| EF Core / Npgsql | `10.0.*` (float patch) | Align with .NET 10. Patch = bug fixes only. |
| Hangfire | `1.8.*` (float patch) | Stable major version. Pin major+minor. |
| Polly | `8.*` (float minor) | v8 API is stable. Minor versions add strategies, don't break. |
| Next.js | `~15.2.0` (float patch) | Pin to latest 15.x minor. Patch = fixes only. Do not allow 16.x. |
| Node | `22` in Dockerfile | LTS. Pin major only -- Alpine updates handle patches. |
| PostgreSQL | `16` in Dockerfile | Pin major. Minor = bug fixes applied via image updates. |
## Sources
- [.NET Support Policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) -- .NET 8 EOL Nov 2026, .NET 10 LTS until Nov 2028
- [Microsoft.Graph 5.103.0 on NuGet](https://www.nuget.org/packages/Microsoft.Graph/) -- Latest SDK version
- [Azure.Identity 1.20.0 on NuGet](https://www.nuget.org/packages/azure.identity) -- Latest auth library
- [Npgsql 10.0.2 on NuGet](https://www.nuget.org/packages/npgsql/) -- Latest PostgreSQL driver
- [Npgsql.EntityFrameworkCore.PostgreSQL 10.0.1 on NuGet](https://www.nuget.org/packages/npgsql.entityframeworkcore.postgresql) -- EF Core provider
- [Hangfire.AspNetCore 1.8.23 on NuGet](https://www.nuget.org/packages/hangfire.aspnetcore/) -- Latest Hangfire
- [Hangfire.PostgreSql 1.21.1 on NuGet](https://www.nuget.org/packages/Hangfire.PostgreSql/) -- PostgreSQL storage
- [Polly 8.6.6 on NuGet](https://www.nuget.org/packages/polly/) -- Resilience library
- [Serilog.AspNetCore 10.0.0 on NuGet](https://www.nuget.org/packages/serilog.aspnetcore) -- Structured logging
- [Next.js Support Policy](https://nextjs.org/support-policy) -- v15 in active LTS
- [Next.js endoflife.date](https://endoflife.date/nextjs) -- Version lifecycle tracking
- [Official .NET Docker images](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/net-core-net-framework-containers/official-net-docker-images) -- Container guidance
- [PostgreSQL Docker Hub](https://hub.docker.com/_/postgres) -- Official images
- [Microsoft Graph authentication providers](https://learn.microsoft.com/en-us/graph/sdks/choose-authentication-providers) -- Client credentials flow docs
- [Dapper vs EF Core 2025 benchmarks](https://developersvoice.com/blog/database/orm-showdown-2025/) -- Performance comparison
<!-- GSD:stack-end -->

<!-- GSD:conventions-start source:CONVENTIONS.md -->
## Conventions

Conventions not yet established. Will populate as patterns emerge during development.
<!-- GSD:conventions-end -->

<!-- GSD:architecture-start source:ARCHITECTURE.md -->
## Architecture

Architecture not yet mapped. Follow existing patterns found in the codebase.
<!-- GSD:architecture-end -->

<!-- GSD:workflow-start source:GSD defaults -->
## GSD Workflow Enforcement

Before using Edit, Write, or other file-changing tools, start work through a GSD command so planning artifacts and execution context stay in sync.

Use these entry points:
- `/gsd:quick` for small fixes, doc updates, and ad-hoc tasks
- `/gsd:debug` for investigation and bug fixing
- `/gsd:execute-phase` for planned phase work

Do not make direct repo edits outside a GSD workflow unless the user explicitly asks to bypass it.
<!-- GSD:workflow-end -->



<!-- GSD:profile-start -->
## Developer Profile

> Profile not yet configured. Run `/gsd:profile-user` to generate your developer profile.
> This section is managed by `generate-claude-profile` -- do not edit manually.
<!-- GSD:profile-end -->
