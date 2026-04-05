using System.Security.Cryptography;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Graph;
using AFHSync.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Unit tests for PhotoSyncService.
/// Tests the static ComputePhotoHash method directly and uses InMemory EF Core DB
/// with a testable subclass that overrides Graph SDK calls.
/// </summary>
public class PhotoSyncServiceTests
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

    // ==============================
    // Test 1: ComputePhotoHash returns lowercase hex SHA-256
    // ==============================

    [Fact]
    public void ComputePhotoHash_ReturnsLowercaseHexSHA256()
    {
        // Known test vector: SHA-256 of empty byte array
        var emptyBytes = Array.Empty<byte>();
        var expectedHash = Convert.ToHexString(SHA256.HashData(emptyBytes)).ToLowerInvariant();

        var result = PhotoSyncService.ComputePhotoHash(emptyBytes);

        Assert.Equal(expectedHash, result);
        // Verify it's lowercase hex
        Assert.Equal(result, result.ToLowerInvariant());
        Assert.Equal(64, result.Length); // SHA-256 = 32 bytes = 64 hex chars
    }

    // ==============================
    // Test 2: ComputePhotoHash returns consistent hash for same bytes
    // ==============================

    [Fact]
    public void ComputePhotoHash_ReturnsSameHash_ForSameBytes()
    {
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }; // JPEG header snippet

        var hash1 = PhotoSyncService.ComputePhotoHash(photoBytes);
        var hash2 = PhotoSyncService.ComputePhotoHash(photoBytes);

        Assert.Equal(hash1, hash2);
    }

    // ==============================
    // Test 3: ComputePhotoHash returns different hash for different bytes
    // ==============================

    [Fact]
    public void ComputePhotoHash_ReturnsDifferentHash_ForDifferentBytes()
    {
        var bytes1 = new byte[] { 0x01, 0x02, 0x03 };
        var bytes2 = new byte[] { 0x04, 0x05, 0x06 };

        var hash1 = PhotoSyncService.ComputePhotoHash(bytes1);
        var hash2 = PhotoSyncService.ComputePhotoHash(bytes2);

        Assert.NotEqual(hash1, hash2);
    }

    // ==============================
    // Test 4: SyncPhotosForTunnelAsync skips when Photo field is NoSync
    // ==============================

    [Fact]
    public async Task SyncPhotosForTunnelAsync_SkipsWhenPhotoFieldIsNoSync()
    {
        var dbName = Guid.NewGuid().ToString();

        // Seed field profile with Photo field set to NoSync
        using var seedCtx = MakeDbContext(dbName);
        var fieldProfile = new FieldProfile { Id = 1, Name = "Test Profile", IsDefault = true };
        var photoField = new FieldProfileField
        {
            Id = 1, FieldProfileId = 1, FieldName = "Photo",
            FieldSection = "Photo", DisplayName = "Contact Photo",
            Behavior = SyncBehavior.Nosync, DisplayOrder = 50
        };
        fieldProfile.FieldProfileFields = new List<FieldProfileField> { photoField };
        seedCtx.FieldProfiles.Add(fieldProfile);
        seedCtx.FieldProfileFields.Add(photoField);
        await seedCtx.SaveChangesAsync();

        var tunnel = new Tunnel
        {
            Id = 1, Name = "Test Tunnel", Status = TunnelStatus.Active,
            PhotoSyncEnabled = true, FieldProfileId = 1, FieldProfile = fieldProfile
        };

        var run = new SyncRun { Id = 1, Status = SyncStatus.Running };
        var sourceUsers = new List<SourceUser>
        {
            new() { Id = 1, EntraId = "user-1", DisplayName = "Alice" }
        };

        var testable = CreateTestableService(dbName);

        var (updated, failed) = await testable.Service.SyncPhotosForTunnelAsync(
            tunnel, run, sourceUsers, isDryRun: false, CancellationToken.None);

        Assert.Equal(0, updated);
        Assert.Equal(0, failed);
        Assert.Equal(0, testable.PhotoFetchCount);
    }

    // ==============================
    // Test 5: SyncPhotosForTunnelAsync skips contacts where hash matches
    // ==============================

    [Fact]
    public async Task SyncPhotosForTunnelAsync_SkipsContactsWhereHashMatches()
    {
        var dbName = Guid.NewGuid().ToString();
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var photoHash = PhotoSyncService.ComputePhotoHash(photoBytes);

        using var seedCtx = MakeDbContext(dbName);
        SeedDefaultFieldProfile(seedCtx);

        // Source user already has current photo hash
        var sourceUser = new SourceUser
        {
            Id = 1, EntraId = "user-1", DisplayName = "Alice", PhotoHash = photoHash
        };
        seedCtx.SourceUsers.Add(sourceUser);

        // Contact sync state already has the same photo hash
        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx-1", Email = "mbx@test.com", IsActive = true };
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.ContactSyncStates.Add(new ContactSyncState
        {
            Id = 1, SourceUserId = 1, PhoneListId = 1, TargetMailboxId = 1,
            TunnelId = 1, GraphContactId = "contact-1", PhotoHash = photoHash,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await seedCtx.SaveChangesAsync();

        var tunnel = CreateTunnelWithDefaultProfile(seedCtx);
        var run = new SyncRun { Id = 1, Status = SyncStatus.Running };

        var testable = CreateTestableService(dbName);
        testable.SetPhoto("user-1", photoBytes);

        var (updated, failed) = await testable.Service.SyncPhotosForTunnelAsync(
            tunnel, run, new List<SourceUser> { sourceUser }, isDryRun: false, CancellationToken.None);

        // Hash matches on ContactSyncState, so no PUT calls
        Assert.Equal(0, updated);
        Assert.Equal(0, failed);
        Assert.Equal(0, testable.PhotoPutCount);
    }

    // ==============================
    // Test 6: SyncPhotosForTunnelAsync writes photo when hash differs
    // ==============================

    [Fact]
    public async Task SyncPhotosForTunnelAsync_WritesPhotoWhenHashDiffers()
    {
        var dbName = Guid.NewGuid().ToString();
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };

        using var seedCtx = MakeDbContext(dbName);
        SeedDefaultFieldProfile(seedCtx);

        var sourceUser = new SourceUser
        {
            Id = 1, EntraId = "user-1", DisplayName = "Alice", PhotoHash = "old-hash"
        };
        seedCtx.SourceUsers.Add(sourceUser);

        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx-1", Email = "mbx@test.com", IsActive = true };
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.ContactSyncStates.Add(new ContactSyncState
        {
            Id = 1, SourceUserId = 1, PhoneListId = 1, TargetMailboxId = 1,
            TunnelId = 1, GraphContactId = "contact-1", PhotoHash = "old-hash",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await seedCtx.SaveChangesAsync();

        var tunnel = CreateTunnelWithDefaultProfile(seedCtx);
        var run = new SyncRun { Id = 1, Status = SyncStatus.Running };

        var testable = CreateTestableService(dbName);
        testable.SetPhoto("user-1", photoBytes);

        var (updated, failed) = await testable.Service.SyncPhotosForTunnelAsync(
            tunnel, run, new List<SourceUser> { sourceUser }, isDryRun: false, CancellationToken.None);

        Assert.Equal(1, updated);
        Assert.Equal(0, failed);
        Assert.Equal(1, testable.PhotoPutCount);
        Assert.Contains(testable.RunLogger.AddedItems, i => i.Action == "photo_updated");
    }

    // ==============================
    // Test 7: SyncPhotosForTunnelAsync creates SyncRunItem with correct fields
    // ==============================

    [Fact]
    public async Task SyncPhotosForTunnelAsync_CreatesSyncRunItemForUpdate()
    {
        var dbName = Guid.NewGuid().ToString();
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };

        using var seedCtx = MakeDbContext(dbName);
        SeedDefaultFieldProfile(seedCtx);

        var sourceUser = new SourceUser { Id = 1, EntraId = "user-1", DisplayName = "Alice" };
        seedCtx.SourceUsers.Add(sourceUser);

        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx-1", Email = "mbx@test.com", IsActive = true };
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.ContactSyncStates.Add(new ContactSyncState
        {
            Id = 1, SourceUserId = 1, PhoneListId = 1, TargetMailboxId = 1,
            TunnelId = 1, GraphContactId = "contact-1", PhotoHash = null,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await seedCtx.SaveChangesAsync();

        var tunnel = CreateTunnelWithDefaultProfile(seedCtx);
        var run = new SyncRun { Id = 1, Status = SyncStatus.Running };

        var testable = CreateTestableService(dbName);
        testable.SetPhoto("user-1", photoBytes);

        await testable.Service.SyncPhotosForTunnelAsync(
            tunnel, run, new List<SourceUser> { sourceUser }, isDryRun: false, CancellationToken.None);

        var photoItem = testable.RunLogger.AddedItems.FirstOrDefault(i => i.Action == "photo_updated");
        Assert.NotNull(photoItem);
        Assert.Equal(1, photoItem.SyncRunId);
        Assert.Equal(1, photoItem.TunnelId);
        Assert.Equal(1, photoItem.SourceUserId);
    }

    // ==============================
    // Test 8: SyncPhotosForTunnelAsync dry-run fetches and hashes but does NOT PUT
    // ==============================

    [Fact]
    public async Task SyncPhotosForTunnelAsync_DryRun_SkipsPut()
    {
        var dbName = Guid.NewGuid().ToString();
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };

        using var seedCtx = MakeDbContext(dbName);
        SeedDefaultFieldProfile(seedCtx);

        var sourceUser = new SourceUser { Id = 1, EntraId = "user-1", DisplayName = "Alice" };
        seedCtx.SourceUsers.Add(sourceUser);

        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx-1", Email = "mbx@test.com", IsActive = true };
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.ContactSyncStates.Add(new ContactSyncState
        {
            Id = 1, SourceUserId = 1, PhoneListId = 1, TargetMailboxId = 1,
            TunnelId = 1, GraphContactId = "contact-1", PhotoHash = null,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await seedCtx.SaveChangesAsync();

        var tunnel = CreateTunnelWithDefaultProfile(seedCtx);
        var run = new SyncRun { Id = 1, Status = SyncStatus.Running };

        var testable = CreateTestableService(dbName);
        testable.SetPhoto("user-1", photoBytes);

        var (updated, failed) = await testable.Service.SyncPhotosForTunnelAsync(
            tunnel, run, new List<SourceUser> { sourceUser }, isDryRun: true, CancellationToken.None);

        // Dry run: reports as updated but no actual PUT
        Assert.Equal(1, updated);
        Assert.Equal(0, failed);
        Assert.Equal(0, testable.PhotoPutCount);
        Assert.True(testable.PhotoFetchCount > 0); // Still fetches for hash comparison
        Assert.Contains(testable.RunLogger.AddedItems, i => i.Action == "photo_updated");
    }

    // ==============================
    // Test 9: SyncPhotosForTunnelAsync handles photo removal as skip
    // ==============================

    [Fact]
    public async Task SyncPhotosForTunnelAsync_PhotoRemoval_LogsSkipped()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        SeedDefaultFieldProfile(seedCtx);

        // Source user had a photo before but now returns null from Graph
        var sourceUser = new SourceUser
        {
            Id = 1, EntraId = "user-1", DisplayName = "Alice", PhotoHash = "old-hash"
        };
        seedCtx.SourceUsers.Add(sourceUser);

        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx-1", Email = "mbx@test.com", IsActive = true };
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.ContactSyncStates.Add(new ContactSyncState
        {
            Id = 1, SourceUserId = 1, PhoneListId = 1, TargetMailboxId = 1,
            TunnelId = 1, GraphContactId = "contact-1", PhotoHash = "old-hash",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await seedCtx.SaveChangesAsync();

        var tunnel = CreateTunnelWithDefaultProfile(seedCtx);
        var run = new SyncRun { Id = 1, Status = SyncStatus.Running };

        // Graph returns null (user removed their photo / 404)
        var testable = CreateTestableService(dbName);
        // Don't set any photo -- fetching will return null

        var (updated, failed) = await testable.Service.SyncPhotosForTunnelAsync(
            tunnel, run, new List<SourceUser> { sourceUser }, isDryRun: false, CancellationToken.None);

        // Per RESEARCH.md: no DELETE endpoint for contact photos, log as skipped
        Assert.Equal(0, updated);
        Assert.Equal(0, failed);
        Assert.Contains(testable.RunLogger.AddedItems, i => i.Action == "photo_removal_skipped");
    }

    // ==============================
    // Test 10: SyncPhotosForTunnelAsync handles write failure gracefully
    // ==============================

    [Fact]
    public async Task SyncPhotosForTunnelAsync_HandlesWriteFailure()
    {
        var dbName = Guid.NewGuid().ToString();
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };

        using var seedCtx = MakeDbContext(dbName);
        SeedDefaultFieldProfile(seedCtx);

        var sourceUser = new SourceUser { Id = 1, EntraId = "user-1", DisplayName = "Alice" };
        seedCtx.SourceUsers.Add(sourceUser);

        var mailbox = new TargetMailbox { Id = 1, EntraId = "mbx-1", Email = "mbx@test.com", IsActive = true };
        seedCtx.TargetMailboxes.Add(mailbox);
        seedCtx.ContactSyncStates.Add(new ContactSyncState
        {
            Id = 1, SourceUserId = 1, PhoneListId = 1, TargetMailboxId = 1,
            TunnelId = 1, GraphContactId = "contact-1", PhotoHash = null,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await seedCtx.SaveChangesAsync();

        var tunnel = CreateTunnelWithDefaultProfile(seedCtx);
        var run = new SyncRun { Id = 1, Status = SyncStatus.Running };

        var testable = CreateTestableService(dbName);
        testable.SetPhoto("user-1", photoBytes);
        testable.FailPuts = true; // Simulate Graph PUT failure

        var (updated, failed) = await testable.Service.SyncPhotosForTunnelAsync(
            tunnel, run, new List<SourceUser> { sourceUser }, isDryRun: false, CancellationToken.None);

        Assert.Equal(0, updated);
        Assert.Equal(1, failed);
        Assert.Contains(testable.RunLogger.AddedItems, i => i.Action == "photo_failed");
    }

    // ==============================
    // Test 11: RunAllAsync loads active tunnels and processes each
    // ==============================

    [Fact]
    public async Task RunAllAsync_LoadsActiveTunnelsAndProcessesEach()
    {
        var dbName = Guid.NewGuid().ToString();

        using var seedCtx = MakeDbContext(dbName);
        SeedDefaultFieldProfile(seedCtx);

        // Seed app_settings for separate_pass mode
        seedCtx.AppSettings.Add(new AppSetting
        {
            Id = 100, Key = "photo_sync_mode", Value = "separate_pass",
            Description = "Test", UpdatedAt = DateTime.UtcNow
        });

        // Seed two active tunnels and one inactive
        seedCtx.Tunnels.AddRange(
            new Tunnel
            {
                Id = 1, Name = "Tunnel 1", Status = TunnelStatus.Active,
                PhotoSyncEnabled = true, FieldProfileId = 1
            },
            new Tunnel
            {
                Id = 2, Name = "Tunnel 2", Status = TunnelStatus.Active,
                PhotoSyncEnabled = true, FieldProfileId = 1
            },
            new Tunnel
            {
                Id = 3, Name = "Tunnel 3", Status = TunnelStatus.Inactive,
                PhotoSyncEnabled = true, FieldProfileId = 1
            }
        );
        await seedCtx.SaveChangesAsync();

        var testable = CreateTestableService(dbName);

        await testable.Service.RunAllAsync(RunType.Scheduled, isDryRun: false, CancellationToken.None);

        // Should have created a run
        Assert.True(testable.RunLogger.WasCreated);
        // Should have finalized the run
        Assert.True(testable.RunLogger.WasFinalized);
    }

    // ==============================
    // Test helpers
    // ==============================

    private static void SeedDefaultFieldProfile(AFHSyncDbContext ctx)
    {
        var fieldProfile = new FieldProfile { Id = 1, Name = "Default Profile", IsDefault = true };
        var photoField = new FieldProfileField
        {
            Id = 21, FieldProfileId = 1, FieldName = "Photo",
            FieldSection = "Photo", DisplayName = "Contact Photo",
            Behavior = SyncBehavior.Always, DisplayOrder = 50
        };
        fieldProfile.FieldProfileFields = new List<FieldProfileField> { photoField };
        ctx.FieldProfiles.Add(fieldProfile);
        ctx.FieldProfileFields.Add(photoField);
    }

    private static Tunnel CreateTunnelWithDefaultProfile(AFHSyncDbContext ctx)
    {
        var fp = ctx.FieldProfiles.Local.First();
        return new Tunnel
        {
            Id = 1, Name = "Test Tunnel", Status = TunnelStatus.Active,
            PhotoSyncEnabled = true, FieldProfileId = 1, FieldProfile = fp
        };
    }

    private static TestablePhotoSyncContext CreateTestableService(string dbName)
    {
        var runLogger = new FakeRunLogger();
        var service = new TestablePhotoSyncService(
            CreateFactory(dbName), runLogger,
            new ThrottleCounter(), NullLogger<PhotoSyncService>.Instance);
        return new TestablePhotoSyncContext(service, runLogger);
    }

    // ==============================
    // Testable subclass and fakes
    // ==============================

    /// <summary>
    /// Wraps the TestablePhotoSyncService with its FakeRunLogger for convenient access
    /// to assertion helpers in tests.
    /// </summary>
    private sealed class TestablePhotoSyncContext(TestablePhotoSyncService service, FakeRunLogger runLogger)
    {
        public TestablePhotoSyncService Service { get; } = service;
        public FakeRunLogger RunLogger { get; } = runLogger;

        public int PhotoFetchCount => Service.PhotoFetchCount;
        public int PhotoPutCount => Service.PhotoPutCount;
        public bool FailPuts { get => Service.FailPuts; set => Service.FailPuts = value; }

        public void SetPhoto(string entraId, byte[] photoBytes) => Service.SetPhoto(entraId, photoBytes);
    }

    /// <summary>
    /// Testable subclass of PhotoSyncService that overrides Graph SDK calls.
    /// FetchUserPhotoAsync and WriteContactPhotoAsync are protected virtual in the base class
    /// (following the ContactFolderManager.FetchOrCreateFolderFromGraphAsync pattern).
    /// </summary>
    private sealed class TestablePhotoSyncService : PhotoSyncService
    {
        private readonly Dictionary<string, byte[]> _photos = new();
        public int PhotoFetchCount { get; private set; }
        public int PhotoPutCount { get; private set; }
        public bool FailPuts { get; set; }

        public TestablePhotoSyncService(
            IDbContextFactory<AFHSyncDbContext> dbContextFactory,
            IRunLogger runLogger,
            ThrottleCounter throttleCounter,
            ILogger<PhotoSyncService> logger)
            : base(dbContextFactory, null!, runLogger, throttleCounter, logger)
        {
        }

        public void SetPhoto(string entraId, byte[] photoBytes) => _photos[entraId] = photoBytes;

        protected override Task<byte[]?> FetchUserPhotoAsync(string entraId, CancellationToken ct)
        {
            PhotoFetchCount++;
            return Task.FromResult(_photos.TryGetValue(entraId, out var bytes) ? bytes : null);
        }

        protected override Task WriteContactPhotoAsync(
            string mailboxEntraId, string graphContactId, byte[] photoBytes, CancellationToken ct)
        {
            if (FailPuts)
                throw new InvalidOperationException("Simulated Graph PUT failure");
            PhotoPutCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunLogger : IRunLogger
    {
        public bool WasCreated { get; private set; }
        public bool WasFinalized { get; private set; }
        public List<SyncRunItem> AddedItems { get; } = [];

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
            return Task.CompletedTask;
        }
    }
}
