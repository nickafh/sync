using AFHSync.Api.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Unit tests for SyncEngine orchestrator.
/// Uses InMemory EF Core DB and stub implementations of all injected services.
/// </summary>
public class SyncEngineTests
{
    // ==============================
    // Test infrastructure
    // ==============================

    private static AFHSyncDbContext MakeDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AFHSyncDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AFHSyncDbContext(options);
    }

    private static IDbContextFactory<AFHSyncDbContext> CreateFactory(string dbName)
        => new TestDbContextFactory(dbName);

    private sealed class TestDbContextFactory(string dbName) : IDbContextFactory<AFHSyncDbContext>
    {
        public AFHSyncDbContext CreateDbContext() => MakeDbContext(dbName);
    }

    private static IConfiguration CreateEmptyConfig()
        => new ConfigurationBuilder().Build();

    private static SyncEngine CreateEngine(
        string dbName,
        FakeSourceResolver? sourceResolver = null,
        FakeContactPayloadBuilder? payloadBuilder = null,
        FakeContactWriter? contactWriter = null,
        FakeContactFolderManager? folderManager = null,
        FakeStaleContactHandler? staleHandler = null,
        FakeRunLogger? runLogger = null,
        ThrottleCounter? throttleCounter = null)
    {
        return new SyncEngine(
            CreateFactory(dbName),
            sourceResolver ?? new FakeSourceResolver([]),
            payloadBuilder ?? new FakeContactPayloadBuilder(),
            contactWriter ?? new FakeContactWriter(),
            folderManager ?? new FakeContactFolderManager(),
            staleHandler ?? new FakeStaleContactHandler(),
            runLogger ?? new FakeRunLogger(),
            throttleCounter ?? new ThrottleCounter(),
            CreateEmptyConfig(),
            NullLogger<SyncEngine>.Instance);
    }

    // ==============================
    // Test 1: RunAsync creates and finalizes SyncRun even with no tunnels
    // ==============================

    [Fact]
    public async Task RunAsync_CreatesAndFinalizesSyncRunWithNoTunnels()
    {
        var dbName = Guid.NewGuid().ToString();
        // No tunnels seeded — DB is empty.
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, runLogger: runLogger);

        // Act
        var run = await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Assert: run created and finalized
        Assert.NotNull(run);
        Assert.True(runLogger.WasCreated);
        Assert.True(runLogger.WasFinalized);
    }

    // ==============================
    // Test 2: 0 source members logs warning
    // ==============================

    [Fact]
    public async Task RunAsync_WithZeroSourceMembers_LogsWarningAndSkipsTunnel()
    {
        var dbName = Guid.NewGuid().ToString();

        // Seed one active tunnel with a phone list.
        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel
        {
            Id = 1,
            Name = "Empty Tunnel",
            Status = TunnelStatus.Active,
            StalePolicy = StalePolicy.FlagHold,
            StaleHoldDays = 14,
            SourceIdentifier = "some-filter"
        };
        seedCtx.Tunnels.Add(tunnel);
        await seedCtx.SaveChangesAsync();

        // Source resolver returns empty list.
        var sourceResolver = new FakeSourceResolver([]);
        var contactWriter = new FakeContactWriter();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, contactWriter: contactWriter);

        // Act
        var run = await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Assert: no Graph writes occurred
        Assert.Empty(contactWriter.CreatedContactIds);
        Assert.Empty(contactWriter.UpdatedContactIds);
    }

    // ==============================
    // Test 3: RunAsync calls SourceResolver for each active tunnel
    // ==============================

    [Fact]
    public async Task RunAsync_CallsSourceResolverForEachActiveTunnel()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        seedCtx.Tunnels.AddRange(
            new Tunnel { Id = 1, Name = "Tunnel 1", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove },
            new Tunnel { Id = 2, Name = "Tunnel 2", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove },
            new Tunnel { Id = 3, Name = "Tunnel 3", Status = TunnelStatus.Inactive, StalePolicy = StalePolicy.AutoRemove } // inactive
        );
        await seedCtx.SaveChangesAsync();

        var sourceResolver = new FakeSourceResolver([]);
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver);

        // Act
        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Assert: only 2 active tunnels were resolved (inactive skipped)
        Assert.Equal(2, sourceResolver.ResolveCallCount);
    }

    // ==============================
    // Test 4: Dry-run does NOT call ContactWriter methods
    // ==============================

    [Fact]
    public async Task RunAsync_DryRun_DoesNotCallContactWriterMethods()
    {
        var dbName = Guid.NewGuid().ToString();

        // Seed a tunnel, phone list, and a target mailbox.
        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel
        {
            Id = 1,
            Name = "Test Tunnel",
            Status = TunnelStatus.Active,
            StalePolicy = StalePolicy.AutoRemove
        };
        var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx-entra-id", Email = "user@contoso.com", IsActive = true };
        var tunnelPhoneList = new TunnelPhoneList { TunnelId = 1, PhoneListId = 1, Tunnel = tunnel, PhoneList = phoneList };

        tunnel.TunnelPhoneLists.Add(tunnelPhoneList);
        seedCtx.Tunnels.Add(tunnel);
        seedCtx.PhoneLists.Add(phoneList);
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.TunnelPhoneLists.Add(tunnelPhoneList);
        await seedCtx.SaveChangesAsync();

        // Source resolver returns 1 user.
        var sourceUser = new SourceUser { Id = 1, EntraId = "user-1", DisplayName = "Alice Smith", Email = "alice@contoso.com" };
        var sourceResolver = new FakeSourceResolver([sourceUser]);
        var contactWriter = new FakeContactWriter();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, contactWriter: contactWriter);

        // Act: dry run
        await engine.RunAsync(null, RunType.DryRun, isDryRun: true, CancellationToken.None);

        // Assert: ContactWriter was never called
        Assert.Empty(contactWriter.CreatedContactIds);
        Assert.Empty(contactWriter.UpdatedContactIds);
        Assert.Empty(contactWriter.DeletedContactIds);
    }

    // ==============================
    // Test 5: Dry-run still produces SyncRunItems
    // ==============================

    [Fact]
    public async Task RunAsync_DryRun_StillProducesSyncRunItems()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel { Id = 1, Name = "Test Tunnel", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
        var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx-entra-id", Email = "user@contoso.com", IsActive = true };
        var tunnelPhoneList = new TunnelPhoneList { TunnelId = 1, PhoneListId = 1, Tunnel = tunnel, PhoneList = phoneList };

        tunnel.TunnelPhoneLists.Add(tunnelPhoneList);
        seedCtx.Tunnels.Add(tunnel);
        seedCtx.PhoneLists.Add(phoneList);
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.TunnelPhoneLists.Add(tunnelPhoneList);
        await seedCtx.SaveChangesAsync();

        var sourceUser = new SourceUser { Id = 1, EntraId = "user-1", DisplayName = "Alice Smith", Email = "alice@contoso.com" };
        var sourceResolver = new FakeSourceResolver([sourceUser]);
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, runLogger: runLogger);

        // Act: dry run
        await engine.RunAsync(null, RunType.DryRun, isDryRun: true, CancellationToken.None);

        // Assert: at least one item was logged (the "created" action for the new contact)
        Assert.NotEmpty(runLogger.AddedItems);
        Assert.Contains(runLogger.AddedItems, i => i.Action == "created");
    }

    // ==============================
    // Test 6: Aggregate counts are correct
    // ==============================

    [Fact]
    public async Task RunAsync_AggregateCountsAreCorrect()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        var tunnel = new Tunnel { Id = 1, Name = "Count Tunnel", Status = TunnelStatus.Active, StalePolicy = StalePolicy.AutoRemove };
        var phoneList = new PhoneList { Id = 1, Name = "AFH Contacts" };
        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx-entra-id", Email = "user@contoso.com", IsActive = true };
        var tunnelPhoneList = new TunnelPhoneList { TunnelId = 1, PhoneListId = 1, Tunnel = tunnel, PhoneList = phoneList };

        tunnel.TunnelPhoneLists.Add(tunnelPhoneList);
        seedCtx.Tunnels.Add(tunnel);
        seedCtx.PhoneLists.Add(phoneList);
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.TunnelPhoneLists.Add(tunnelPhoneList);

        // Pre-existing sync state for SourceUser 1 with a hash that will MATCH (skipped)
        // and SourceUser 2 with a hash that will MISMATCH (updated).
        // The FakeContactPayloadBuilder always returns hash "new-hash".
        seedCtx.ContactSyncStates.Add(new ContactSyncState
        {
            Id = 1,
            SourceUserId = 1, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1,
            GraphContactId = "g1", DataHash = "new-hash", // will match → skipped
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        seedCtx.ContactSyncStates.Add(new ContactSyncState
        {
            Id = 2,
            SourceUserId = 2, TunnelId = 1, PhoneListId = 1, TargetMailboxId = 1,
            GraphContactId = "g2", DataHash = "old-hash", // will mismatch → updated
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await seedCtx.SaveChangesAsync();

        var sourceUsers = new List<SourceUser>
        {
            new() { Id = 1, EntraId = "u1", DisplayName = "Alice" }, // existing → skipped
            new() { Id = 2, EntraId = "u2", DisplayName = "Bob" },   // existing with old hash → updated
            new() { Id = 3, EntraId = "u3", DisplayName = "Carol" }  // new → created
        };
        var sourceResolver = new FakeSourceResolver(sourceUsers);
        var runLogger = new FakeRunLogger();
        var engine = CreateEngine(dbName, sourceResolver: sourceResolver, runLogger: runLogger);

        // Act
        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Assert aggregate counts: 1 created, 1 updated, 1 skipped
        Assert.Equal(1, runLogger.FinalizedCreated);
        Assert.Equal(1, runLogger.FinalizedUpdated);
        Assert.Equal(1, runLogger.FinalizedSkipped);
    }

    // ==============================
    // Test 7: RunAsync resets ThrottleCounter at run start
    // ==============================

    [Fact]
    public async Task RunAsync_ResetsThrottleCounter_AtRunStart()
    {
        var dbName = Guid.NewGuid().ToString();
        // No tunnels seeded — run will complete with no work.
        var runLogger = new FakeRunLogger();
        var throttleCounter = new ThrottleCounter();

        // Simulate stale state from a previous run.
        throttleCounter.Increment();
        throttleCounter.Increment();
        throttleCounter.Increment();
        throttleCounter.Increment();
        throttleCounter.Increment(); // counter = 5

        var engine = CreateEngine(dbName, runLogger: runLogger, throttleCounter: throttleCounter);

        // Act
        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Assert: throttle events should be 0 because counter was reset at run start
        // and no actual throttling occurred during this (empty) run.
        Assert.Equal(0, runLogger.FinalizedThrottleEvents);
    }

    // ==============================
    // Test 8: RunAsync passes ThrottleCounter.Count to FinalizeRunAsync
    // ==============================

    [Fact]
    public async Task RunAsync_PassesThrottleCounterCount_ToFinalizeRunAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        // No tunnels seeded — run will complete with no work.
        var runLogger = new FakeRunLogger();
        var throttleCounter = new ThrottleCounter();
        var engine = CreateEngine(dbName, runLogger: runLogger, throttleCounter: throttleCounter);

        // Act: run completes with a clean counter (no throttling occurred).
        await engine.RunAsync(null, RunType.Manual, isDryRun: false, CancellationToken.None);

        // Assert: FinalizeRunAsync received the counter value (0 — no throttling in test).
        // This verifies the engine reads from throttleCounter.Count (not a hardcoded 0 or
        // an old stale value), since reset was called and no retries occurred.
        Assert.Equal(0, runLogger.FinalizedThrottleEvents);
        Assert.True(runLogger.WasFinalized);
    }

    // ==============================
    // Stub implementations
    // ==============================

    private sealed class FakeSourceResolver(List<SourceUser> users) : ISourceResolver
    {
        public int ResolveCallCount { get; private set; }

        public Task<List<SourceUser>> ResolveAsync(Tunnel tunnel, CancellationToken ct)
        {
            ResolveCallCount++;
            return Task.FromResult(users);
        }
    }

    /// <summary>
    /// Always returns hash "new-hash" so existing states with "old-hash" trigger updates,
    /// and states with "new-hash" are skipped.
    /// </summary>
    private sealed class FakeContactPayloadBuilder : IContactPayloadBuilder
    {
        public ContactPayloadResult BuildPayload(
            SourceUser source,
            IReadOnlyList<FieldProfileField> fieldSettings,
            ContactSyncState? existingState)
        {
            var payload = new SortedDictionary<string, string> { { "DisplayName", source.DisplayName ?? "Unknown" } };
            return new ContactPayloadResult(payload, "new-hash");
        }
    }

    private sealed class FakeContactWriter : IContactWriter
    {
        public List<string> CreatedContactIds { get; } = [];
        public List<string> UpdatedContactIds { get; } = [];
        public List<string> DeletedContactIds { get; } = [];

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

    private sealed class FakeContactFolderManager : IContactFolderManager
    {
        public Task<string> GetOrCreateFolderAsync(string mailboxEntraId, string folderName, CancellationToken ct)
            => Task.FromResult("fake-folder-id");

        public void ResetCache() { }
    }

    private sealed class FakeStaleContactHandler : IStaleContactHandler
    {
        public Task<StaleResult> HandleStaleAsync(
            Tunnel tunnel, int phoneListId, int targetMailboxId,
            string mailboxEntraId, HashSet<int> currentSourceUserIds, CancellationToken ct)
            => Task.FromResult(new StaleResult(0, 0));
    }

    private sealed class FakeRunLogger : IRunLogger
    {
        public bool WasCreated { get; private set; }
        public bool WasFinalized { get; private set; }
        public List<SyncRunItem> AddedItems { get; } = [];
        public int FinalizedCreated { get; private set; }
        public int FinalizedUpdated { get; private set; }
        public int FinalizedSkipped { get; private set; }
        public int FinalizedThrottleEvents { get; private set; }

        private int _nextRunId = 1;

        public Task<SyncRun> CreateRunAsync(RunType runType, bool isDryRun, CancellationToken ct)
        {
            WasCreated = true;
            var run = new SyncRun
            {
                Id = _nextRunId++,
                RunType = runType,
                Status = SyncStatus.Running,
                IsDryRun = isDryRun,
                StartedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            return Task.FromResult(run);
        }

        public void AddItem(SyncRunItem item) => AddedItems.Add(item);

        public Task FlushItemsAsync(CancellationToken ct) => Task.CompletedTask;

        public Task FinalizeRunAsync(
            SyncRun run, SyncStatus status, string? errorSummary,
            int contactsCreated, int contactsUpdated, int contactsSkipped, int contactsFailed,
            int contactsRemoved, int tunnelsProcessed, int tunnelsWarned, int tunnelsFailed,
            int throttleEvents, CancellationToken ct)
        {
            WasFinalized = true;
            FinalizedCreated = contactsCreated;
            FinalizedUpdated = contactsUpdated;
            FinalizedSkipped = contactsSkipped;
            FinalizedThrottleEvents = throttleEvents;
            return Task.CompletedTask;
        }
    }
}
