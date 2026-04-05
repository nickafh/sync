using AFHSync.Shared.Data;
using AFHSync.Shared.Enums;
using AFHSync.Shared.Services;
using AFHSync.Worker.Graph;
using AFHSync.Worker.Services;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
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
    services.AddSingleton<GraphClientFactory>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var resilienceHandler = sp.GetRequiredService<GraphResilienceHandler>();
        return new GraphClientFactory(config, new DelegatingHandler[] { resilienceHandler });
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

    // Hangfire server + PostgreSQL storage (per D-07, D-16, D-17)
    services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(c =>
            c.UseNpgsqlConnection(
                configuration.GetConnectionString("Default")!)));

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
