using System.Collections.Concurrent;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Worker.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace AFHSync.Worker.Services;

/// <summary>A Graph contact folder as seen by the folder manager's Graph seams.</summary>
public sealed record GraphFolderInfo(string Id, string? DisplayName);

/// <summary>
/// Resolves the contact folder for a (tunnel, mailbox) pair and caches the id for the duration
/// of a sync run.
///
/// Phase 2 (§2.5) resolution order: run cache → remembered id in tunnel_mailbox_folders
/// (GET by id; 404 falls through) → search by name → create (never in a dry run) → upsert the
/// row → if the remembered name differs from tunnel.Name, PATCH displayName. This makes a
/// tunnel rename a rename on every phone instead of a brand-new folder (and a state wipe).
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
    private readonly IDbContextFactory<AFHSyncDbContext> _dbContextFactory;
    private readonly ILogger<ContactFolderManager> _logger;

    // ConcurrentDictionary: key = "mailboxEntraId:tunnelId", value = folderId.
    private readonly ConcurrentDictionary<string, string> _folderCache = new();

    // Per-key locks to prevent concurrent Graph calls for the same folder.
    // Without this, two parallel tasks could both miss the cache and both POST
    // a folder create to Graph, resulting in duplicate folders.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();

    public ContactFolderManager(
        GraphClientFactory graphClientFactory,
        IDbContextFactory<AFHSyncDbContext> dbContextFactory,
        ILogger<ContactFolderManager> logger)
    {
        _graphClientFactory = graphClientFactory;
        _dbContextFactory = dbContextFactory;
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

        // Slow path: acquire per-key lock so only one Graph round-trip fires per folder
        var keyLock = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock — another task may have populated the cache
            if (_folderCache.TryGetValue(cacheKey, out cachedId))
                return (cachedId, false);

            string? folderId = null;
            var wasCreated = false;
            var foundById = false;

            // (1) Remembered id → GET by id; 404 falls through.
            var known = await LoadKnownFolderAsync(tunnel.Id, mailbox.Id, ct);
            if (known is not null)
            {
                var byId = await GetFolderByIdAsync(mailbox.EntraId, known.GraphFolderId, ct);
                if (byId is not null)
                {
                    folderId = byId.Id;
                    foundById = true;
                }
                else
                {
                    _logger.LogInformation(
                        "Remembered folder {FolderId} for tunnel {TunnelId} in mailbox {MailboxId} is gone — falling back to name lookup",
                        known.GraphFolderId, tunnel.Id, mailbox.EntraId);
                }
            }

            // (2) Search by name.
            if (folderId is null)
            {
                var byName = await FindFolderByNameAsync(mailbox.EntraId, tunnel.Name, ct);
                if (byName is not null)
                {
                    _logger.LogDebug(
                        "Found existing contact folder '{FolderName}' ({FolderId}) in mailbox {MailboxId}",
                        tunnel.Name, byName.Id, mailbox.EntraId);
                    folderId = byName.Id;
                }
            }

            // (3) Create — never in a dry run (§2.2).
            if (folderId is null)
            {
                if (isDryRun)
                {
                    _logger.LogInformation(
                        "Dry run: contact folder '{FolderName}' does not exist in mailbox {MailboxId} — would create",
                        tunnel.Name, mailbox.EntraId);
                    return (null, false);
                }

                _logger.LogInformation(
                    "Creating contact folder '{FolderName}' in mailbox {MailboxId}",
                    tunnel.Name, mailbox.EntraId);
                folderId = await CreateFolderAsync(mailbox.EntraId, tunnel.Name, ct);
                wasCreated = true;
            }

            if (!isDryRun)
            {
                // (4) Rename when the remembered name differs from the tunnel's current name.
                // The folder id is already resolved above, so a rename PATCH is cosmetic — a
                // transient Graph failure here must not fail the whole mailbox for this run.
                // Log and leave the stored name unchanged so the mismatch is retried next run.
                var nameToStore = tunnel.Name;
                if (foundById && known is not null && !string.Equals(known.FolderName, tunnel.Name, StringComparison.Ordinal))
                {
                    try
                    {
                        _logger.LogInformation(
                            "Renaming contact folder {FolderId} in mailbox {MailboxId} from '{OldName}' to '{NewName}'",
                            folderId, mailbox.EntraId, known.FolderName, tunnel.Name);
                        await RenameFolderAsync(mailbox.EntraId, folderId, tunnel.Name, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Tunnel {TunnelName}: could not rename contact folder {FolderId} in mailbox {Mailbox} from '{OldName}' to '{NewName}'; will retry next run",
                            tunnel.Name, folderId, mailbox.EntraId, known.FolderName, tunnel.Name);
                        nameToStore = known.FolderName;
                    }
                }

                // (5) Remember id + current (or, on a failed rename, still-old) name.
                // CancellationToken.None: bookkeeping must survive a cancel.
                await UpsertKnownFolderAsync(tunnel.Id, mailbox.Id, folderId, nameToStore);
            }

            _folderCache.TryAdd(cacheKey, folderId);

            _logger.LogDebug(
                "Contact folder '{FolderName}' resolved to {FolderId} for mailbox {MailboxId} (created={Created})",
                tunnel.Name, folderId, mailbox.EntraId, wasCreated);

            return (folderId, wasCreated);
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

    // ==============================
    // tunnel_mailbox_folders bookkeeping
    // ==============================

    private async Task<TunnelMailboxFolder?> LoadKnownFolderAsync(int tunnelId, int mailboxId, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await db.TunnelMailboxFolders
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.TunnelId == tunnelId && f.TargetMailboxId == mailboxId, ct);
    }

    private async Task UpsertKnownFolderAsync(int tunnelId, int mailboxId, string graphFolderId, string folderName)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        var row = await db.TunnelMailboxFolders
            .FirstOrDefaultAsync(f => f.TunnelId == tunnelId && f.TargetMailboxId == mailboxId, CancellationToken.None);
        var now = DateTime.UtcNow;

        if (row is null)
        {
            db.TunnelMailboxFolders.Add(new TunnelMailboxFolder
            {
                TunnelId = tunnelId,
                TargetMailboxId = mailboxId,
                GraphFolderId = graphFolderId,
                FolderName = folderName,
                UpdatedAt = now
            });
        }
        else if (row.GraphFolderId != graphFolderId || row.FolderName != folderName)
        {
            row.GraphFolderId = graphFolderId;
            row.FolderName = folderName;
            row.UpdatedAt = now;
        }
        else
        {
            return;
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private Microsoft.Graph.GraphServiceClient Client =>
        _graphClientFactory?.Client
        ?? throw new InvalidOperationException("GraphClientFactory is required for Graph operations");

    // ==============================
    // Protected virtual Graph seams (overridden in unit tests)
    // ==============================

    /// <summary>GET /users/{mailbox}/contactFolders/{id}; null when Graph answers 404.</summary>
    protected virtual async Task<GraphFolderInfo?> GetFolderByIdAsync(
        string mailboxEntraId, string folderId, CancellationToken ct)
    {
        try
        {
            var folder = await Client
                .Users[mailboxEntraId]
                .ContactFolders[folderId]
                .GetAsync(config => config.QueryParameters.Select = ["id", "displayName"], cancellationToken: ct);

            return folder?.Id is null ? null : new GraphFolderInfo(folder.Id, folder.DisplayName);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }

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

    /// <summary>PATCH /users/{mailbox}/contactFolders/{id} { displayName }.</summary>
    protected virtual async Task RenameFolderAsync(
        string mailboxEntraId, string folderId, string newName, CancellationToken ct)
    {
        await Client
            .Users[mailboxEntraId]
            .ContactFolders[folderId]
            .PatchAsync(new ContactFolder { DisplayName = newName }, cancellationToken: ct);
    }
}
