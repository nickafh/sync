using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Shared.Services;
using AFHSync.Worker.Services;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Unit tests for <see cref="CleanupJobRunner"/> covering progress flush math
/// (Test 1) and status transitions on entry / completion / failure (Test 2).
///
/// Test 1 follows the plan's downgraded path: instead of stubbing the Graph SDK
/// (which has no convenient mocking surface), we exercise the internal
/// <c>FlushProgressAsync</c> helper directly and assert the (deleted, failed,
/// status) tuple is persisted exactly as expected. This is sufficient to verify
/// the write semantics that drive the every-25-items + final-flush cadence.
///
/// Test 2 invokes <c>RunAsync</c> with an empty items array — no Graph calls
/// happen — to confirm the Queued → Running → Completed transition with
/// StartedAt / CompletedAt timestamps. The Failed-state path is verified by
/// directly invoking the failure-flush via <see cref="CleanupJobRunner.FlushProgressAsync"/>.
/// </summary>
public class CleanupJobRunnerTests
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

    private static GraphServiceClient MakeGraphClientStub()
    {
        // The runner only calls into the Graph SDK from inside the per-item loop.
        // For empty-item RunAsync invocations and for direct FlushProgressAsync
        // calls, the client is never invoked — a stub built against a default
        // ClientSecretCredential is sufficient to satisfy the constructor.
        return new GraphServiceClient(new DefaultAzureCredential());
    }

    private static CleanupJobRunner MakeRunner(string dbName)
    {
        return new CleanupJobRunner(
            new TestDbContextFactory(dbName),
            MakeGraphClientStub(),
            NullLogger<CleanupJobRunner>.Instance);
    }

    // ============================================================
    // Test 1: progress flush math via FlushProgressAsync directly
    // ============================================================

    [Fact]
    public async Task FlushProgressAsync_PersistsCounters_StatusAndCompletedAt_AsTuple()
    {
        // Arrange — seed a Queued job row.
        var dbName = Guid.NewGuid().ToString();
        var jobId = Guid.NewGuid();
        await using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.CleanupJobs.Add(new CleanupJob
            {
                Id = jobId,
                Status = CleanupJobStatus.Queued,
                Total = 60,
                Deleted = 0,
                Failed = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seedCtx.SaveChangesAsync();
        }

        var runner = MakeRunner(dbName);

        // Act 1 — mid-run flush (every-25 cadence): status=null, completed=false.
        await runner.FlushProgressAsync(jobId, deleted: 25, failed: 0, lastError: null,
            status: null, completed: false, CancellationToken.None);

        await using (var ctx = MakeDbContext(dbName))
        {
            var job = await ctx.CleanupJobs.FindAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(25, job!.Deleted);
            Assert.Equal(0, job.Failed);
            Assert.Equal(CleanupJobStatus.Queued, job.Status); // unchanged — flush passed status=null
            Assert.Null(job.CompletedAt);                       // not yet completed
            Assert.Null(job.LastError);
        }

        // Act 2 — second flush at item 50 with a failure recorded.
        await runner.FlushProgressAsync(jobId, deleted: 49, failed: 1,
            lastError: "[ErrorCode] something broke", status: null, completed: false,
            CancellationToken.None);

        await using (var ctx = MakeDbContext(dbName))
        {
            var job = await ctx.CleanupJobs.FindAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(49, job!.Deleted);
            Assert.Equal(1, job.Failed);
            Assert.Equal("[ErrorCode] something broke", job.LastError);
            Assert.Null(job.CompletedAt);
        }

        // Act 3 — final flush: status=Completed, completed=true.
        await runner.FlushProgressAsync(jobId, deleted: 59, failed: 1,
            lastError: "[ErrorCode] something broke",
            status: CleanupJobStatus.Completed, completed: true, CancellationToken.None);

        await using (var ctx = MakeDbContext(dbName))
        {
            var job = await ctx.CleanupJobs.FindAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(59, job!.Deleted);
            Assert.Equal(1, job.Failed);
            Assert.Equal(CleanupJobStatus.Completed, job.Status);
            Assert.NotNull(job.CompletedAt);
            // Sanity: final tuple sums match Total.
            Assert.Equal(job.Total, job.Deleted + job.Failed);
        }
    }

    // ============================================================
    // Test 2: status transitions on RunAsync entry + completion
    // ============================================================

    [Fact]
    public async Task RunAsync_FlipsStatusToRunningOnEntry_ThenCompletedAtEnd_WithEmptyItems()
    {
        // Arrange — seed a Queued job row with Total=0 (empty items array).
        var dbName = Guid.NewGuid().ToString();
        var jobId = Guid.NewGuid();
        var beforeRun = DateTime.UtcNow;

        await using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.CleanupJobs.Add(new CleanupJob
            {
                Id = jobId,
                Status = CleanupJobStatus.Queued,
                Total = 0,
                CreatedAt = beforeRun,
                UpdatedAt = beforeRun,
            });
            await seedCtx.SaveChangesAsync();
        }

        var runner = MakeRunner(dbName);

        // Act — empty items array means no Graph calls are made; we exercise
        // only the entry transition + final flush.
        await runner.RunAsync(jobId, items: Array.Empty<CleanupJobItem>(), CancellationToken.None);

        // Assert — Queued → Running → Completed; StartedAt + CompletedAt set;
        // counters at zero (no items).
        await using var ctx = MakeDbContext(dbName);
        var job = await ctx.CleanupJobs.FindAsync(jobId);
        Assert.NotNull(job);
        Assert.Equal(CleanupJobStatus.Completed, job!.Status);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.CompletedAt);
        Assert.True(job.StartedAt >= beforeRun);
        Assert.True(job.CompletedAt >= job.StartedAt);
        Assert.Equal(0, job.Deleted);
        Assert.Equal(0, job.Failed);
        Assert.Equal(0, job.Total);
        Assert.Null(job.LastError);
    }

    [Fact]
    public async Task FlushProgressAsync_FailureFlush_FlipsStatusToFailed_AndRecordsLastError()
    {
        // Mirrors the catch-block flush in RunAsync where an unexpected exception
        // is observed: the runner must record Status=Failed, CompletedAt, and the
        // exception message — not leave the row stuck in Running.
        var dbName = Guid.NewGuid().ToString();
        var jobId = Guid.NewGuid();
        await using (var seedCtx = MakeDbContext(dbName))
        {
            seedCtx.CleanupJobs.Add(new CleanupJob
            {
                Id = jobId,
                Status = CleanupJobStatus.Running,
                Total = 100,
                Deleted = 12,
                Failed = 3,
                StartedAt = DateTime.UtcNow.AddMinutes(-5),
                CreatedAt = DateTime.UtcNow.AddMinutes(-6),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            });
            await seedCtx.SaveChangesAsync();
        }

        var runner = MakeRunner(dbName);

        await runner.FlushProgressAsync(jobId, deleted: 12, failed: 3,
            lastError: "Database connection lost",
            status: CleanupJobStatus.Failed, completed: true, CancellationToken.None);

        await using var ctx = MakeDbContext(dbName);
        var job = await ctx.CleanupJobs.FindAsync(jobId);
        Assert.NotNull(job);
        Assert.Equal(CleanupJobStatus.Failed, job!.Status);
        Assert.Equal("Database connection lost", job.LastError);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task RunAsync_MissingJobRow_LogsWarningAndReturnsWithoutThrowing()
    {
        // Defensive guard: if the job row is missing at start (caller race / manual
        // delete), the runner must not crash the Hangfire worker.
        var dbName = Guid.NewGuid().ToString();
        var runner = MakeRunner(dbName);
        var missingId = Guid.NewGuid();

        await runner.RunAsync(missingId, Array.Empty<CleanupJobItem>(), CancellationToken.None);

        // No exception thrown — and the row genuinely doesn't exist.
        await using var ctx = MakeDbContext(dbName);
        Assert.False(await ctx.CleanupJobs.AnyAsync(j => j.Id == missingId));
    }
}
