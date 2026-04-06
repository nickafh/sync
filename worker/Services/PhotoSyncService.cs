using System.Security.Cryptography;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models.ODataErrors;

namespace AFHSync.Worker.Services;

/// <summary>
/// Fetches source user photos from Microsoft Graph, computes SHA-256 hashes for delta
/// comparison, and writes changed photos to target contact records. Implements fetch-once
/// write-many pattern: each source user photo is fetched once and distributed to all target
/// contacts where the hash differs.
///
/// Photo sync uses lower concurrency (SemaphoreSlim(2)) than contact sync (4) because
/// photo PUT operations send binary payloads (~200KB each) which are heavier on the Graph API.
///
/// Graph SDK calls are <c>protected virtual</c> to enable test subclassing without complex
/// Graph SDK mock chains (following ContactFolderManager pattern per Phase 2 decision).
/// </summary>
public class PhotoSyncService : IPhotoSyncService
{
    private readonly IDbContextFactory<AFHSyncDbContext> _dbContextFactory;
    private readonly GraphClientFactory? _graphClientFactory;
    private readonly IRunLogger _runLogger;
    private readonly ThrottleCounter _throttleCounter;
    private readonly ILogger<PhotoSyncService> _logger;

    /// <summary>
    /// Lower concurrency for photo writes than contact sync (D-04).
    /// Photo PUT operations send binary payloads (~200KB each).
    /// </summary>
    private readonly SemaphoreSlim _photoSemaphore = new(2);

    /// <summary>
    /// Batch size for initial backfill processing (D-12).
    /// </summary>
    private const int BatchSize = 50;

    /// <summary>
    /// Pause between batches during initial backfill (D-12).
    /// </summary>
    private const int BatchPauseMs = 2000;

    /// <summary>
    /// Maximum photo payload size in bytes (Graph API limit: 4MB).
    /// Threat mitigation T-06-02.
    /// </summary>
    private const int MaxPhotoSizeBytes = 4 * 1024 * 1024;

    public PhotoSyncService(
        IDbContextFactory<AFHSyncDbContext> dbContextFactory,
        GraphClientFactory graphClientFactory,
        IRunLogger runLogger,
        ThrottleCounter throttleCounter,
        ILogger<PhotoSyncService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _graphClientFactory = graphClientFactory;
        _runLogger = runLogger;
        _throttleCounter = throttleCounter;
        _logger = logger;
    }

    /// <summary>
    /// Computes a lowercase hex SHA-256 hash of the given photo bytes.
    /// Public static for direct unit testing (following ContactPayloadBuilder pattern).
    /// </summary>
    public static string ComputePhotoHash(byte[] photoBytes)
    {
        var hashBytes = SHA256.HashData(photoBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <inheritdoc />
    public async Task<(int updated, int failed)> SyncPhotosForTunnelAsync(
        Tunnel tunnel,
        SyncRun run,
        List<SourceUser> sourceUsers,
        bool isDryRun,
        CancellationToken ct)
    {
        // Step a: Check Photo field SyncBehavior from field profile
        var photoBehavior = GetPhotoSyncBehavior(tunnel);
        if (photoBehavior == SyncBehavior.Nosync)
        {
            _logger.LogInformation(
                "Photo sync skipped for tunnel {TunnelId} -- Photo field set to NoSync",
                tunnel.Id);
            return (0, 0);
        }

        // Step b: Fetch source photos with fetch-once pattern (D-06)
        var sourcePhotos = new Dictionary<int, (byte[]? bytes, string? hash)>();

        foreach (var sourceUser in sourceUsers)
        {
            try
            {
                var photoBytes = await FetchUserPhotoAsync(sourceUser.EntraId, ct);

                if (photoBytes != null)
                {
                    // Validate photo size (T-06-02: max 4MB)
                    if (photoBytes.Length > MaxPhotoSizeBytes)
                    {
                        _logger.LogWarning(
                            "Photo for user {EntraId} exceeds 4MB limit ({Size} bytes), skipping",
                            sourceUser.EntraId, photoBytes.Length);
                        sourcePhotos[sourceUser.Id] = (null, null);
                        continue;
                    }

                    var hash = ComputePhotoHash(photoBytes);
                    sourcePhotos[sourceUser.Id] = (photoBytes, hash);

                    // Update source user photo hash if changed
                    if (sourceUser.PhotoHash != hash)
                    {
                        sourceUser.PhotoHash = hash;
                        await UpdateSourceUserPhotoHashAsync(sourceUser.Id, hash, ct);
                    }
                }
                else
                {
                    sourcePhotos[sourceUser.Id] = (null, null);

                    // Source user removed their photo
                    if (sourceUser.PhotoHash != null)
                    {
                        sourceUser.PhotoHash = null;
                        await UpdateSourceUserPhotoHashAsync(sourceUser.Id, null, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to fetch photo for source user {EntraId}", sourceUser.EntraId);
                sourcePhotos[sourceUser.Id] = (null, null);
            }
        }

        var photosFound = sourcePhotos.Count(p => p.Value.bytes != null);
        _logger.LogInformation(
            "Photo fetch complete for tunnel {TunnelId}: {Total} users, {Found} with photos, {Missing} without",
            tunnel.Id, sourcePhotos.Count, photosFound, sourcePhotos.Count - photosFound);

        // Step c: Load ContactSyncState records for this tunnel where GraphContactId is not null
        // (contact must exist before photo write per D-01)
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var contactStates = await db.ContactSyncStates
            .Include(s => s.TargetMailbox)
            .Where(s => s.TunnelId == tunnel.Id && s.GraphContactId != null)
            .ToListAsync(ct);

        if (contactStates.Count == 0)
        {
            _logger.LogInformation(
                "No synced contacts found for tunnel {TunnelId}, skipping photo sync",
                tunnel.Id);
            return (0, 0);
        }

        // Step d: Group by target mailbox for semaphore-bounded parallel processing (D-04)
        var byMailbox = contactStates
            .GroupBy(s => s.TargetMailboxId)
            .ToList();

        int totalUpdated = 0, totalFailed = 0;
        var counterLock = new object();

        // Detect backfill scenario: all contacts have null PhotoHash on their ContactSyncState
        var isBackfill = contactStates.All(s => s.PhotoHash == null);

        var mailboxTasks = byMailbox.Select(async mailboxGroup =>
        {
            await _photoSemaphore.WaitAsync(ct);
            try
            {
                var states = mailboxGroup.ToList();
                var mailboxEntraId = states.First().TargetMailbox.EntraId;

                var (updated, failed) = await ProcessMailboxPhotosAsync(
                    tunnel, run, mailboxEntraId, states, sourcePhotos,
                    photoBehavior, isBackfill, isDryRun, ct);

                lock (counterLock)
                {
                    totalUpdated += updated;
                    totalFailed += failed;
                }
            }
            finally
            {
                _photoSemaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(mailboxTasks);

        _logger.LogInformation(
            "Photo sync for tunnel {TunnelId}: Updated={Updated}, Failed={Failed}",
            tunnel.Id, totalUpdated, totalFailed);

        return (totalUpdated, totalFailed);
    }

    /// <inheritdoc />
    public async Task RunAllAsync(RunType runType, bool isDryRun, CancellationToken ct)
    {
        // Read photo_sync_mode once at start (T-06-04: prevents mid-run mode switch)
        await using var settingsDb = await _dbContextFactory.CreateDbContextAsync(ct);
        var modeSetting = await settingsDb.AppSettings
            .FirstOrDefaultAsync(s => s.Key == "photo_sync_mode", ct);
        var photoSyncMode = modeSetting?.Value ?? "included";

        if (photoSyncMode != "separate_pass")
        {
            _logger.LogInformation(
                "Photo sync mode is '{Mode}', not 'separate_pass' -- RunAllAsync is a no-op",
                photoSyncMode);
            return;
        }

        // Check for running sync to avoid overlap
        var runningSync = await settingsDb.SyncRuns
            .AnyAsync(r => r.Status == SyncStatus.Running, ct);
        if (runningSync)
        {
            _logger.LogWarning("A sync run is already in progress, skipping photo sync");
            return;
        }

        // Create a SyncRun for this photo sync pass
        var run = await _runLogger.CreateRunAsync(runType, isDryRun, ct);
        _logger.LogInformation("Photo sync RunAllAsync starting RunId={RunId}", run.Id);

        int totalPhotosUpdated = 0, totalPhotosFailed = 0;
        int tunnelsWithPhotos = 0;
        string? fatalError = null;

        try
        {
            // Load active tunnels with includes
            await using var tunnelDb = await _dbContextFactory.CreateDbContextAsync(ct);
            var tunnels = await tunnelDb.Tunnels
                .Where(t => t.Status == TunnelStatus.Active)
                .Include(t => t.FieldProfile)
                    .ThenInclude(fp => fp!.FieldProfileFields)
                .Include(t => t.TunnelPhoneLists)
                    .ThenInclude(tpl => tpl.PhoneList)
                .ToListAsync(ct);

            tunnelsWithPhotos = tunnels.Count(t => t.PhotoSyncEnabled);

            foreach (var tunnel in tunnels)
            {
                // Per-tunnel opt-out (D-13)
                if (!tunnel.PhotoSyncEnabled)
                {
                    _logger.LogInformation(
                        "Photo sync disabled for tunnel {TunnelId} ({TunnelName}), skipping",
                        tunnel.Id, tunnel.Name);
                    continue;
                }

                try
                {
                    // Load source users that have synced contacts for this tunnel
                    var sourceUsers = await LoadSourceUsersForTunnelAsync(tunnel.Id, ct);

                    var (updated, failed) = await SyncPhotosForTunnelAsync(
                        tunnel, run, sourceUsers, isDryRun, ct);

                    totalPhotosUpdated += updated;
                    totalPhotosFailed += failed;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Photo sync failed for tunnel {TunnelId} ({TunnelName})",
                        tunnel.Id, tunnel.Name);
                }
            }

            // Flush items — use CancellationToken.None so shutdown doesn't discard buffered items.
            await _runLogger.FlushItemsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            fatalError = $"Photo sync run failed with unhandled exception: {ex.Message}";
            _logger.LogError(ex, "Photo sync RunAllAsync {RunId} failed with unhandled exception", run.Id);
        }

        var finalStatus = fatalError != null
            ? SyncStatus.Failed
            : totalPhotosFailed > 0 ? SyncStatus.Warning : SyncStatus.Success;

        // Finalize — always runs, even after fatal exceptions.
        try
        {
            await _runLogger.FinalizeRunAsync(
                run,
                status: finalStatus,
                errorSummary: fatalError ?? (totalPhotosFailed > 0 ? $"{totalPhotosFailed} photo(s) failed" : null),
                contactsCreated: 0,
                contactsUpdated: 0,
                contactsSkipped: 0,
                contactsFailed: 0,
                contactsRemoved: 0,
                tunnelsProcessed: tunnelsWithPhotos,
                tunnelsWarned: 0,
                tunnelsFailed: 0,
                throttleEvents: _throttleCounter.Count,
                photosUpdated: totalPhotosUpdated,
                photosFailed: totalPhotosFailed,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "CRITICAL: Failed to finalize photo sync RunId={RunId}", run.Id);
        }

        _logger.LogInformation(
            "Photo sync RunAllAsync complete: RunId={RunId}, PhotosUpdated={Updated}, PhotosFailed={Failed}",
            run.Id, totalPhotosUpdated, totalPhotosFailed);
    }

    // ==============================
    // Protected virtual methods for Graph SDK calls (testable)
    // ==============================

    /// <summary>
    /// Fetches a user's profile photo bytes from Microsoft Graph.
    /// Returns null if the user has no photo (404 is expected, not an error).
    /// Protected virtual for test subclassing (following ContactFolderManager pattern).
    /// </summary>
    protected virtual async Task<byte[]?> FetchUserPhotoAsync(string entraId, CancellationToken ct)
    {
        try
        {
            var stream = await _graphClientFactory!.Client
                .Users[entraId]
                .Photo.Content
                .GetAsync(cancellationToken: ct);

            if (stream is null)
            {
                _logger.LogDebug("Photo stream null for user {EntraId}", entraId);
                return null;
            }

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            _logger.LogDebug("Fetched photo for user {EntraId}: {Size} bytes", entraId, ms.Length);
            return ms.ToArray();
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            // User has no photo -- expected, not an error
            return null;
        }
        catch (ODataError ex)
        {
            // Non-404 OData error (e.g., 403 Forbidden = missing permission)
            _logger.LogWarning("Graph photo error for user {EntraId}: {StatusCode} {Message}",
                entraId, ex.ResponseStatusCode, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes photo bytes to a target contact via Microsoft Graph.
    /// Creates a new MemoryStream for each call to avoid stream exhaustion (Pitfall 2).
    /// Protected virtual for test subclassing.
    /// </summary>
    protected virtual async Task WriteContactPhotoAsync(
        string mailboxEntraId, string graphContactId, byte[] photoBytes, CancellationToken ct)
    {
        using var stream = new MemoryStream(photoBytes);
        await _graphClientFactory!.Client
            .Users[mailboxEntraId]
            .Contacts[graphContactId]
            .Photo.Content
            .PutAsync(stream, cancellationToken: ct);
    }

    // ==============================
    // Private helpers
    // ==============================

    private static SyncBehavior GetPhotoSyncBehavior(Tunnel tunnel)
    {
        var photoField = tunnel.FieldProfile?.FieldProfileFields?
            .FirstOrDefault(f => f.FieldName == "Photo");
        return photoField?.Behavior ?? SyncBehavior.Always;
    }

    private async Task<(int updated, int failed)> ProcessMailboxPhotosAsync(
        Tunnel tunnel,
        SyncRun run,
        string mailboxEntraId,
        List<ContactSyncState> states,
        Dictionary<int, (byte[]? bytes, string? hash)> sourcePhotos,
        SyncBehavior photoBehavior,
        bool isBackfill,
        bool isDryRun,
        CancellationToken ct)
    {
        int updated = 0, failed = 0;

        // Step e: Batched processing for backfill (D-12)
        for (int i = 0; i < states.Count; i += BatchSize)
        {
            var batch = states.Skip(i).Take(BatchSize).ToList();

            foreach (var state in batch)
            {
                if (!sourcePhotos.TryGetValue(state.SourceUserId, out var photoData))
                    continue;

                var (photoBytes, sourceHash) = photoData;

                // Photo removal case
                if (photoBytes == null && state.PhotoHash != null)
                {
                    // Per RESEARCH.md: no DELETE endpoint for contact photos.
                    // Log as skipped for v1.
                    if (photoBehavior == SyncBehavior.Always || photoBehavior == SyncBehavior.RemoveBlank)
                    {
                        _runLogger.AddItem(new SyncRunItem
                        {
                            SyncRunId = run.Id,
                            TunnelId = tunnel.Id,
                            TargetMailboxId = state.TargetMailboxId,
                            SourceUserId = state.SourceUserId,
                            Action = "photo_removal_skipped",
                            CreatedAt = DateTime.UtcNow
                        });
                        _logger.LogDebug(
                            "Photo removal skipped for contact {GraphContactId} -- no Graph DELETE endpoint",
                            state.GraphContactId);
                    }
                    continue;
                }

                // Skip if no photo to write
                if (photoBytes == null)
                    continue;

                // Skip if hash matches (no change)
                if (sourceHash == state.PhotoHash)
                    continue;

                // Write photo to target contact
                try
                {
                    if (!isDryRun)
                    {
                        await WriteContactPhotoAsync(
                            mailboxEntraId, state.GraphContactId!, photoBytes, ct);
                    }

                    // Update ContactSyncState photo hash
                    await UpdateContactSyncStatePhotoHashAsync(
                        state.Id, sourceHash!, state.PhotoHash, ct);

                    _runLogger.AddItem(new SyncRunItem
                    {
                        SyncRunId = run.Id,
                        TunnelId = tunnel.Id,
                        TargetMailboxId = state.TargetMailboxId,
                        SourceUserId = state.SourceUserId,
                        Action = "photo_updated",
                        CreatedAt = DateTime.UtcNow
                    });
                    updated++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to write photo to contact {GraphContactId} in mailbox {MailboxId}",
                        state.GraphContactId, mailboxEntraId);

                    _runLogger.AddItem(new SyncRunItem
                    {
                        SyncRunId = run.Id,
                        TunnelId = tunnel.Id,
                        TargetMailboxId = state.TargetMailboxId,
                        SourceUserId = state.SourceUserId,
                        Action = "photo_failed",
                        ErrorMessage = ex.Message,
                        CreatedAt = DateTime.UtcNow
                    });
                    failed++;
                }
            }

            // Pause between batches during backfill (D-12)
            if (isBackfill && i + BatchSize < states.Count)
            {
                await Task.Delay(BatchPauseMs, ct);
            }
        }

        return (updated, failed);
    }

    private async Task UpdateSourceUserPhotoHashAsync(int sourceUserId, string? photoHash, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var user = await db.SourceUsers.FindAsync([sourceUserId], ct);
            if (user != null)
            {
                user.PhotoHash = photoHash;
                user.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to update PhotoHash for SourceUser {Id}", sourceUserId);
        }
    }

    private async Task UpdateContactSyncStatePhotoHashAsync(
        int stateId, string newHash, string? previousHash, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var state = await db.ContactSyncStates.FindAsync([stateId], ct);
            if (state != null)
            {
                state.PreviousPhotoHash = previousHash;
                state.PhotoHash = newHash;
                state.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to update PhotoHash for ContactSyncState {Id}", stateId);
        }
    }

    private async Task<List<SourceUser>> LoadSourceUsersForTunnelAsync(int tunnelId, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var sourceUserIds = await db.ContactSyncStates
            .Where(s => s.TunnelId == tunnelId && s.GraphContactId != null)
            .Select(s => s.SourceUserId)
            .Distinct()
            .ToListAsync(ct);

        return await db.SourceUsers
            .Where(u => sourceUserIds.Contains(u.Id))
            .ToListAsync(ct);
    }
}
