using System.Collections.Concurrent;
using AFHSync.Worker.Graph;
using Microsoft.Graph.Models;

namespace AFHSync.Worker.Services;

/// <summary>
/// Creates contact folders lazily per mailbox and caches their IDs in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> for the duration of a sync run.
///
/// Thread-safe: multiple parallel mailbox tasks (bounded by semaphore in SyncEngine)
/// may call <see cref="GetOrCreateFolderAsync"/> concurrently. ConcurrentDictionary
/// ensures only one Graph call is made per mailbox even under concurrent access.
///
/// Lifecycle: one instance per sync run scope (registered as Scoped in DI). The SyncEngine
/// calls <see cref="ResetCache"/> at the start of each run so stale folder IDs from
/// previous runs don't persist.
/// </summary>
public class ContactFolderManager : IContactFolderManager
{
    private readonly GraphClientFactory? _graphClientFactory;
    private readonly ILogger<ContactFolderManager> _logger;

    // ConcurrentDictionary: key = mailboxEntraId, value = folderId.
    // Thread-safe for concurrent mailbox processing (D-14: parallelism at mailbox level).
    private readonly ConcurrentDictionary<string, string> _folderCache = new();

    public ContactFolderManager(GraphClientFactory graphClientFactory, ILogger<ContactFolderManager> logger)
    {
        _graphClientFactory = graphClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> GetOrCreateFolderAsync(
        string mailboxEntraId,
        string folderName,
        CancellationToken ct)
    {
        // Fast path: return cached folder ID without Graph call
        if (_folderCache.TryGetValue(mailboxEntraId, out var cachedId))
            return cachedId;

        // Slow path: hit Graph to find or create the folder
        var folderId = await FetchOrCreateFolderFromGraphAsync(mailboxEntraId, folderName, ct);

        // TryAdd is safe even if another thread beat us here — both threads get the same
        // folder ID from Graph (idempotent folder names) so the value doesn't matter.
        _folderCache.TryAdd(mailboxEntraId, folderId);

        _logger.LogDebug(
            "Contact folder '{FolderName}' resolved to {FolderId} for mailbox {MailboxId}",
            folderName, folderId, mailboxEntraId);

        return folderId;
    }

    /// <inheritdoc />
    public void ResetCache()
    {
        _folderCache.Clear();
        _logger.LogDebug("Contact folder cache cleared for new sync run");
    }

    /// <summary>
    /// Queries Graph for an existing contact folder matching <paramref name="folderName"/>,
    /// creating it if not found. Extracted as a virtual method for unit test overriding.
    /// </summary>
    protected virtual async Task<string> FetchOrCreateFolderFromGraphAsync(
        string mailboxEntraId, string folderName, CancellationToken ct)
    {
        if (_graphClientFactory is null)
            throw new InvalidOperationException(
                "GraphClientFactory is required for Graph operations");

        // Query existing folders matching the display name
        var foldersResponse = await _graphClientFactory.Client
            .Users[mailboxEntraId]
            .ContactFolders
            .GetAsync(config =>
            {
                config.QueryParameters.Filter = $"displayName eq '{folderName}'";
                config.QueryParameters.Top = 1;
            }, cancellationToken: ct);

        var existingFolder = foldersResponse?.Value?.FirstOrDefault();

        if (existingFolder?.Id is not null)
        {
            _logger.LogDebug(
                "Found existing contact folder '{FolderName}' ({FolderId}) in mailbox {MailboxId}",
                folderName, existingFolder.Id, mailboxEntraId);
            return existingFolder.Id;
        }

        // Folder not found — create it
        _logger.LogInformation(
            "Creating contact folder '{FolderName}' in mailbox {MailboxId}",
            folderName, mailboxEntraId);

        var created = await _graphClientFactory.Client
            .Users[mailboxEntraId]
            .ContactFolders
            .PostAsync(new ContactFolder { DisplayName = folderName }, cancellationToken: ct);

        if (created?.Id is null)
            throw new InvalidOperationException(
                $"Graph returned null folder ID after POST for mailbox {mailboxEntraId}");

        return created.Id;
    }
}
