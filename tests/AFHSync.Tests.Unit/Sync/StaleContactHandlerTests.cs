using AFHSync.Api.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Unit tests for StaleContactHandler stale detection and policy execution.
/// Uses InMemory EF Core DbContext for isolation, mocks IContactWriter.
/// </summary>
public class StaleContactHandlerTests
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

    private static Tunnel CreateTunnel(int id, StalePolicy policy, int staleHoldDays = 14)
    {
        return new Tunnel
        {
            Id = id,
            Name = $"Tunnel {id}",
            StalePolicy = policy,
            StaleHoldDays = staleHoldDays
        };
    }

    private static ContactSyncState CreateState(int id, int sourceUserId, int tunnelId, int phoneListId, int targetMailboxId, string? graphContactId = "graph-id-" + "x")
    {
        return new ContactSyncState
        {
            Id = id,
            SourceUserId = sourceUserId,
            TunnelId = tunnelId,
            PhoneListId = phoneListId,
            TargetMailboxId = targetMailboxId,
            GraphContactId = graphContactId,
            IsStale = false
        };
    }

    // ==============================
    // Test 1: DetectStale via set-difference
    // ==============================

    [Fact]
    public async Task HandleStaleAsync_AutoRemove_DetectsContactsNotInCurrentSourceSet()
    {
        // Arrange: 3 existing states, currentSourceUserIds = {1, 2} only
        var dbName = Guid.NewGuid().ToString();
        using var seedCtx = MakeDbContext(dbName);
        seedCtx.ContactSyncStates.AddRange(
            CreateState(1, sourceUserId: 1, tunnelId: 1, phoneListId: 1, targetMailboxId: 1),
            CreateState(2, sourceUserId: 2, tunnelId: 1, phoneListId: 1, targetMailboxId: 1),
            CreateState(3, sourceUserId: 3, tunnelId: 1, phoneListId: 1, targetMailboxId: 1) // stale!
        );
        await seedCtx.SaveChangesAsync();

        var writer = new FakeContactWriter();
        var factory = CreateFactory(dbName);
        var handler = new StaleContactHandler(factory, writer, NullLogger<StaleContactHandler>.Instance);

        var tunnel = CreateTunnel(1, StalePolicy.AutoRemove);
        var currentSourceIds = new HashSet<int> { 1, 2 };

        // Act
        var result = await handler.HandleStaleAsync(tunnel, phoneListId: 1, targetMailboxId: 1, mailboxEntraId: "mailbox@contoso.com", currentSourceUserIds: currentSourceIds, ct: CancellationToken.None);

        // Assert: 1 stale contact detected and removed
        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.StaleDetected);
    }

    // ==============================
    // Test 2: AutoRemove calls DeleteContactAsync and removes state
    // ==============================

    [Fact]
    public async Task HandleStaleAsync_AutoRemove_DeletesContactAndRemovesState()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedCtx = MakeDbContext(dbName);
        seedCtx.ContactSyncStates.Add(
            new ContactSyncState
            {
                Id = 1,
                SourceUserId = 99,
                TunnelId = 1,
                PhoneListId = 1,
                TargetMailboxId = 1,
                GraphContactId = "graph-id-abc",
                IsStale = false
            }
        );
        await seedCtx.SaveChangesAsync();

        var writer = new FakeContactWriter();
        var factory = CreateFactory(dbName);
        var handler = new StaleContactHandler(factory, writer, NullLogger<StaleContactHandler>.Instance);

        var tunnel = CreateTunnel(1, StalePolicy.AutoRemove);
        var currentSourceIds = new HashSet<int>(); // SourceUserId 99 is NOT in current set

        // Act
        var result = await handler.HandleStaleAsync(tunnel, phoneListId: 1, targetMailboxId: 1, mailboxEntraId: "mailbox@contoso.com", currentSourceUserIds: currentSourceIds, ct: CancellationToken.None);

        // Assert: DeleteContactAsync was called
        Assert.Contains("graph-id-abc", writer.DeletedContactIds);
        Assert.Equal(1, result.Removed);

        // Verify state was removed from DB
        using var verifyCtx = MakeDbContext(dbName);
        var remaining = await verifyCtx.ContactSyncStates.ToListAsync();
        Assert.Empty(remaining);
    }

    // ==============================
    // Test 3: FlagHold sets IsStale=true on first detection
    // ==============================

    [Fact]
    public async Task HandleStaleAsync_FlagHold_SetsIsStaleOnFirstDetection()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedCtx = MakeDbContext(dbName);
        seedCtx.ContactSyncStates.Add(
            new ContactSyncState
            {
                Id = 1,
                SourceUserId = 99,
                TunnelId = 1,
                PhoneListId = 1,
                TargetMailboxId = 1,
                GraphContactId = "graph-id-abc",
                IsStale = false,
                StaleDetectedAt = null
            }
        );
        await seedCtx.SaveChangesAsync();

        var writer = new FakeContactWriter();
        var factory = CreateFactory(dbName);
        var handler = new StaleContactHandler(factory, writer, NullLogger<StaleContactHandler>.Instance);

        var tunnel = CreateTunnel(1, StalePolicy.FlagHold, staleHoldDays: 14);
        var currentSourceIds = new HashSet<int>(); // stale

        // Act
        var result = await handler.HandleStaleAsync(tunnel, 1, 1, "mailbox@contoso.com", currentSourceIds, CancellationToken.None);

        // Assert: NOT deleted, just marked stale
        Assert.Empty(writer.DeletedContactIds);
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.StaleDetected);

        using var verifyCtx = MakeDbContext(dbName);
        var state = await verifyCtx.ContactSyncStates.FirstAsync();
        Assert.True(state.IsStale);
        Assert.NotNull(state.StaleDetectedAt);
    }

    // ==============================
    // Test 4: FlagHold deletes after hold period has passed
    // ==============================

    [Fact]
    public async Task HandleStaleAsync_FlagHold_DeletesWhenHoldPeriodPassed()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedCtx = MakeDbContext(dbName);
        seedCtx.ContactSyncStates.Add(
            new ContactSyncState
            {
                Id = 1,
                SourceUserId = 99,
                TunnelId = 1,
                PhoneListId = 1,
                TargetMailboxId = 1,
                GraphContactId = "graph-id-expired",
                IsStale = true,
                StaleDetectedAt = DateTime.UtcNow.AddDays(-15) // 15 days ago, past 14-day hold
            }
        );
        await seedCtx.SaveChangesAsync();

        var writer = new FakeContactWriter();
        var factory = CreateFactory(dbName);
        var handler = new StaleContactHandler(factory, writer, NullLogger<StaleContactHandler>.Instance);

        var tunnel = CreateTunnel(1, StalePolicy.FlagHold, staleHoldDays: 14);
        var currentSourceIds = new HashSet<int>(); // still stale

        // Act
        var result = await handler.HandleStaleAsync(tunnel, 1, 1, "mailbox@contoso.com", currentSourceIds, CancellationToken.None);

        // Assert: deleted
        Assert.Contains("graph-id-expired", writer.DeletedContactIds);
        Assert.Equal(1, result.Removed);
    }

    // ==============================
    // Test 5: FlagHold does NOT delete during hold period
    // ==============================

    [Fact]
    public async Task HandleStaleAsync_FlagHold_DoesNotDeleteDuringHoldPeriod()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedCtx = MakeDbContext(dbName);
        seedCtx.ContactSyncStates.Add(
            new ContactSyncState
            {
                Id = 1,
                SourceUserId = 99,
                TunnelId = 1,
                PhoneListId = 1,
                TargetMailboxId = 1,
                GraphContactId = "graph-id-pending",
                IsStale = true,
                StaleDetectedAt = DateTime.UtcNow.AddDays(-5) // only 5 days ago, within 14-day hold
            }
        );
        await seedCtx.SaveChangesAsync();

        var writer = new FakeContactWriter();
        var factory = CreateFactory(dbName);
        var handler = new StaleContactHandler(factory, writer, NullLogger<StaleContactHandler>.Instance);

        var tunnel = CreateTunnel(1, StalePolicy.FlagHold, staleHoldDays: 14);
        var currentSourceIds = new HashSet<int>(); // still stale

        // Act
        var result = await handler.HandleStaleAsync(tunnel, 1, 1, "mailbox@contoso.com", currentSourceIds, CancellationToken.None);

        // Assert: NOT deleted, still in hold period
        Assert.Empty(writer.DeletedContactIds);
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.StaleDetected);
    }

    // ==============================
    // Test 6: Leave policy marks stale but never deletes
    // ==============================

    [Fact]
    public async Task HandleStaleAsync_Leave_SetsIsStaleButNeverDeletes()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedCtx = MakeDbContext(dbName);
        seedCtx.ContactSyncStates.Add(
            new ContactSyncState
            {
                Id = 1,
                SourceUserId = 99,
                TunnelId = 1,
                PhoneListId = 1,
                TargetMailboxId = 1,
                GraphContactId = "graph-id-leave",
                IsStale = false,
                StaleDetectedAt = null
            }
        );
        await seedCtx.SaveChangesAsync();

        var writer = new FakeContactWriter();
        var factory = CreateFactory(dbName);
        var handler = new StaleContactHandler(factory, writer, NullLogger<StaleContactHandler>.Instance);

        var tunnel = CreateTunnel(1, StalePolicy.Leave);
        var currentSourceIds = new HashSet<int>(); // stale

        // Act
        var result = await handler.HandleStaleAsync(tunnel, 1, 1, "mailbox@contoso.com", currentSourceIds, CancellationToken.None);

        // Assert: Never deleted
        Assert.Empty(writer.DeletedContactIds);
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.StaleDetected);

        using var verifyCtx = MakeDbContext(dbName);
        var state = await verifyCtx.ContactSyncStates.FirstAsync();
        Assert.True(state.IsStale);
    }

    // ==============================
    // Test 7: Returns correct counts for run logging
    // ==============================

    [Fact]
    public async Task HandleStaleAsync_ReturnsCorrectCountsForRunLogging()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedCtx = MakeDbContext(dbName);
        // sourceUserId 1 = still active
        // sourceUserId 2 = stale (auto-remove)
        // sourceUserId 3 = stale (auto-remove)
        seedCtx.ContactSyncStates.AddRange(
            new ContactSyncState { Id = 1, SourceUserId = 1, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1, GraphContactId = "g1", IsStale = false },
            new ContactSyncState { Id = 2, SourceUserId = 2, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1, GraphContactId = "g2", IsStale = false },
            new ContactSyncState { Id = 3, SourceUserId = 3, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1, GraphContactId = "g3", IsStale = false }
        );
        await seedCtx.SaveChangesAsync();

        var writer = new FakeContactWriter();
        var factory = CreateFactory(dbName);
        var handler = new StaleContactHandler(factory, writer, NullLogger<StaleContactHandler>.Instance);

        var tunnel = CreateTunnel(1, StalePolicy.AutoRemove);
        var currentSourceIds = new HashSet<int> { 1 }; // only userId 1 remains active

        // Act
        var result = await handler.HandleStaleAsync(tunnel, 1, 1, "mailbox@contoso.com", currentSourceIds, CancellationToken.None);

        // Assert: 2 removed (users 2 and 3), 0 stale-detected
        Assert.Equal(2, result.Removed);
        Assert.Equal(0, result.StaleDetected);
        Assert.Equal(2, writer.DeletedContactIds.Count);
    }

    // ==============================
    // Test helper: FakeContactWriter
    // ==============================

    private sealed class FakeContactWriter : IContactWriter
    {
        public List<string> DeletedContactIds { get; } = [];
        public List<string> CreatedContactIds { get; } = [];
        public List<string> UpdatedContactIds { get; } = [];

        public Task<string> CreateContactAsync(string mailboxEntraId, string folderId, SortedDictionary<string, string> payload, CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString();
            CreatedContactIds.Add(id);
            return Task.FromResult(id);
        }

        public Task UpdateContactAsync(string mailboxEntraId, string graphContactId, SortedDictionary<string, string> payload, CancellationToken ct)
        {
            UpdatedContactIds.Add(graphContactId);
            return Task.CompletedTask;
        }

        public Task DeleteContactAsync(string mailboxEntraId, string graphContactId, CancellationToken ct)
        {
            DeletedContactIds.Add(graphContactId);
            return Task.CompletedTask;
        }
    }
}
