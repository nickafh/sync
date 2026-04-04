using System.Collections.Concurrent;
using AFHSync.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Tests for ContactFolderManager — lazy folder creation with ConcurrentDictionary cache.
/// Uses a FakeContactFolderManager subclass to intercept Graph calls and verify caching behavior.
/// </summary>
public class ContactFolderManagerTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A testable subclass of ContactFolderManager that overrides the Graph call
    /// to return a predictable folder ID without hitting the real Graph API.
    /// </summary>
    private sealed class FakeContactFolderManager : ContactFolderManager
    {
        public int GraphCallCount { get; private set; }

        // Maps mailboxId -> folderId for the fake backend
        private readonly Dictionary<string, string> _fakeBackend;

        public FakeContactFolderManager(Dictionary<string, string>? fakeBackend = null)
            : base(null!, NullLogger<ContactFolderManager>.Instance)
        {
            _fakeBackend = fakeBackend ?? new Dictionary<string, string>();
        }

        protected override Task<string> FetchOrCreateFolderFromGraphAsync(
            string mailboxEntraId, string folderName, CancellationToken ct)
        {
            GraphCallCount++;
            if (_fakeBackend.TryGetValue(mailboxEntraId, out var folderId))
                return Task.FromResult(folderId);
            var newId = $"folder-{mailboxEntraId}-{folderName}";
            _fakeBackend[mailboxEntraId] = newId;
            return Task.FromResult(newId);
        }
    }

    // ── Test 5: Returns cached folderId on second call ────────────────────────

    [Fact]
    public async Task GetOrCreateFolderAsync_ReturnsCachedId_OnSecondCall_ForSameMailbox()
    {
        var fake = new FakeContactFolderManager();
        const string mailboxId = "mailbox-1@test.com";

        var id1 = await fake.GetOrCreateFolderAsync(mailboxId, "AFH Contacts", CancellationToken.None);
        var id2 = await fake.GetOrCreateFolderAsync(mailboxId, "AFH Contacts", CancellationToken.None);

        Assert.Equal(id1, id2);
        Assert.Equal(1, fake.GraphCallCount); // Graph called only once — second hits cache
    }

    // ── Test 6: Returns different folderIds for different mailboxes ───────────

    [Fact]
    public async Task GetOrCreateFolderAsync_ReturnsDifferentIds_ForDifferentMailboxes()
    {
        var fake = new FakeContactFolderManager();

        var id1 = await fake.GetOrCreateFolderAsync("mailbox-a@test.com", "AFH Contacts", CancellationToken.None);
        var id2 = await fake.GetOrCreateFolderAsync("mailbox-b@test.com", "AFH Contacts", CancellationToken.None);

        Assert.NotEqual(id1, id2);
        Assert.Equal(2, fake.GraphCallCount); // separate Graph calls for each mailbox
    }

    // ── Test 7: ResetCache clears all cached entries ──────────────────────────

    [Fact]
    public async Task ResetCache_ClearsAllCachedEntries_ForcingNewGraphCalls()
    {
        var fake = new FakeContactFolderManager();
        const string mailboxId = "mailbox-x@test.com";

        // Populate cache
        await fake.GetOrCreateFolderAsync(mailboxId, "AFH Contacts", CancellationToken.None);
        Assert.Equal(1, fake.GraphCallCount);

        // Clear cache
        fake.ResetCache();

        // Next call should hit Graph again
        await fake.GetOrCreateFolderAsync(mailboxId, "AFH Contacts", CancellationToken.None);
        Assert.Equal(2, fake.GraphCallCount);
    }
}
