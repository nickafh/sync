using System.Text;
using AFHSync.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.IdentityModel.Tokens;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateLogger();

try
{
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

    // DbContext registration added in Plan 02

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
                        ?? "fallback-dev-secret-at-least-32-chars!!"))
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

    // Graph health check service (D-11)
    builder.Services.AddSingleton<GraphHealthService>();

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "AFH Sync API terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Make Program class accessible to WebApplicationFactory in test projects
public partial class Program { }
