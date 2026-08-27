using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Phase 3 (§3.7): strays (Graph contacts in the tunnel folder with no state row) are adopted when a
/// current source user matches by deterministic key and has no state row, and removed otherwise.
/// A subclass intercepts the Graph listing seam; a per-file fake writer records deletes.
/// </summary>
public class FolderReconcilerTests
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

    private sealed class RecordingContactWriter : IContactWriter
    {
        public List<string> DeletedContactIds { get; } = [];

        public Task<string> CreateContactAsync(string mailboxEntraId, string folderId, SortedDictionary<string, string> payload, CancellationToken ct)
            => throw new NotSupportedException();
        public Task UpdateContactAsync(string mailboxEntraId, string graphContactId, SortedDictionary<string, string> payload, CancellationToken ct)
            => throw new NotSupportedException();
        public Task DeleteContactAsync(string mailboxEntraId, string graphContactId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<Dictionary<string, BatchOperationResult>> CreateContactsBatchAsync(string mailboxEntraId, string folderId,
            List<(string key, SortedDictionary<string, string> payload)> operations,
            Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<Dictionary<string, BatchOperationResult>> UpdateContactsBatchAsync(string mailboxEntraId,
            List<(string key, string graphContactId, SortedDictionary<string, string> payload)> operations,
            Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Dictionary<string, BatchOperationResult>> DeleteContactsBatchAsync(string mailboxEntraId,
            List<(string key, string graphContactId)> operations, CancellationToken ct)
        {
            var results = new Dictionary<string, BatchOperationResult>();
            foreach (var (key, graphContactId) in operations)
            {
                DeletedContactIds.Add(graphContactId);
                results[key] = new BatchOperationResult(true);
            }
            return Task.FromResult(results);
        }
    }

    private sealed class FakeFolderReconciler : FolderReconciler
    {
        public List<GraphContactStub> FolderContacts { get; } = [];

        public FakeFolderReconciler(string dbName, RecordingContactWriter writer)
            : base(null!, new TestDbContextFactory(dbName), writer, NullLogger<FolderReconciler>.Instance) { }

        protected override Task<List<GraphContactStub>> ListFolderContactsAsync(string mailboxEntraId, string folderId, CancellationToken ct)
            => Task.FromResult(FolderContacts.ToList());
    }

    private static readonly Tunnel Tunnel = new() { Id = 1, Name = "Buckhead" };
    private static readonly TargetMailbox Mailbox = new() { Id = 7, EntraId = "mbx-7", Email = "seven@contoso.com" };

    private static async Task SeedStateAsync(string dbName, int sourceUserId, string graphContactId)
    {
        using var ctx = MakeDbContext(dbName);
        ctx.ContactSyncStates.Add(new ContactSyncState
        {
            SourceUserId = sourceUserId, PhoneListId = 1, TargetMailboxId = Mailbox.Id, TunnelId = Tunnel.Id,
            GraphContactId = graphContactId, DataHash = "h", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    [Theory]
    [InlineData("Alice@Contoso.com", "Alice", "alice@contoso.com")]
    [InlineData("  bob@contoso.com ", null, "bob@contoso.com")]
    [InlineData(null, "  Cara Lee ", "cara lee")]
    [InlineData("", "Dan", "dan")]
    [InlineData(null, "   ", null)]
    public void ContactKey_PrefersEmail_ThenDisplayName_LowerCased(string? email, string? displayName, string? expected)
        => Assert.Equal(expected, FolderReconciler.ContactKey(email, displayName));

    [Fact]
    public async Task Stray_MatchingSourceUserWithoutState_IsAdopted_WithNullHash()
    {
        var dbName = Guid.NewGuid().ToString();
        var writer = new RecordingContactWriter();
        var reconciler = new FakeFolderReconciler(dbName, writer);
        reconciler.FolderContacts.Add(new GraphContactStub("g-alice", "Alice", "alice@contoso.com"));
        var users = new List<SourceUser> { new() { Id = 1, EntraId = "u1", DisplayName = "Alice", Email = "ALICE@contoso.com" } };

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", canonicalPhoneListId: 1, users, CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(Examined: 1, Adopted: 1, Removed: 0), result);
        Assert.Empty(writer.DeletedContactIds);
        await using var verifyCtx = MakeDbContext(dbName);
        var state = await verifyCtx.ContactSyncStates.SingleAsync();
        Assert.Equal(1, state.SourceUserId);
        Assert.Equal("g-alice", state.GraphContactId);
        Assert.Equal(1, state.PhoneListId);
        Assert.Equal(Tunnel.Id, state.TunnelId);
        Assert.Equal(Mailbox.Id, state.TargetMailboxId);
        Assert.Null(state.DataHash);                                   // next classification PATCHes it
        Assert.Equal(FolderReconciler.AdoptedResult, state.LastResult);
    }

    [Fact]
    public async Task Stray_MatchingSourceUserThatAlreadyHasState_IsRemoved()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedStateAsync(dbName, sourceUserId: 1, graphContactId: "g-alice-real");
        var writer = new RecordingContactWriter();
        var reconciler = new FakeFolderReconciler(dbName, writer);
        reconciler.FolderContacts.Add(new GraphContactStub("g-alice-real", "Alice", "alice@contoso.com"));
        reconciler.FolderContacts.Add(new GraphContactStub("g-alice-dupe", "Alice", "alice@contoso.com"));
        var users = new List<SourceUser> { new() { Id = 1, EntraId = "u1", DisplayName = "Alice", Email = "alice@contoso.com" } };

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", 1, users, CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(2, 0, 1), result);
        Assert.Equal(new[] { "g-alice-dupe" }, writer.DeletedContactIds);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal(1, await verifyCtx.ContactSyncStates.CountAsync());   // no second row
    }

    [Fact]
    public async Task Stray_MatchingNobody_IsRemoved()
    {
        var dbName = Guid.NewGuid().ToString();
        var writer = new RecordingContactWriter();
        var reconciler = new FakeFolderReconciler(dbName, writer);
        reconciler.FolderContacts.Add(new GraphContactStub("g-ghost", "Ghost", "ghost@contoso.com"));
        var users = new List<SourceUser> { new() { Id = 1, EntraId = "u1", DisplayName = "Alice", Email = "alice@contoso.com" } };

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", 1, users, CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(1, 0, 1), result);
        Assert.Equal(new[] { "g-ghost" }, writer.DeletedContactIds);
    }

    [Fact]
    public async Task KnownContacts_AreLeftAlone_EvenWhenTheirUserLeftTheSource()
    {
        var dbName = Guid.NewGuid().ToString();
        await SeedStateAsync(dbName, sourceUserId: 9, graphContactId: "g-stale");   // stale handler's job, not ours
        var writer = new RecordingContactWriter();
        var reconciler = new FakeFolderReconciler(dbName, writer);
        reconciler.FolderContacts.Add(new GraphContactStub("g-stale", "Old Timer", "old@contoso.com"));

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", 1, [], CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(1, 0, 0), result);
        Assert.Empty(writer.DeletedContactIds);
    }

    [Fact]
    public async Task TwoStraysForOneUser_AdoptsTheFirst_RemovesTheSecond()
    {
        var dbName = Guid.NewGuid().ToString();
        var writer = new RecordingContactWriter();
        var reconciler = new FakeFolderReconciler(dbName, writer);
        reconciler.FolderContacts.Add(new GraphContactStub("g-1", "Alice", "alice@contoso.com"));
        reconciler.FolderContacts.Add(new GraphContactStub("g-2", "Alice", "alice@contoso.com"));
        var users = new List<SourceUser> { new() { Id = 1, EntraId = "u1", DisplayName = "Alice", Email = "alice@contoso.com" } };

        var result = await reconciler.ReconcileAsync(Tunnel, Mailbox, "folder", 1, users, CancellationToken.None);

        Assert.Equal(new FolderReconcileResult(2, 1, 1), result);
        Assert.Equal(new[] { "g-2" }, writer.DeletedContactIds);
        await using var verifyCtx = MakeDbContext(dbName);
        Assert.Equal("g-1", (await verifyCtx.ContactSyncStates.SingleAsync()).GraphContactId);
    }
}
