using AFHSync.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AFHSync.Tests.Integration;

/// <summary>
/// Custom WebApplicationFactory that replaces PostgreSQL with InMemory database for testing.
/// Auth tests don't require real Postgres -- InMemory is sufficient for validating
/// JWT middleware, cookie handling, and route protection.
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Find and remove the existing DbContextOptions<AFHSyncDbContext> registration
            // This is the critical one -- it contains the Npgsql provider configuration
            var dbContextDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AFHSyncDbContext>));

            if (dbContextDescriptor != null)
            {
                services.Remove(dbContextDescriptor);
            }

            // Also remove the DbContext registration itself
            var dbContextService = services.SingleOrDefault(
                d => d.ServiceType == typeof(AFHSyncDbContext));

            if (dbContextService != null)
            {
                services.Remove(dbContextService);
            }

            // Add InMemory database for tests (unique per factory instance)
            services.AddDbContext<AFHSyncDbContext>(options =>
                options.UseInMemoryDatabase("TestDb_" + Guid.NewGuid().ToString("N")));
        });

        builder.UseEnvironment("Development");
    }
}
