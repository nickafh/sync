using System.Collections.Concurrent;
using AFHSync.Shared.Entities;
using AFHSync.Worker.Graph;
using Microsoft.Graph.Models;

namespace AFHSync.Worker.Services;

/// <summary>A Graph contact folder as seen by the folder manager's Graph seams.</summary>
public sealed record GraphFolderInfo(string Id, string? DisplayName);

/// <summary>
/// Creates contact folders lazily per mailbox and caches their IDs in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> for the duration of a sync run.
///
/// Thread-safe: multiple parallel mailbox tasks (bounded by semaphore in SyncEngine)
/// may call <see cref="GetOrCreateFolderAsync"/> concurrently. A per-key lock ensures
/// only one Graph round-trip is made per (mailbox, tunnel) even under concurrent access.
///
/// Lifecycle: one instance per sync run scope (registered as Scoped in DI). The SyncEngine
/// calls <see cref="ResetCache"/> at the start of each run so stale folder IDs from
/// previous runs don't persist.
///
/// Graph SDK calls are <c>protected virtual</c> seams so unit tests can subclass this class.
/// </summary>
public class ContactFolderManager : IContactFolderManager
{
    private readonly GraphClientFactory? _graphClientFactory;
    private readonly ILogger<ContactFolderManager> _logger;

    // ConcurrentDictionary: key = "mailboxEntraId:tunnelId", value = folderId.
    private readonly ConcurrentDictionary<string, string> _folderCache = new();

    // Per-key locks to prevent concurrent Graph calls for the same folder.
    // Without this, two parallel tasks could both miss the cache and both POST
    // a folder create to Graph, resulting in duplicate folders.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();

    public ContactFolderManager(GraphClientFactory graphClientFactory, ILogger<ContactFolderManager> logger)
    {
        _graphClientFactory = graphClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(string? folderId, bool wasCreated)> GetOrCreateFolderAsync(
        Tunnel tunnel,
        TargetMailbox mailbox,
        bool isDryRun,
        CancellationToken ct)
    {
        var cacheKey = $"{mailbox.EntraId}:{tunnel.Id}";

        // Fast path: return cached folder ID without Graph call
        if (_folderCache.TryGetValue(cacheKey, out var cachedId))
            return (cachedId, false);

        // Slow path: acquire per-key lock so only one Graph call fires per folder
        var keyLock = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock — another task may have populated the cache
            if (_folderCache.TryGetValue(cacheKey, out cachedId))
                return (cachedId, false);

            var existing = await FindFolderByNameAsync(mailbox.EntraId, tunnel.Name, ct);
            if (existing is not null)
            {
                _logger.LogDebug(
                    "Found existing contact folder '{FolderName}' ({FolderId}) in mailbox {MailboxId}",
                    tunnel.Name, existing.Id, mailbox.EntraId);
                _folderCache.TryAdd(cacheKey, existing.Id);
                return (existing.Id, false);
            }

            if (isDryRun)
            {
                // Phase 2 (§2.2): dry runs never create. Not cached — a null is not a folder.
                _logger.LogInformation(
                    "Dry run: contact folder '{FolderName}' does not exist in mailbox {MailboxId} — would create",
                    tunnel.Name, mailbox.EntraId);
                return (null, false);
            }

            _logger.LogInformation(
                "Creating contact folder '{FolderName}' in mailbox {MailboxId}",
                tunnel.Name, mailbox.EntraId);
            var createdId = await CreateFolderAsync(mailbox.EntraId, tunnel.Name, ct);
            _folderCache.TryAdd(cacheKey, createdId);
            return (createdId, true);
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <inheritdoc />
    public void ResetCache()
    {
        _folderCache.Clear();
        _keyLocks.Clear();
        _logger.LogDebug("Contact folder cache cleared for new sync run");
    }

    private Microsoft.Graph.GraphServiceClient Client =>
        _graphClientFactory?.Client
        ?? throw new InvalidOperationException("GraphClientFactory is required for Graph operations");

    // ==============================
    // Protected virtual Graph seams (overridden in unit tests)
    // ==============================

    /// <summary>Queries Graph for a contact folder whose displayName equals <paramref name="folderName"/>.</summary>
    protected virtual async Task<GraphFolderInfo?> FindFolderByNameAsync(
        string mailboxEntraId, string folderName, CancellationToken ct)
    {
        var foldersResponse = await Client
            .Users[mailboxEntraId]
            .ContactFolders
            .GetAsync(config =>
            {
                var escapedName = folderName.Replace("'", "''");
                config.QueryParameters.Filter = $"displayName eq '{escapedName}'";
                config.QueryParameters.Top = 1;
            }, cancellationToken: ct);

        var existingFolder = foldersResponse?.Value?.FirstOrDefault();
        return existingFolder?.Id is null ? null : new GraphFolderInfo(existingFolder.Id, existingFolder.DisplayName);
    }

    /// <summary>Creates a contact folder and returns its id.</summary>
    protected virtual async Task<string> CreateFolderAsync(
        string mailboxEntraId, string folderName, CancellationToken ct)
    {
        var created = await Client
            .Users[mailboxEntraId]
            .ContactFolders
            .PostAsync(new ContactFolder { DisplayName = folderName }, cancellationToken: ct);

        if (created?.Id is null)
            throw new InvalidOperationException(
                $"Graph returned null folder ID after POST for mailbox {mailboxEntraId}");

        return created.Id;
    }
}
