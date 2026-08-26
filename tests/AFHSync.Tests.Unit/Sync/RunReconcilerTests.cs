using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>Phase 2 (§2.7): worker startup fails rows left Running by a dead process.</summary>
public class RunReconcilerTests
{
    private static AFHSyncDbContext MakeDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AFHSyncDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AFHSyncDbContext(options);
    }

    private sealed class TestDbContextFactory(string dbName) : IDbContextFactory<AFHSyncDbContext>
    {
        public AFHSyncDbContext CreateDbContext() => MakeDbContext(dbName);
    }

    [Fact]
    public async Task ReconcileAsync_FailsRunningRows_ClearsCancelFlag_LeavesOthersAlone()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.SyncRuns.AddRange(
                new SyncRun { Id = 1, RunType = RunType.Manual, Status = SyncStatus.Running, StartedAt = DateTime.UtcNow.AddMinutes(-20), CreatedAt = DateTime.UtcNow.AddMinutes(-21) },
                new SyncRun { Id = 2, RunType = RunType.PhotoSync, Status = SyncStatus.Running, StartedAt = DateTime.UtcNow.AddMinutes(-5), CreatedAt = DateTime.UtcNow.AddMinutes(-5) },
                new SyncRun { Id = 3, RunType = RunType.Manual, Status = SyncStatus.Pending, CreatedAt = DateTime.UtcNow },
                new SyncRun { Id = 4, RunType = RunType.Manual, Status = SyncStatus.Success, CompletedAt = DateTime.UtcNow.AddHours(-1), CreatedAt = DateTime.UtcNow.AddHours(-1) });
            seedCtx.AppSettings.Add(new AppSetting { Id = 1, Key = "cancel_sync", Value = "true", UpdatedAt = DateTime.UtcNow });
            await seedCtx.SaveChangesAsync();
        }

        var reconciler = new RunReconciler(new TestDbContextFactory(dbName), NullLogger<RunReconciler>.Instance);

        var count = await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, count);
        using var verifyCtx = MakeDbContext(dbName);
        var runs = await verifyCtx.SyncRuns.OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(SyncStatus.Failed, runs[0].Status);
        Assert.Equal(RunReconciler.InterruptedSummary, runs[0].ErrorSummary);
        Assert.NotNull(runs[0].CompletedAt);
        Assert.NotNull(runs[0].DurationMs);
        Assert.Equal(SyncStatus.Failed, runs[1].Status);
        Assert.Equal(SyncStatus.Pending, runs[2].Status);      // a queued job may still claim it
        Assert.Equal(SyncStatus.Success, runs[3].Status);
        var flag = await verifyCtx.AppSettings.SingleAsync(s => s.Key == "cancel_sync");
        Assert.Equal("false", flag.Value);
    }

    [Fact]
    public async Task ReconcileAsync_NothingRunning_ReturnsZero()
    {
        var dbName = Guid.NewGuid().ToString();
        var reconciler = new RunReconciler(new TestDbContextFactory(dbName), NullLogger<RunReconciler>.Instance);

        var count = await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, count);
    }
}
