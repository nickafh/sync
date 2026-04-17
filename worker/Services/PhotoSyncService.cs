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
    private readonly IContactFolderManager _contactFolderManager;
    private readonly IRunLogger _runLogger;
    private readonly ThrottleCounter _throttleCounter;
    private readonly ILogger<PhotoSyncService> _logger;

    /// <summary>
    /// Concurrency for photo writes (D-04). Thumbnails are 5-20KB each.
    /// </summary>
    private readonly SemaphoreSlim _photoSemaphore = new(4);

    /// <summary>
    /// Concurrency for photo fetches. Keep low to avoid Graph 503s.
    /// </summary>
    private readonly SemaphoreSlim _fetchSemaphore = new(4);

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
        IContactFolderManager contactFolderManager,
        IRunLogger runLogger,
        ThrottleCounter throttleCounter,
        ILogger<PhotoSyncService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _graphClientFactory = graphClientFactory;
        _contactFolderManager = contactFolderManager;
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
        // Parallel fetches bounded by _fetchSemaphore to avoid Graph throttling.
        // OPTIMIZATION: Skip fetching for users whose SourceUser.PhotoHash already matches
        // all their ContactSyncState.PhotoHash entries — the photo hasn't changed.
        // IMPORTANT: Don't skip if ANY state still has null PhotoHash (e.g. wiped mailbox
        // with freshly-created contacts that need the photo pushed).
        Dictionary<int, string?> existingPhotoHashes;
        HashSet<int> sourceUsersNeedingPhotos;
        {
            await using var hashDb = await _dbContextFactory.CreateDbContextAsync(ct);
            existingPhotoHashes = await hashDb.ContactSyncStates
                .Where(s => s.TunnelId == tunnel.Id && s.GraphContactId != null && s.PhotoHash != null)
                .GroupBy(s => s.SourceUserId)
                .Select(g => new { SourceUserId = g.Key, Hash = g.Min(s => s.PhotoHash) })
                .ToDictionaryAsync(x => x.SourceUserId, x => x.Hash, ct);

            sourceUsersNeedingPhotos = (await hashDb.ContactSyncStates
                .Where(s => s.TunnelId == tunnel.Id && s.GraphContactId != null && s.PhotoHash == null)
                .Select(s => s.SourceUserId)
                .Distinct()
                .ToListAsync(ct))
                .ToHashSet();
        }

        var sourcePhotos = new System.Collections.Concurrent.ConcurrentDictionary<int, (byte[]? bytes, string? hash)>();
        int fetchSuccess = 0, fetchNotFound = 0, fetchError = 0, fetchOversized = 0, fetchSkipped = 0;

        var fetchTasks = sourceUsers.Select(async sourceUser =>
        {
            // Skip fetch if source photo hash matches the already-synced hash
            // AND every contact state already has the photo (no null PhotoHash).
            // This avoids hundreds of Graph API calls on steady-state runs while
            // still fetching when a wiped/new mailbox needs the photo.
            if (sourceUser.PhotoHash != null
                && existingPhotoHashes.TryGetValue(sourceUser.Id, out var syncedHash)
                && syncedHash == sourceUser.PhotoHash
                && !sourceUsersNeedingPhotos.Contains(sourceUser.Id))
            {
                sourcePhotos[sourceUser.Id] = (null, sourceUser.PhotoHash);
                Interlocked.Increment(ref fetchSkipped);
                return;
            }

            await _fetchSemaphore.WaitAsync(ct);
            try
            {
                // Route to the correct photo endpoint based on source type and directory match.
                // Stub Contacts that point to other tenant shared mailboxes (e.g. IMMY-AFH, OTTO-AFH)
                // have no photo on the Contact endpoint — OWA renders the linked user's photo. When
                // directory enrichment matched the contact's email to a tenant user, prefer the linked
                // user's photo and fall back to the Contact endpoint only if the user has no photo.
                var mailboxSource = sourceUser.MailboxType == "MailboxContact"
                    ? tunnel.TunnelSources?.FirstOrDefault(s => s.SourceType == AFHSync.Shared.Enums.SourceType.MailboxContacts)
                    : null;

                byte[]? photoBytes;
                bool wasNotFound;

                if (mailboxSource != null && !string.IsNullOrWhiteSpace(sourceUser.MatchedUserEntraId))
                {
                    // Try the linked tenant user first (mirrors OWA's render-time behavior).
                    (photoBytes, wasNotFound) = await FetchUserPhotoAsync(sourceUser.MatchedUserEntraId!, ct);

                    // Conservative fallback: if the linked user has no photo (genuine 404), fall through
                    // to the Contact endpoint so a contact-level photo (rare but possible) still wins.
                    // Transient errors (wasNotFound == false with null bytes) are NOT retried via fallback.
                    if (photoBytes == null && wasNotFound)
                    {
                        (photoBytes, wasNotFound) = await FetchContactPhotoAsync(
                            mailboxSource.SourceIdentifier, sourceUser.EntraId, ct);
                    }
                }
                else if (mailboxSource != null)
                {
                    // No matched user — stub Contact with no linked tenant user. Existing behavior.
                    (photoBytes, wasNotFound) = await FetchContactPhotoAsync(
                        mailboxSource.SourceIdentifier, sourceUser.EntraId, ct);
                }
                else
                {
                    // DDG / OrgContacts / UserMailbox — existing behavior.
                    (photoBytes, wasNotFound) = await FetchUserPhotoAsync(sourceUser.EntraId, ct);
                }

                if (photoBytes != null)
                {
                    if (photoBytes.Length > MaxPhotoSizeBytes)
                    {
                        _logger.LogWarning(
                            "Photo for user {EntraId} exceeds 4MB limit ({Size} bytes), skipping",
                            sourceUser.EntraId, photoBytes.Length);
                        sourcePhotos[sourceUser.Id] = (null, null);
                        Interlocked.Increment(ref fetchOversized);
                        return;
                    }

                    var hash = ComputePhotoHash(photoBytes);
                    sourcePhotos[sourceUser.Id] = (photoBytes, hash);
                    Interlocked.Increment(ref fetchSuccess);

                    if (sourceUser.PhotoHash != hash)
                    {
                        sourceUser.PhotoHash = hash;
                        await UpdateSourceUserPhotoHashAsync(sourceUser.Id, hash, ct);
                    }
                }
                else
                {
                    sourcePhotos[sourceUser.Id] = (null, null);

                    if (wasNotFound)
                        Interlocked.Increment(ref fetchNotFound);
                    else
                        Interlocked.Increment(ref fetchError);

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
                Interlocked.Increment(ref fetchError);
            }
            finally
            {
                _fetchSemaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(fetchTasks);

        // Convert to Dictionary now that concurrent writes are done
        var sourcePhotoResults = new Dictionary<int, (byte[]? bytes, string? hash)>(sourcePhotos);

        var photosFound = sourcePhotoResults.Count(p => p.Value.bytes != null);
        _logger.LogInformation(
            "Photo fetch complete for tunnel {TunnelId}: {Total} users, {Skipped} unchanged, {Found} fetched with photos, {Missing} without (Success={Success}, NotFound404={NotFound}, Error={Error}, Oversized={Oversized})",
            tunnel.Id, sourcePhotoResults.Count, fetchSkipped, photosFound, sourcePhotoResults.Count - photosFound - fetchSkipped,
            fetchSuccess, fetchNotFound, fetchError, fetchOversized);

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

        // Diagnostic: log ContactSyncState counts and hash state
        var nullPhotoHashCount = contactStates.Count(s => s.PhotoHash == null);
        var distinctMailboxes = contactStates.Select(s => s.TargetMailboxId).Distinct().Count();
        var distinctSourceUsers = contactStates.Select(s => s.SourceUserId).Distinct().Count();
        _logger.LogInformation(
            "Photo sync state for tunnel {TunnelId}: ContactSyncStates={Total}, NullPhotoHash={NullHash}, NonNullPhotoHash={NonNullHash}, DistinctMailboxes={Mailboxes}, DistinctSourceUsers={SourceUsers}",
            tunnel.Id, contactStates.Count, nullPhotoHashCount, contactStates.Count - nullPhotoHashCount,
            distinctMailboxes, distinctSourceUsers);

        // Step d: Filter out contacts where the photo was skipped (already matching hash).
        // Then group by target mailbox for semaphore-bounded parallel processing (D-04).
        var skippedSourceUserIds = sourcePhotoResults
            .Where(p => p.Value.bytes == null && p.Value.hash != null)
            .Select(p => p.Key)
            .ToHashSet();

        var contactsToProcess = skippedSourceUserIds.Count > 0
            ? contactStates.Where(s => !skippedSourceUserIds.Contains(s.SourceUserId)).ToList()
            : contactStates;

        if (contactsToProcess.Count < contactStates.Count)
            _logger.LogInformation(
                "Photo sync for tunnel {TunnelId}: skipping {Skipped} contact states (photos unchanged), processing {Remaining}",
                tunnel.Id, contactStates.Count - contactsToProcess.Count, contactsToProcess.Count);

        var byMailbox = contactsToProcess
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

                // Resolve the contact folder ID for this mailbox+tunnel.
                // Photos must be written via the ContactFolders path since contacts
                // live in a subfolder (e.g. "Buckhead-test"), not the root contacts
                // collection. The flat /contacts/{id}/photo path does not reliably
                // resolve the photo sub-resource for subfolder contacts.
                var (folderId, _) = await _contactFolderManager.GetOrCreateFolderAsync(
                    mailboxEntraId, tunnel.Name, ct);

                var (updated, failed) = await ProcessMailboxPhotosAsync(
                    tunnel, run, mailboxEntraId, folderId, states, sourcePhotoResults,
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
    public async Task RunAllAsync(RunType runType, bool isDryRun, CancellationToken ct, bool skipRunningCheck = false)
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

        // Check for running sync to avoid overlap — skip when called from auto-trigger
        // (the auto-trigger runs inside the active sync, so the check always sees itself).
        if (!skipRunningCheck)
        {
            var runningSync = await settingsDb.SyncRuns
                .AnyAsync(r => r.Status == SyncStatus.Running, ct);
            if (runningSync)
            {
                _logger.LogWarning("A sync run is already in progress, skipping photo sync");
                return;
            }
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
    /// Returns (bytes, wasNotFound) where wasNotFound distinguishes 404 (user has no photo)
    /// from other errors. This diagnostic information identifies whether photo sync bottlenecks
    /// are caused by missing photos vs. API errors.
    /// Protected virtual for test subclassing (following ContactFolderManager pattern).
    /// </summary>
    protected virtual async Task<(byte[]? bytes, bool wasNotFound)> FetchUserPhotoAsync(string entraId, CancellationToken ct)
    {
        // Try 360x360 — high quality while staying well within Exchange Online EAS
        // photo size limits (~100KB). 360x360 JPEG thumbnails are typically 20-50KB.
        // Full-size profile photos (200KB+) are too large and get dropped by EAS.
        try
        {
            var stream = await _graphClientFactory!.Client
                .Users[entraId]
                .Photos["360x360"]
                .Content
                .GetAsync(cancellationToken: ct);

            if (stream is not null)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                _logger.LogDebug("Fetched 360x360 photo for user {EntraId}: {Size} bytes", entraId, ms.Length);
                return (ms.ToArray(), false);
            }
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            // 360x360 not available, try 240x240
        }
        catch (ODataError)
        {
            // Non-404 error on 360x360, try 240x240
        }

        // Fall back to 240x240 thumbnail
        try
        {
            var stream = await _graphClientFactory!.Client
                .Users[entraId]
                .Photos["240x240"]
                .Content
                .GetAsync(cancellationToken: ct);

            if (stream is not null)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                _logger.LogDebug("Fetched 240x240 photo for user {EntraId}: {Size} bytes", entraId, ms.Length);
                return (ms.ToArray(), false);
            }
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return (null, true);
        }
        catch (ODataError ex)
        {
            _logger.LogWarning("Graph photo error for user {EntraId}: {StatusCode} {Message}",
                entraId, ex.ResponseStatusCode, ex.Message);
            return (null, false);
        }

        return (null, true);
    }

    /// <summary>
    /// Fetches a contact's photo from a shared mailbox via Microsoft Graph.
    /// Shared mailbox contacts store photos on the Contact object, not as User profile photos.
    /// Path: GET /users/{mailboxEmail}/contacts/{contactId}/photo/$value
    /// Protected virtual for test subclassing.
    /// </summary>
    protected virtual async Task<(byte[]? bytes, bool wasNotFound)> FetchContactPhotoAsync(
        string mailboxEmail, string contactId, CancellationToken ct)
    {
        try
        {
            var stream = await _graphClientFactory!.Client
                .Users[mailboxEmail]
                .Contacts[contactId]
                .Photo.Content
                .GetAsync(cancellationToken: ct);

            if (stream is not null)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                _logger.LogDebug("Fetched contact photo for {ContactId} in mailbox {Mailbox}: {Size} bytes",
                    contactId, mailboxEmail, ms.Length);
                return (ms.ToArray(), false);
            }
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return (null, true);
        }
        catch (ODataError ex)
        {
            _logger.LogWarning("Graph contact photo error for {ContactId} in {Mailbox}: {StatusCode} {Message}",
                contactId, mailboxEmail, ex.ResponseStatusCode, ex.Message);
            return (null, false);
        }

        return (null, true);
    }

    /// <summary>
    /// Writes photo bytes to a target contact via Microsoft Graph.
    /// Uses the ContactFolders path because contacts live in a named subfolder
    /// (e.g. "Buckhead-test"), not the root contacts collection. The flat
    /// /contacts/{id}/photo path does not reliably write photos for subfolder contacts.
    /// Creates a new MemoryStream for each call to avoid stream exhaustion (Pitfall 2).
    /// Protected virtual for test subclassing.
    /// </summary>
    protected virtual async Task WriteContactPhotoAsync(
        string mailboxEntraId, string folderId, string graphContactId, byte[] photoBytes, CancellationToken ct)
    {
        using var stream = new MemoryStream(photoBytes);
        await _graphClientFactory!.Client
            .Users[mailboxEntraId]
            .ContactFolders[folderId]
            .Contacts[graphContactId]
            .Photo.Content
            .PutAsync(stream, requestConfiguration =>
            {
                requestConfiguration.Headers.Add("Content-Type", "image/jpeg");
            }, cancellationToken: ct);
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
        string folderId,
        List<ContactSyncState> states,
        Dictionary<int, (byte[]? bytes, string? hash)> sourcePhotos,
        SyncBehavior photoBehavior,
        bool isBackfill,
        bool isDryRun,
        CancellationToken ct)
    {
        int updated = 0, failed = 0;

        // Diagnostic counters for this mailbox
        int skipNoSourcePhoto = 0, skipNoPhotoBytes = 0, skipHashMatch = 0, skipRemoval = 0, attempted = 0;

        // Step e: Batched processing for backfill (D-12)
        for (int i = 0; i < states.Count; i += BatchSize)
        {
            var batch = states.Skip(i).Take(BatchSize).ToList();

            foreach (var state in batch)
            {
                if (!sourcePhotos.TryGetValue(state.SourceUserId, out var photoData))
                {
                    skipNoSourcePhoto++;
                    continue;
                }

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
                    skipRemoval++;
                    continue;
                }

                // Skip if no photo to write
                if (photoBytes == null)
                {
                    skipNoPhotoBytes++;
                    continue;
                }

                // Skip if hash matches (no change)
                if (sourceHash == state.PhotoHash)
                {
                    skipHashMatch++;
                    continue;
                }

                attempted++;

                // Write photo to target contact
                try
                {
                    if (!isDryRun)
                    {
                        await WriteContactPhotoAsync(
                            mailboxEntraId, folderId, state.GraphContactId!, photoBytes, ct);
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

        // Diagnostic summary for this mailbox (log first 5 mailboxes at Warning, rest at Debug)
        _logger.LogDebug(
            "Photo mailbox diagnostics: Mailbox={MailboxId}, States={Total}, NoSourcePhoto={NoSource}, NoPhotoBytes={NoBytes}, HashMatch={HashMatch}, Removal={Removal}, Attempted={Attempted}, Updated={Updated}, Failed={Failed}",
            mailboxEntraId, states.Count, skipNoSourcePhoto, skipNoPhotoBytes, skipHashMatch, skipRemoval, attempted, updated, failed);

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
