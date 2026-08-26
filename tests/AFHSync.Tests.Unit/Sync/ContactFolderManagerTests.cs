using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Tests for ContactFolderManager — folder identity by remembered Graph id (Phase 2 §2.5) with a
/// per-run cache. A subclass intercepts the four Graph seams so no real Graph call is made.
/// </summary>
public class ContactFolderManagerTests
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

    private static Tunnel T(int id, string name) => new() { Id = id, Name = name };
    private static TargetMailbox M(int id, string entraId) => new() { Id = id, EntraId = entraId, Email = $"{entraId}@test.com" };

    private static async Task SeedKnownFolderAsync(string dbName, int tunnelId, int mailboxId, string graphFolderId, string folderName)
    {
        using var ctx = MakeDbContext(dbName);
        ctx.TunnelMailboxFolders.Add(new TunnelMailboxFolder
        {
            TunnelId = tunnelId, TargetMailboxId = mailboxId, GraphFolderId = graphFolderId, FolderName = folderName, UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>In-memory Graph: folderId -> (mailboxEntraId, displayName).</summary>
    private sealed class FakeContactFolderManager : ContactFolderManager
    {
        public int LookupByIdCount { get; private set; }
        public int LookupByNameCount { get; private set; }
        public int CreateCount { get; private set; }
        public int RenameCount { get; private set; }
        public int GraphCallCount => LookupByIdCount + LookupByNameCount + CreateCount + RenameCount;

        /// <summary>When true, RenameFolderAsync counts the attempt then throws instead of renaming.</summary>
        public bool ThrowOnRename { get; set; }

        /// <summary>When true, RenameFolderAsync counts the attempt then throws OperationCanceledException.</summary>
        public bool ThrowOceOnRename { get; set; }

        public Dictionary<string, (string mailbox, string name)> Folders { get; }

        public FakeContactFolderManager(string dbName, Dictionary<string, (string mailbox, string name)>? folders = null)
            : base(null!, new TestDbContextFactory(dbName), NullLogger<ContactFolderManager>.Instance)
        {
            Folders = folders ?? new Dictionary<string, (string mailbox, string name)>();
        }

        protected override Task<GraphFolderInfo?> GetFolderByIdAsync(string mailboxEntraId, string folderId, CancellationToken ct)
        {
            LookupByIdCount++;
            return Task.FromResult(Folders.TryGetValue(folderId, out var f) && f.mailbox == mailboxEntraId
                ? new GraphFolderInfo(folderId, f.name)
                : null);
        }

        protected override Task<GraphFolderInfo?> FindFolderByNameAsync(string mailboxEntraId, string folderName, CancellationToken ct)
        {
            LookupByNameCount++;
            var hit = Folders.FirstOrDefault(kv => kv.Value.mailbox == mailboxEntraId
                && string.Equals(kv.Value.name, folderName, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(hit.Key is null ? null : new GraphFolderInfo(hit.Key, hit.Value.name));
        }

        protected override Task<string> CreateFolderAsync(string mailboxEntraId, string folderName, CancellationToken ct)
        {
            CreateCount++;
            var id = $"folder-{mailboxEntraId}-{CreateCount}";
            Folders[id] = (mailboxEntraId, folderName);
            return Task.FromResult(id);
        }

        protected override Task RenameFolderAsync(string mailboxEntraId, string folderId, string newName, CancellationToken ct)
        {
            RenameCount++;
            if (ThrowOnRename)
                throw new InvalidOperationException("simulated transient Graph failure on rename PATCH");
            if (ThrowOceOnRename)
                throw new OperationCanceledException("simulated shutdown mid-rename");
            Folders[folderId] = (mailboxEntraId, newName);
            return Task.CompletedTask;
        }
    }

    // ── cache behaviour (unchanged from before Phase 2) ───────────────────────

    [Fact]
    public async Task GetOrCreateFolderAsync_ReturnsCachedId_OnSecondCall_ForSameMailbox()
    {
        var dbName = Guid.NewGuid().ToString();
        var fake = new FakeContactFolderManager(dbName);
        var tunnel = T(1, "AFH Contacts");
        var mailbox = M(1, "mailbox-1");

        var (id1, created1) = await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);
        var calls = fake.GraphCallCount;
        var (id2, created2) = await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);

        Assert.Equal(id1, id2);
        Assert.True(created1);
        Assert.False(created2);
        Assert.Equal(calls, fake.GraphCallCount);   // second call hit the cache
    }

    [Fact]
    public async Task ResetCache_ClearsAllCachedEntries_ForcingNewGraphCalls()
    {
        var dbName = Guid.NewGuid().ToString();
        var fake = new FakeContactFolderManager(dbName);
        var tunnel = T(1, "AFH Contacts");
        var mailbox = M(1, "mailbox-x");

        await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);
        var calls = fake.GraphCallCount;

        fake.ResetCache();
        var (_, wasCreated) = await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);

        Assert.False(wasCreated);
        Assert.True(fake.GraphCallCount > calls);
    }

    // ── Phase 2 (2.5): identity by remembered id ─────────────────────────────

    [Fact]
    public async Task RememberedId_Found_IsUsedWithoutNameLookup()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedKnownFolderAsync(dbName, tunnelId: 1, mailboxId: 1, graphFolderId: "f-1", folderName: "Buckhead");
        var fake = new FakeContactFolderManager(dbName, new() { ["f-1"] = ("mailbox-1", "Buckhead") });

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "Buckhead"), M(1, "mailbox-1"), false, CancellationToken.None);

        Assert.Equal("f-1", id);
        Assert.False(wasCreated);
        Assert.Equal(1, fake.LookupByIdCount);
        Assert.Equal(0, fake.LookupByNameCount);
        Assert.Equal(0, fake.RenameCount);
    }

    [Fact]
    public async Task RememberedId_Gone404_FallsThroughToName_AndUpdatesRow()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedKnownFolderAsync(dbName, 1, 1, graphFolderId: "f-gone", folderName: "Buckhead");
        var fake = new FakeContactFolderManager(dbName, new() { ["f-2"] = ("mailbox-1", "Buckhead") });

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "Buckhead"), M(1, "mailbox-1"), false, CancellationToken.None);

        Assert.Equal("f-2", id);
        Assert.False(wasCreated);
        Assert.Equal(1, fake.LookupByIdCount);
        Assert.Equal(1, fake.LookupByNameCount);
        using var ctx = MakeDbContext(dbName);
        var row = await ctx.TunnelMailboxFolders.SingleAsync();
        Assert.Equal("f-2", row.GraphFolderId);
        Assert.Equal("Buckhead", row.FolderName);
    }

    [Fact]
    public async Task TunnelRenamed_PatchesDisplayName_AndUpdatesRow()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedKnownFolderAsync(dbName, 1, 1, graphFolderId: "f-1", folderName: "Old Name");
        var fake = new FakeContactFolderManager(dbName, new() { ["f-1"] = ("mailbox-1", "Old Name") });

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "New Name"), M(1, "mailbox-1"), false, CancellationToken.None);

        Assert.Equal("f-1", id);
        Assert.False(wasCreated);                                  // a rename is not a create ⇒ no state wipe
        Assert.Equal(1, fake.RenameCount);
        Assert.Equal("New Name", fake.Folders["f-1"].name);
        Assert.Equal(0, fake.CreateCount);
        using var ctx = MakeDbContext(dbName);
        Assert.Equal("New Name", (await ctx.TunnelMailboxFolders.SingleAsync()).FolderName);
    }

    [Fact]
    public async Task RenameFailure_IsLoggedAndSwallowed_ResolvedFolderStillUsed_RowKeepsOldName()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedKnownFolderAsync(dbName, 1, 1, graphFolderId: "f-1", folderName: "Old Name");
        var fake = new FakeContactFolderManager(dbName, new() { ["f-1"] = ("mailbox-1", "Old Name") }) { ThrowOnRename = true };

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "New Name"), M(1, "mailbox-1"), false, CancellationToken.None);

        Assert.Equal("f-1", id);                                   // still resolved and returned
        Assert.False(wasCreated);
        Assert.Equal(1, fake.RenameCount);                         // attempted once
        using var ctx = MakeDbContext(dbName);
        Assert.Equal("Old Name", (await ctx.TunnelMailboxFolders.SingleAsync()).FolderName);  // retried next run
    }

    // Important #4: unlike a real transient rename failure, shutdown cancellation during the
    // rename PATCH must propagate rather than being swallowed as "retry next run".
    [Fact]
    public async Task RenameThrowsOperationCanceled_PropagatesInsteadOfBeingSwallowed()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedKnownFolderAsync(dbName, 1, 1, graphFolderId: "f-1", folderName: "Old Name");
        var fake = new FakeContactFolderManager(dbName, new() { ["f-1"] = ("mailbox-1", "Old Name") }) { ThrowOceOnRename = true };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fake.GetOrCreateFolderAsync(T(1, "New Name"), M(1, "mailbox-1"), false, CancellationToken.None));

        Assert.Equal(1, fake.RenameCount);                         // attempted once
        using var ctx = MakeDbContext(dbName);
        Assert.Equal("Old Name", (await ctx.TunnelMailboxFolders.SingleAsync()).FolderName);  // upsert never ran
    }

    [Fact]
    public async Task WasCreated_TrueOnlyWhenCreated_AndRowIsUpserted()
    {
        var dbName = Guid.NewGuid().ToString();
        var fake = new FakeContactFolderManager(dbName, new() { ["f-existing"] = ("mailbox-a", "AFH Contacts") });

        var (idA, createdA) = await fake.GetOrCreateFolderAsync(T(1, "AFH Contacts"), M(1, "mailbox-a"), false, CancellationToken.None);
        var (idB, createdB) = await fake.GetOrCreateFolderAsync(T(1, "AFH Contacts"), M(2, "mailbox-b"), false, CancellationToken.None);

        Assert.Equal("f-existing", idA);
        Assert.False(createdA);                                    // found by name, no create
        Assert.True(createdB);                                     // created
        Assert.Equal(1, fake.CreateCount);
        using var ctx = MakeDbContext(dbName);
        var rows = await ctx.TunnelMailboxFolders.OrderBy(r => r.TargetMailboxId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("f-existing", rows[0].GraphFolderId);
        Assert.Equal(idB, rows[1].GraphFolderId);
        Assert.All(rows, r => Assert.Equal("AFH Contacts", r.FolderName));
    }

    [Fact]
    public async Task DryRun_NeverCreatesRenamesOrWritesTheRow()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedKnownFolderAsync(dbName, 1, 1, graphFolderId: "f-1", folderName: "Old Name");
        var fake = new FakeContactFolderManager(dbName, new() { ["f-1"] = ("mailbox-1", "Old Name") });

        // Known folder, tunnel renamed: a real run would PATCH; a dry run must not.
        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "New Name"), M(1, "mailbox-1"), true, CancellationToken.None);
        // Unknown mailbox: a real run would create; a dry run returns null.
        var (idMissing, createdMissing) = await fake.GetOrCreateFolderAsync(T(1, "New Name"), M(2, "mailbox-2"), true, CancellationToken.None);

        Assert.Equal("f-1", id);
        Assert.False(wasCreated);
        Assert.Null(idMissing);
        Assert.False(createdMissing);
        Assert.Equal(0, fake.RenameCount);
        Assert.Equal(0, fake.CreateCount);
        using var ctx = MakeDbContext(dbName);
        var row = await ctx.TunnelMailboxFolders.SingleAsync();
        Assert.Equal("Old Name", row.FolderName);                  // row untouched
        Assert.Equal(1, await ctx.TunnelMailboxFolders.CountAsync());
    }
}
