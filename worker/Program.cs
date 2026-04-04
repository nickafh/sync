using AFHSync.Api.Data;
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

    var builder = Host.CreateDefaultBuilder(args);

    builder.UseSerilog();

    builder.ConfigureServices((context, services) =>
    {
        // DbContextFactory registration with Npgsql MapEnum for PostgreSQL native enums.
        // IDbContextFactory is used instead of IDbContext for parallel mailbox processing
        // (each parallel task creates its own DbContext from the factory per Pitfall 1 in RESEARCH.md).
        services.AddDbContextFactory<AFHSyncDbContext>(options =>
            options.UseNpgsql(
                context.Configuration.GetConnectionString("Default"),
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

        // Hangfire server + PostgreSQL storage (per D-07, D-16, D-17)
        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(c =>
                c.UseNpgsqlConnection(
                    context.Configuration.GetConnectionString("Default")!)));

        services.AddHangfireServer(options =>
        {
            options.WorkerCount = 2; // Low count: sync runs are heavy, bounded by semaphore
            options.Queues = new[] { "sync", "default" };
        });
    });

    var host = builder.Build();

    // Register recurring sync job from app_settings (per D-08, SCHD-01)
    using (var scope = host.Services.CreateScope())
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
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "AFH Sync Worker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
