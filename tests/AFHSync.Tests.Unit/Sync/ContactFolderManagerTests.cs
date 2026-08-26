using AFHSync.Shared.Entities;
using AFHSync.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Tests for ContactFolderManager — lazy folder creation with a per-run cache. A subclass
/// intercepts the Graph seams so no real Graph call is made.
/// </summary>
public class ContactFolderManagerTests
{
    private static Tunnel T(int id, string name) => new() { Id = id, Name = name };
    private static TargetMailbox M(int id, string entraId) => new() { Id = id, EntraId = entraId, Email = $"{entraId}@test.com" };

    private sealed class FakeContactFolderManager : ContactFolderManager
    {
        public int LookupCount { get; private set; }
        public int CreateCount { get; private set; }
        public int GraphCallCount => LookupCount + CreateCount;

        // mailboxEntraId -> folderId for the fake backend (one folder per mailbox is enough here)
        private readonly Dictionary<string, string> _foldersByMailbox;

        public FakeContactFolderManager(Dictionary<string, string>? backend = null)
            : base(null!, NullLogger<ContactFolderManager>.Instance)
        {
            _foldersByMailbox = backend ?? new Dictionary<string, string>();
        }

        protected override Task<GraphFolderInfo?> FindFolderByNameAsync(string mailboxEntraId, string folderName, CancellationToken ct)
        {
            LookupCount++;
            return Task.FromResult(_foldersByMailbox.TryGetValue(mailboxEntraId, out var id)
                ? new GraphFolderInfo(id, folderName)
                : null);
        }

        protected override Task<string> CreateFolderAsync(string mailboxEntraId, string folderName, CancellationToken ct)
        {
            CreateCount++;
            var id = $"folder-{mailboxEntraId}-{folderName}";
            _foldersByMailbox[mailboxEntraId] = id;
            return Task.FromResult(id);
        }
    }

    [Fact]
    public async Task GetOrCreateFolderAsync_ReturnsCachedId_OnSecondCall_ForSameMailbox()
    {
        var fake = new FakeContactFolderManager();
        var tunnel = T(1, "AFH Contacts");
        var mailbox = M(1, "mailbox-1");

        var (id1, created1) = await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);
        var (id2, created2) = await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);

        Assert.Equal(id1, id2);
        Assert.True(created1);
        Assert.False(created2);
        Assert.Equal(2, fake.GraphCallCount); // one lookup + one create; second call hits the cache
    }

    [Fact]
    public async Task GetOrCreateFolderAsync_ReturnsDifferentIds_ForDifferentMailboxes()
    {
        var fake = new FakeContactFolderManager();
        var tunnel = T(1, "AFH Contacts");

        var (id1, _) = await fake.GetOrCreateFolderAsync(tunnel, M(1, "mailbox-a"), false, CancellationToken.None);
        var (id2, _) = await fake.GetOrCreateFolderAsync(tunnel, M(2, "mailbox-b"), false, CancellationToken.None);

        Assert.NotEqual(id1, id2);
        Assert.Equal(2, fake.CreateCount);
    }

    [Fact]
    public async Task ResetCache_ClearsAllCachedEntries_ForcingNewGraphCalls()
    {
        var fake = new FakeContactFolderManager();
        var tunnel = T(1, "AFH Contacts");
        var mailbox = M(1, "mailbox-x");

        await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);
        Assert.Equal(2, fake.GraphCallCount);

        fake.ResetCache();

        var (_, wasCreated) = await fake.GetOrCreateFolderAsync(tunnel, mailbox, false, CancellationToken.None);
        Assert.False(wasCreated);                 // the backend already has it now
        Assert.Equal(3, fake.GraphCallCount);     // one more lookup, no create
    }

    [Fact]
    public async Task GetOrCreateFolderAsync_ExistingFolder_IsNotReportedAsCreated()
    {
        var fake = new FakeContactFolderManager(new() { ["mailbox-1"] = "existing-folder" });

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "AFH Contacts"), M(1, "mailbox-1"), false, CancellationToken.None);

        Assert.Equal("existing-folder", id);
        Assert.False(wasCreated);
        Assert.Equal(0, fake.CreateCount);
    }

    // ── Phase 2 (2.2): dry runs never create ─────────────────────────────────

    [Fact]
    public async Task GetOrCreateFolderAsync_DryRun_MissingFolder_ReturnsNullAndNeverCreates()
    {
        var fake = new FakeContactFolderManager();

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "AFH Contacts"), M(1, "mailbox-1"), true, CancellationToken.None);

        Assert.Null(id);
        Assert.False(wasCreated);
        Assert.Equal(1, fake.LookupCount);
        Assert.Equal(0, fake.CreateCount);
    }

    [Fact]
    public async Task GetOrCreateFolderAsync_DryRun_ExistingFolder_ReturnsItsId()
    {
        var fake = new FakeContactFolderManager(new() { ["mailbox-1"] = "existing-folder" });

        var (id, wasCreated) = await fake.GetOrCreateFolderAsync(T(1, "AFH Contacts"), M(1, "mailbox-1"), true, CancellationToken.None);

        Assert.Equal("existing-folder", id);
        Assert.False(wasCreated);
    }
}
