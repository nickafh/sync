using AFHSync.Shared.Data;
using AFHSync.Shared.Enums;
using AFHSync.Shared.Services;
using AFHSync.Worker.Graph;
using AFHSync.Worker.Services;
using Azure.Identity;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateLogger();

try
{
    Log.Information("AFH Sync Worker started");

    var builder = WebApplication.CreateSlimBuilder(args);

    builder.Host.UseSerilog();

    // Listen on port 8081 for health checks only
    builder.WebHost.UseUrls("http://+:8081");

    var services = builder.Services;
    var configuration = builder.Configuration;

    // DbContextFactory registration with Npgsql MapEnum for PostgreSQL native enums.
    // IDbContextFactory is used instead of IDbContext for parallel mailbox processing
    // (each parallel task creates its own DbContext from the factory per Pitfall 1 in RESEARCH.md).
    services.AddDbContextFactory<AFHSyncDbContext>(options =>
        options.UseNpgsql(
            configuration.GetConnectionString("Default"),
            o =>
            {
                o.MapEnum<SourceType>("source_type");
                o.MapEnum<TargetScope>("target_scope");
                o.MapEnum<StalePolicy>("stale_policy");
                o.MapEnum<SyncBehavior>("sync_behavior");
                o.MapEnum<SyncStatus>("sync_status");
                o.MapEnum<TunnelStatus>("tunnel_status");
                o.MapEnum<RunType>("run_type");
                o.MapEnum<CleanupJobStatus>("cleanup_job_status");
            }));

    // Thread-safe throttle event counter — singleton shared between the singleton
    // GraphResilienceHandler and scoped SyncEngine instances. SyncEngine resets it
    // at the start of each run and reads Count at finalize (Plan 04).
    services.AddSingleton<ThrottleCounter>();

    // Resilience handler — singleton so the Polly pipeline state is shared across all
    // Graph calls. Wraps 429/503 responses with exponential backoff + jitter and honours
    // the Retry-After header. The onThrottle callback is wired to ThrottleCounter.Increment
    // so SyncRun.ThrottleEvents reflects actual Graph retry counts (Plan 04).
    services.AddSingleton<GraphResilienceHandler>(sp =>
        new GraphResilienceHandler(
            sp.GetRequiredService<ILogger<GraphResilienceHandler>>(),
            onThrottle: _ => sp.GetRequiredService<ThrottleCounter>().Increment()));

    // Graph client factory — singleton so the GraphServiceClient is reused across requests.
    // Inject the resilience handler into the Graph HTTP pipeline so every SDK call
    // automatically benefits from retry logic (SYNC-10).
    services.AddSingleton<AFHSync.Worker.Graph.GraphClientFactory>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var resilienceHandler = sp.GetRequiredService<GraphResilienceHandler>();
        return new AFHSync.Worker.Graph.GraphClientFactory(config, new DelegatingHandler[] { resilienceHandler });
    });

    // Plain Singleton GraphServiceClient for the cleanup runner (quick-260417-48z).
    // Mirrors api/Program.cs:98-106 so the cleanup runner gets a vanilla Graph client
    // without the SyncEngine resilience pipeline (Polly retries are unnecessary for
    // one-shot folder deletes — Hangfire's automatic retry is also disabled in the
    // runner because tenant wipes are destructive and not safe to auto-replay).
    services.AddSingleton(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var credential = new ClientSecretCredential(
            config["Graph:TenantId"],
            config["Graph:ClientId"],
            config["Graph:ClientSecret"]);
        return new GraphServiceClient(credential);
    });

    // Sync pipeline services — scoped per sync run invocation
    services.AddScoped<ISourceResolver, SourceResolver>();
    services.AddScoped<IContactPayloadBuilder, ContactPayloadBuilder>();
    services.AddScoped<IContactWriter, ContactWriter>();
    services.AddScoped<IContactFolderManager, ContactFolderManager>();
    services.AddScoped<IStaleContactHandler, StaleContactHandler>();
    services.AddScoped<IRunLogger, RunLogger>();
    services.AddScoped<ISyncEngine, SyncEngine>();
    services.AddScoped<IPhotoSyncService, PhotoSyncService>();
    services.AddScoped<StaleRunCleanupService>();

    // Cleanup runner (quick-260417-48z) — Hangfire resolves this via the
    // ICleanupJobRunner interface registered in shared/.
    services.AddScoped<ICleanupJobRunner, CleanupJobRunner>();

    // DDG target resolution (per quick-260417-2lb): worker resolves DDG members at sync time
    // to union with explicit emails in PhoneList.TargetUserFilter. Reuses the api project's
    // resolver (ExchangeOnlineManagement PowerShell + the OPATH→Graph filter converter)
    // to avoid duplicating the runspace + cert auth config in two assemblies.
    // Lifetimes mirror api/Program.cs:109-110 — Scoped resolver, Singleton converter.
    services.AddScoped<AFHSync.Api.Services.IDDGResolver, AFHSync.Api.Services.DDGResolver>();
    services.AddSingleton<AFHSync.Api.Services.IFilterConverter, AFHSync.Api.Services.FilterConverter>();

    // Hangfire server + PostgreSQL storage (per D-07, D-16, D-17).
    // InvisibilityTimeout bumped to 3h: tenant-wide syncs hitting ~965 mailboxes can
    // exceed the 30m default and trigger spurious cancellation tokens (observed
    // 2026-04-17 — runs canceled at 30m21s with all tunnels reporting "operation was canceled").
    services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(c =>
            c.UseNpgsqlConnection(
                configuration.GetConnectionString("Default")!),
            new Hangfire.PostgreSql.PostgreSqlStorageOptions
            {
                InvisibilityTimeout = TimeSpan.FromHours(3)
            }));

    services.AddHangfireServer(options =>
    {
        options.WorkerCount = 2; // Low count: sync runs are heavy, bounded by semaphore
        options.Queues = new[] { "sync", "default" };
    });

    var app = builder.Build();

    // Health check endpoint for Docker healthcheck
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

    // Register recurring sync job from app_settings (per D-08, SCHD-01)
    using (var scope = app.Services.CreateScope())
    {
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AFHSyncDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var cronSetting = await db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "sync_schedule_cron");
        var cronExpression = cronSetting?.Value ?? "0 */4 * * *";

        var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
        recurringJobManager.AddOrUpdate<ISyncEngine>(
            "sync-all",
            engine => engine.RunAsync(null, RunType.Scheduled, false, CancellationToken.None),
            cronExpression);

        Log.Information("Registered recurring sync job with cron: {Cron}", cronExpression);

        // Stale run cleanup: mark runs stuck in "Running" for >2 hours as Failed.
        // Safety net for the rare case where even finalization fails (DB outage, OOM, etc.).
        recurringJobManager.AddOrUpdate<StaleRunCleanupService>(
            "stale-run-cleanup",
            svc => svc.CleanupAsync(),
            "*/30 * * * *"); // every 30 minutes

        // Register photo sync recurring job for separate_pass mode (D-02, PHOT-03)
        var photoModeSetting = await db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "photo_sync_mode");
        var photoSyncMode = photoModeSetting?.Value ?? "included";

        if (photoSyncMode == "separate_pass")
        {
            var photoCronSetting = await db.AppSettings
                .FirstOrDefaultAsync(s => s.Key == "photo_sync_cron");
            var photoCronExpression = photoCronSetting?.Value ?? "0 */6 * * *";

            recurringJobManager.AddOrUpdate<IPhotoSyncService>(
                "photo-sync-all",
                svc => svc.RunAllAsync(RunType.Scheduled, false, CancellationToken.None),
                photoCronExpression);

            Log.Information("Registered photo sync recurring job with cron: {Cron}", photoCronExpression);
        }
        else
        {
            // Remove the job if mode is not separate_pass (clean up if mode was changed)
            recurringJobManager.RemoveIfExists("photo-sync-all");
        }
    }

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "AFH Sync Worker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

