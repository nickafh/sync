using AFHSync.Api.Data;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Graph;
using AFHSync.Worker.Services;
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

        // Resilience handler — singleton so the Polly pipeline state is shared across all
        // Graph calls. Wraps 429/503 responses with exponential backoff + jitter and honours
        // the Retry-After header. The onThrottle callback will be wired to SyncRun tracking
        // in the SyncEngine (Plan 03).
        services.AddSingleton<GraphResilienceHandler>();

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
    });

    var host = builder.Build();
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
