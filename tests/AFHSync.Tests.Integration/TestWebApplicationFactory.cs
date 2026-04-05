using AFHSync.Shared.Data;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AFHSync.Tests.Integration;

/// <summary>
/// Custom WebApplicationFactory that replaces PostgreSQL with InMemory database for testing.
/// Auth tests don't require real Postgres -- InMemory is sufficient for validating
/// JWT middleware, cookie handling, and route protection.
/// Also stubs Hangfire services so tests don't require a real PostgreSQL Hangfire storage.
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

            // Add InMemory database for tests (unique per factory instance).
            // Use a dedicated service provider to avoid "multiple providers" conflicts
            // that occur when Npgsql's extension registration is still cached globally.
            var testDbName = "TestDb_" + Guid.NewGuid().ToString("N");
            var inMemoryServiceProvider = new ServiceCollection()
                .AddEntityFrameworkInMemoryDatabase()
                .BuildServiceProvider();

            services.AddDbContext<AFHSyncDbContext>(options =>
                options
                    .UseInMemoryDatabase(testDbName)
                    .UseInternalServiceProvider(inMemoryServiceProvider));

            // Replace Hangfire services with no-op stubs for testing.
            // Hangfire's AddHangfire() registers IBackgroundJobClient and IRecurringJobManager
            // which try to connect to PostgreSQL. Override with stubs.
            var hangfireJobClient = services.SingleOrDefault(
                d => d.ServiceType == typeof(IBackgroundJobClient));
            if (hangfireJobClient != null) services.Remove(hangfireJobClient);

            var hangfireRecurringManager = services.SingleOrDefault(
                d => d.ServiceType == typeof(IRecurringJobManager));
            if (hangfireRecurringManager != null) services.Remove(hangfireRecurringManager);

            services.AddSingleton<IBackgroundJobClient>(new NoOpBackgroundJobClient());
            services.AddSingleton<IRecurringJobManager>(new NoOpRecurringJobManager());
        });

        builder.UseEnvironment("Development");
    }
}

/// <summary>
/// No-op IBackgroundJobClient for integration tests.
/// All Enqueue/Schedule calls succeed silently without connecting to Hangfire storage.
/// </summary>
internal class NoOpBackgroundJobClient : IBackgroundJobClient
{
    public string Create(Hangfire.Common.Job job, IState state)
    {
        // Return a fake job ID
        return Guid.NewGuid().ToString("N");
    }

    public bool ChangeState(string jobId, IState state, string? expectedState)
    {
        return true;
    }
}

/// <summary>
/// No-op IRecurringJobManager for integration tests.
/// AddOrUpdate/RemoveIfExists calls succeed silently.
/// </summary>
internal class NoOpRecurringJobManager : IRecurringJobManager
{
    public void AddOrUpdate(string recurringJobId, Hangfire.Common.Job job, string cronExpression, RecurringJobOptions options)
    {
        // No-op
    }

    public void Trigger(string recurringJobId)
    {
        // No-op
    }

    public void RemoveIfExists(string recurringJobId)
    {
        // No-op
    }
}
