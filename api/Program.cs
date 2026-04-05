using System.Text;
using AFHSync.Shared.Data;
using AFHSync.Api.Filters;
using AFHSync.Api.Services;
using AFHSync.Shared.Enums;
using Azure.Identity;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.IdentityModel.Tokens;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateLogger();

Log.Information("AFH Sync API starting");

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();

// Global auth filter: all endpoints require authentication by default (AUTH-03)
// Individual endpoints opt out with [AllowAnonymous]
builder.Services.AddControllers(options =>
{
    var policy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.Filters.Add(new AuthorizeFilter(policy));
});

// DbContext registration with Npgsql MapEnum for PostgreSQL native enums (D-01, INFRA-02)
builder.Services.AddDbContext<AFHSyncDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Default"),
        o =>
        {
            o.MigrationsAssembly("AFHSync.Api");
            o.MapEnum<SourceType>("source_type");
            o.MapEnum<TargetScope>("target_scope");
            o.MapEnum<StalePolicy>("stale_policy");
            o.MapEnum<SyncBehavior>("sync_behavior");
            o.MapEnum<SyncStatus>("sync_status");
            o.MapEnum<TunnelStatus>("tunnel_status");
            o.MapEnum<RunType>("run_type");
        }));

// JWT Authentication (D-05: simple JWT, D-07: httpOnly cookie)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = "AFHSync",
            ValidAudience = "AFHSync",
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Auth:JwtSecret"]
                    ?? throw new InvalidOperationException("Auth:JwtSecret must be configured")))
        };

        // Read JWT from httpOnly cookie instead of Authorization header (D-07)
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                context.Token = context.Request.Cookies["afh_auth"];
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Hangfire client-only registration (per D-07, D-17)
// API enqueues jobs; worker executes them. No AddHangfireServer() here.
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(c =>
        c.UseNpgsqlConnection(
            builder.Configuration.GetConnectionString("Default")!)));

// Graph health check service (D-11)
builder.Services.AddSingleton<GraphHealthService>();

// GraphServiceClient for DDG member lookups and future Graph API calls
// Uses same Entra app credentials as GraphHealthService (Graph:TenantId/ClientId/ClientSecret)
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var credential = new ClientSecretCredential(
        config["Graph:TenantId"],
        config["Graph:ClientId"],
        config["Graph:ClientSecret"]);
    return new GraphServiceClient(credential);
});

// DDG resolution services (per D-01, D-02)
builder.Services.AddScoped<IDDGResolver, DDGResolver>();
builder.Services.AddSingleton<IFilterConverter, FilterConverter>();

var app = builder.Build();

app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

// Hangfire dashboard at /hangfire (per D-16)
// Internal-only monitoring — nginx does NOT route /hangfire to public.
// Dashboard requires JWT authentication via HangfireDashboardAuthFilter.
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireDashboardAuthFilter() }
});

app.MapControllers();

// Auto-migrate database on startup (D-03: no manual dotnet ef database update)
// Skip migration for InMemory database (used in tests)
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AFHSyncDbContext>();
    if (!db.Database.IsInMemory())
    {
        await db.Database.MigrateAsync();
    }
}
catch (Exception ex)
{
    Log.Warning(ex, "Database migration skipped or failed - will retry on next startup");
}

app.Run();

// Make Program class accessible to WebApplicationFactory in test projects
public partial class Program { }
