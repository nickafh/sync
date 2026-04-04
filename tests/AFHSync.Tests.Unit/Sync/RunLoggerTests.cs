using AFHSync.Api.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Unit tests for RunLogger: SyncRun creation, item buffering, and finalization.
/// Uses InMemory EF Core DbContext. Tests do not exercise actual SQL batch inserts
/// (those require a real DB) but do verify the buffer accumulation logic.
/// </summary>
public class RunLoggerTests
{
    private static AFHSyncDbContext MakeDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AFHSyncDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AFHSyncDbContext(options);
    }

    private static IDbContextFactory<AFHSyncDbContext> CreateFactory(string dbName)
    {
        return new TestDbContextFactory(dbName);
    }

    private sealed class TestDbContextFactory(string dbName) : IDbContextFactory<AFHSyncDbContext>
    {
        public AFHSyncDbContext CreateDbContext() => MakeDbContext(dbName);
    }

    // ==============================
    // Test 8: CreateRunAsync creates SyncRun with Running status
    // ==============================

    [Fact]
    public async Task CreateRunAsync_CreatesSyncRunWithRunningStatus()
    {
        var dbName = Guid.NewGuid().ToString();
        var factory = CreateFactory(dbName);
        var logger = new RunLogger(factory, NullLogger<RunLogger>.Instance);

        // Act
        var run = await logger.CreateRunAsync(RunType.Manual, isDryRun: false, ct: CancellationToken.None);

        // Assert
        Assert.NotEqual(0, run.Id);
        Assert.Equal(SyncStatus.Running, run.Status);
        Assert.Equal(RunType.Manual, run.RunType);
        Assert.False(run.IsDryRun);
        Assert.NotNull(run.StartedAt);

        // Verify persisted in DB
        using var verifyCtx = MakeDbContext(dbName);
        var persisted = await verifyCtx.SyncRuns.FirstAsync();
        Assert.Equal(SyncStatus.Running, persisted.Status);
        Assert.Equal(RunType.Manual, persisted.RunType);
    }

    // ==============================
    // Test 9: FinalizeRunAsync updates status, CompletedAt, DurationMs, aggregate counts
    // ==============================

    [Fact]
    public async Task FinalizeRunAsync_UpdatesStatusTimingAndCounts()
    {
        var dbName = Guid.NewGuid().ToString();
        var factory = CreateFactory(dbName);
        var logger = new RunLogger(factory, NullLogger<RunLogger>.Instance);

        var run = await logger.CreateRunAsync(RunType.Scheduled, isDryRun: false, ct: CancellationToken.None);

        // Act
        await logger.FinalizeRunAsync(
            run,
            status: SyncStatus.Success,
            errorSummary: null,
            contactsCreated: 10,
            contactsUpdated: 5,
            contactsSkipped: 20,
            contactsFailed: 1,
            contactsRemoved: 2,
            tunnelsProcessed: 3,
            tunnelsWarned: 0,
            tunnelsFailed: 0,
            throttleEvents: 0,
            ct: CancellationToken.None);

        // Assert
        using var verifyCtx = MakeDbContext(dbName);
        var updated = await verifyCtx.SyncRuns.FirstAsync();
        Assert.Equal(SyncStatus.Success, updated.Status);
        Assert.NotNull(updated.CompletedAt);
        Assert.NotNull(updated.DurationMs);
        Assert.Equal(10, updated.ContactsCreated);
        Assert.Equal(5, updated.ContactsUpdated);
        Assert.Equal(20, updated.ContactsSkipped);
        Assert.Equal(1, updated.ContactsFailed);
        Assert.Equal(2, updated.ContactsRemoved);
        Assert.Equal(3, updated.TunnelsProcessed);
    }

    // ==============================
    // Test 10: AddItem adds SyncRunItem to internal buffer
    // ==============================

    [Fact]
    public async Task AddItem_AccumulatesItemsInBuffer()
    {
        var dbName = Guid.NewGuid().ToString();
        var factory = CreateFactory(dbName);
        var logger = new RunLogger(factory, NullLogger<RunLogger>.Instance);

        var run = await logger.CreateRunAsync(RunType.Manual, isDryRun: false, CancellationToken.None);

        // Act: add 3 items
        logger.AddItem(new SyncRunItem { SyncRunId = run.Id, Action = "created", CreatedAt = DateTime.UtcNow });
        logger.AddItem(new SyncRunItem { SyncRunId = run.Id, Action = "updated", CreatedAt = DateTime.UtcNow });
        logger.AddItem(new SyncRunItem { SyncRunId = run.Id, Action = "skipped", CreatedAt = DateTime.UtcNow });

        // Verify items are NOT yet in the database (not flushed)
        using var verifyCtx = MakeDbContext(dbName);
        var countBeforeFlush = await verifyCtx.SyncRunItems.CountAsync();
        Assert.Equal(0, countBeforeFlush); // items are buffered, not committed yet

        // The buffer is internal — we verify the count via flush behavior in Test 11
    }

    // ==============================
    // Test 11: FlushItemsAsync inserts all buffered items via SQL
    // ==============================

    [Fact]
    public async Task FlushItemsAsync_InsertsAllBufferedItemsToDatabase()
    {
        var dbName = Guid.NewGuid().ToString();
        var factory = CreateFactory(dbName);
        var logger = new RunLogger(factory, NullLogger<RunLogger>.Instance);

        var run = await logger.CreateRunAsync(RunType.Manual, isDryRun: false, CancellationToken.None);

        logger.AddItem(new SyncRunItem { SyncRunId = run.Id, Action = "created", TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1, SourceUserId = 1, CreatedAt = DateTime.UtcNow });
        logger.AddItem(new SyncRunItem { SyncRunId = run.Id, Action = "updated", TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1, SourceUserId = 2, FieldChanges = "{\"Name\":{\"old\":\"Alice\",\"new\":\"Alicia\"}}", CreatedAt = DateTime.UtcNow });

        // Act: flush
        await logger.FlushItemsAsync(CancellationToken.None);

        // Assert: items persisted in DB
        using var verifyCtx = MakeDbContext(dbName);
        var items = await verifyCtx.SyncRunItems.OrderBy(i => i.SourceUserId).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.Equal("created", items[0].Action);
        Assert.Equal("updated", items[1].Action);
        Assert.Equal("{\"Name\":{\"old\":\"Alice\",\"new\":\"Alicia\"}}", items[1].FieldChanges);
    }
}
