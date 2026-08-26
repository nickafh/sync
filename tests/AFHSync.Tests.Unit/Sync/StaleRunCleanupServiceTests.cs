using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>Phase 2 (§2.7): Pending rows nobody claimed within 10 minutes are failed.</summary>
public class StaleRunCleanupServiceTests
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

    /// <summary>Records job ids passed to Delete (ChangeState with DeletedState).</summary>
    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<string> DeletedJobIds { get; } = [];
        public string Create(Job job, IState state) => Guid.NewGuid().ToString("N");
        public bool ChangeState(string jobId, IState state, string? expectedState)
        {
            if (state is DeletedState) DeletedJobIds.Add(jobId);
            return true;
        }
    }

    [Fact]
    public async Task CleanupAsync_FailsPendingRowsOlderThan10Minutes_LeavesYoungerOnes()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.SyncRuns.AddRange(
                new SyncRun { Id = 1, RunType = RunType.Manual, Status = SyncStatus.Pending, CreatedAt = DateTime.UtcNow.AddMinutes(-11), HangfireJobIds = "job-old" },
                new SyncRun { Id = 2, RunType = RunType.Manual, Status = SyncStatus.Pending, CreatedAt = DateTime.UtcNow.AddMinutes(-2), HangfireJobIds = "job-young" });
            await seedCtx.SaveChangesAsync();
        }
        var jobs = new RecordingJobClient();
        var service = new StaleRunCleanupService(new TestDbContextFactory(dbName), jobs, NullLogger<StaleRunCleanupService>.Instance);

        await service.CleanupAsync();

        using var verifyCtx = MakeDbContext(dbName);
        var old = await verifyCtx.SyncRuns.SingleAsync(r => r.Id == 1);
        Assert.Equal(SyncStatus.Failed, old.Status);
        Assert.Equal(StaleRunCleanupService.PendingNeverClaimedSummary, old.ErrorSummary);
        Assert.NotNull(old.CompletedAt);
        var young = await verifyCtx.SyncRuns.SingleAsync(r => r.Id == 2);
        Assert.Equal(SyncStatus.Pending, young.Status);
        Assert.Equal(new[] { "job-old" }, jobs.DeletedJobIds);
        // A never-started job needs no cancel flag.
        Assert.False(await verifyCtx.AppSettings.AnyAsync(s => s.Key == "cancel_sync" && s.Value == "true"));
    }

    [Fact]
    public async Task CleanupAsync_StillFailsLongRunningRows_AndRaisesCancelFlag()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.SyncRuns.Add(new SyncRun { Id = 1, RunType = RunType.Manual, Status = SyncStatus.Running, StartedAt = DateTime.UtcNow.AddHours(-3), CreatedAt = DateTime.UtcNow.AddHours(-3), HangfireJobIds = "job-stuck" });
            await seedCtx.SaveChangesAsync();
        }
        var jobs = new RecordingJobClient();
        var service = new StaleRunCleanupService(new TestDbContextFactory(dbName), jobs, NullLogger<StaleRunCleanupService>.Instance);

        await service.CleanupAsync();

        using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(SyncStatus.Failed, (await verifyCtx.SyncRuns.SingleAsync()).Status);
        Assert.Equal("true", (await verifyCtx.AppSettings.SingleAsync(s => s.Key == "cancel_sync")).Value);
        Assert.Equal(new[] { "job-stuck" }, jobs.DeletedJobIds);
    }
}
