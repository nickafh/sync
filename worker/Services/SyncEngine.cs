using System.Text.Json;
using AFHSync.Api.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AFHSync.Worker.Services;

/// <summary>
/// Orchestrates the complete sync pipeline per the AFH Sync developer specification.
///
/// Pipeline per tunnel (D-13: sequential per-tunnel):
///   1. Resolve source members via ISourceResolver
///   2. Build payloads via IContactPayloadBuilder
///   3. Delta-compare via SHA-256 hash (D-07)
///   4. Write creates/updates to Graph via IContactWriter
///   5. Handle stale contacts per tunnel policy via IStaleContactHandler
///   6. Log all actions via IRunLogger
///
/// Mailbox processing is bounded by SemaphoreSlim (D-14, D-15).
/// Dry-run mode computes all actions without Graph writes.
/// </summary>
public sealed class SyncEngine(
    IDbContextFactory<AFHSyncDbContext> dbContextFactory,
    ISourceResolver sourceResolver,
    IContactPayloadBuilder contactPayloadBuilder,
    IContactWriter contactWriter,
    IContactFolderManager contactFolderManager,
    IStaleContactHandler staleContactHandler,
    IRunLogger runLogger,
    ThrottleCounter throttleCounter,
    IConfiguration configuration,
    ILogger<SyncEngine> logger) : ISyncEngine
{
    private const int DefaultParallelism = 4;

    public async Task<SyncRun> RunAsync(
        int? tunnelId,
        RunType runType,
        bool isDryRun,
        CancellationToken ct)
    {
        // Step 1: Create the run record.
        var run = await runLogger.CreateRunAsync(runType, isDryRun, ct);
        logger.LogInformation(
            "SyncEngine starting RunId={RunId}, TunnelId={TunnelId}, IsDryRun={IsDryRun}",
            run.Id, tunnelId?.ToString() ?? "all", isDryRun);

        // Step 2: Reset contact folder manager cache (fresh run).
        contactFolderManager.ResetCache();

        // Reset throttle counter for this run (singleton counter, safe because concurrent
        // runs are blocked per SCHD-05 — only one sync run executes at a time).
        throttleCounter.Reset();

        // Step 3: Load tunnels.
        var tunnels = await LoadTunnelsAsync(tunnelId, ct);
        logger.LogInformation("Loaded {Count} tunnel(s) to process", tunnels.Count);

        // Step 4: Run-level aggregate counters.
        int totalCreated = 0, totalUpdated = 0, totalSkipped = 0;
        int totalFailed = 0, totalRemoved = 0;
        int tunnelsProcessed = 0, tunnelsWarned = 0, tunnelsFailed = 0;

        // Step 5: Process each tunnel sequentially (D-13).
        foreach (var tunnel in tunnels)
        {
            try
            {
                var (created, updated, skipped, failed, removed) =
                    await ProcessTunnelAsync(tunnel, run, isDryRun, ct);

                totalCreated += created;
                totalUpdated += updated;
                totalSkipped += skipped;
                totalFailed += failed;
                totalRemoved += removed;

                if (failed > 0)
                    tunnelsWarned++;
                else
                    tunnelsProcessed++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tunnel {TunnelId} ({TunnelName}) failed with unhandled exception",
                    tunnel.Id, tunnel.Name);
                tunnelsFailed++;
            }
        }

        // Step 6: Flush all buffered SyncRunItems.
        await runLogger.FlushItemsAsync(ct);

        // Step 7: Determine final status.
        var finalStatus = DetermineStatus(tunnels.Count, tunnelsProcessed, tunnelsWarned, tunnelsFailed, totalFailed);

        // Step 8: Finalize the run.
        await runLogger.FinalizeRunAsync(
            run,
            status: finalStatus,
            errorSummary: tunnelsFailed > 0 ? $"{tunnelsFailed} tunnel(s) failed completely" : null,
            contactsCreated: totalCreated,
            contactsUpdated: totalUpdated,
            contactsSkipped: totalSkipped,
            contactsFailed: totalFailed,
            contactsRemoved: totalRemoved,
            tunnelsProcessed: tunnelsProcessed + tunnelsWarned, // both processed (warned = partial success)
            tunnelsWarned: tunnelsWarned,
            tunnelsFailed: tunnelsFailed,
            throttleEvents: throttleCounter.Count,
            ct: ct);

        logger.LogInformation(
            "SyncRun {RunId} complete: Status={Status}, Created={Created}, Updated={Updated}, Skipped={Skipped}, Failed={Failed}, Removed={Removed}",
            run.Id, finalStatus, totalCreated, totalUpdated, totalSkipped, totalFailed, totalRemoved);

        // Return run with updated status for callers.
        run.Status = finalStatus;
        return run;
    }

    private async Task<List<Tunnel>> LoadTunnelsAsync(int? tunnelId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        if (tunnelId.HasValue)
        {
            var tunnel = await db.Tunnels
                .Include(t => t.FieldProfile)
                    .ThenInclude(fp => fp!.FieldProfileFields)
                .Include(t => t.TunnelPhoneLists)
                    .ThenInclude(tpl => tpl.PhoneList)
                .FirstOrDefaultAsync(t => t.Id == tunnelId.Value, ct);

            if (tunnel == null)
            {
                logger.LogWarning("Tunnel {TunnelId} not found", tunnelId.Value);
                return [];
            }
            return [tunnel];
        }

        return await db.Tunnels
            .Where(t => t.Status == TunnelStatus.Active)
            .Include(t => t.FieldProfile)
                .ThenInclude(fp => fp!.FieldProfileFields)
            .Include(t => t.TunnelPhoneLists)
                .ThenInclude(tpl => tpl.PhoneList)
            .ToListAsync(ct);
    }

    private async Task<(int created, int updated, int skipped, int failed, int removed)> ProcessTunnelAsync(
        Tunnel tunnel,
        SyncRun run,
        bool isDryRun,
        CancellationToken ct)
    {
        logger.LogInformation("Processing tunnel {TunnelId} ({TunnelName})", tunnel.Id, tunnel.Name);

        // Step 5a: Resolve source members.
        var sourceUsers = await sourceResolver.ResolveAsync(tunnel, ct);
        if (sourceUsers.Count == 0)
        {
            logger.LogWarning("Tunnel {TunnelName}: 0 source members resolved, skipping", tunnel.Name);
            return (0, 0, 0, 0, 0);
        }

        // Step 5c: Load field profile settings.
        var fieldSettings = tunnel.FieldProfile?.FieldProfileFields?.ToList()
            ?? await LoadDefaultFieldProfileAsync(ct);

        // Step 5d: Load target mailboxes (AllUsers scope — SpecificUsers deferred).
        var targetMailboxes = await LoadTargetMailboxesAsync(tunnel, ct);

        // Step 5f: Read parallelism setting.
        var parallelism = await ReadParallelismSettingAsync(ct);

        // Step 5g: Create semaphore for bounded mailbox parallelism.
        using var semaphore = new SemaphoreSlim(parallelism);

        int created = 0, updated = 0, skipped = 0, failed = 0, removed = 0;
        var counterLock = new object();

        // Step 5h: Process mailboxes in parallel (bounded by semaphore, D-15).
        var phoneLists = tunnel.TunnelPhoneLists
            .Select(tpl => tpl.PhoneList)
            .Where(pl => pl != null)
            .ToList();

        var mailboxTasks = targetMailboxes.Select(async mailbox =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                foreach (var phoneList in phoneLists)
                {
                    var (c, u, s, f, r) = await ProcessMailboxPhoneListAsync(
                        tunnel, phoneList, mailbox, run, sourceUsers, fieldSettings, isDryRun, ct);

                    lock (counterLock)
                    {
                        created += c;
                        updated += u;
                        skipped += s;
                        failed += f;
                        removed += r;
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(mailboxTasks);

        logger.LogInformation(
            "Tunnel {TunnelName} complete: Created={Created}, Updated={Updated}, Skipped={Skipped}, Failed={Failed}, Removed={Removed}",
            tunnel.Name, created, updated, skipped, failed, removed);

        return (created, updated, skipped, failed, removed);
    }

    private async Task<(int created, int updated, int skipped, int failed, int removed)> ProcessMailboxPhoneListAsync(
        Tunnel tunnel,
        PhoneList phoneList,
        TargetMailbox mailbox,
        SyncRun run,
        List<SourceUser> sourceUsers,
        List<FieldProfileField> fieldSettings,
        bool isDryRun,
        CancellationToken ct)
    {
        int created = 0, updated = 0, skipped = 0, failed = 0, removed = 0;

        // Get or create the contact folder.
        string folderId;
        try
        {
            folderId = await contactFolderManager.GetOrCreateFolderAsync(mailbox.EntraId, phoneList.Name, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get/create folder '{FolderName}' in mailbox {MailboxId}", phoneList.Name, mailbox.Id);
            failed++;
            return (created, updated, skipped, failed, removed);
        }

        // Load existing sync state for this tunnel+phoneList+mailbox (AsNoTracking per Pitfall 5).
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var existingStates = await db.ContactSyncStates
            .AsNoTracking()
            .Where(s => s.TunnelId == tunnel.Id
                        && s.PhoneListId == phoneList.Id
                        && s.TargetMailboxId == mailbox.Id)
            .ToDictionaryAsync(s => s.SourceUserId, ct);

        // Track new/modified states to save at the end.
        var statesToAdd = new List<ContactSyncState>();
        var statesToUpdate = new List<(int StateId, string DataHash, string? PreviousHash, string LastResult)>();

        // Process each source user.
        foreach (var sourceUser in sourceUsers)
        {
            try
            {
                existingStates.TryGetValue(sourceUser.Id, out var existingState);
                var result = contactPayloadBuilder.BuildPayload(sourceUser, fieldSettings, existingState);

                if (existingState == null)
                {
                    // New contact (SYNC-05).
                    string? graphContactId = null;
                    if (!isDryRun)
                    {
                        graphContactId = await contactWriter.CreateContactAsync(
                            mailbox.EntraId, folderId, result.Payload, ct);
                    }

                    statesToAdd.Add(new ContactSyncState
                    {
                        SourceUserId = sourceUser.Id,
                        PhoneListId = phoneList.Id,
                        TargetMailboxId = mailbox.Id,
                        TunnelId = tunnel.Id,
                        GraphContactId = graphContactId,
                        DataHash = result.DataHash,
                        LastSyncedAt = DateTime.UtcNow,
                        LastResult = "created",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });

                    runLogger.AddItem(new SyncRunItem
                    {
                        SyncRunId = run.Id,
                        TunnelId = tunnel.Id,
                        PhoneListId = phoneList.Id,
                        TargetMailboxId = mailbox.Id,
                        SourceUserId = sourceUser.Id,
                        Action = "created",
                        CreatedAt = DateTime.UtcNow
                    });
                    created++;
                }
                else if (existingState.DataHash != result.DataHash)
                {
                    // Changed contact (SYNC-06) — hash mismatch.
                    if (!isDryRun)
                    {
                        await contactWriter.UpdateContactAsync(
                            mailbox.EntraId, existingState.GraphContactId!, result.Payload, ct);
                    }

                    // Compute field-level changes for audit trail (LOGS-03).
                    var fieldChangesJson = BuildFieldChangesJson(result.Payload, existingState.DataHash);

                    statesToUpdate.Add((existingState.Id, result.DataHash, existingState.DataHash, "updated"));

                    runLogger.AddItem(new SyncRunItem
                    {
                        SyncRunId = run.Id,
                        TunnelId = tunnel.Id,
                        PhoneListId = phoneList.Id,
                        TargetMailboxId = mailbox.Id,
                        SourceUserId = sourceUser.Id,
                        Action = "updated",
                        FieldChanges = fieldChangesJson,
                        CreatedAt = DateTime.UtcNow
                    });
                    updated++;
                }
                else
                {
                    // Hash match — unchanged contact (SYNC-07). Zero Graph API calls.
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process contact for SourceUserId={SourceUserId} in mailbox {MailboxId}",
                    sourceUser.Id, mailbox.Id);

                runLogger.AddItem(new SyncRunItem
                {
                    SyncRunId = run.Id,
                    TunnelId = tunnel.Id,
                    PhoneListId = phoneList.Id,
                    TargetMailboxId = mailbox.Id,
                    SourceUserId = sourceUser.Id,
                    Action = "failed",
                    ErrorMessage = ex.Message,
                    CreatedAt = DateTime.UtcNow
                });
                failed++;
                // Continue to next contact (D-17).
            }
        }

        // Save new/updated ContactSyncState records using a fresh tracked context.
        if (statesToAdd.Count > 0 || statesToUpdate.Count > 0)
        {
            await using var writeDb = await dbContextFactory.CreateDbContextAsync(ct);

            if (statesToAdd.Count > 0)
            {
                writeDb.ContactSyncStates.AddRange(statesToAdd);
            }

            if (statesToUpdate.Count > 0)
            {
                // Bulk load states to update by ID.
                var updateIds = statesToUpdate.Select(u => u.StateId).ToList();
                var trackedStates = await writeDb.ContactSyncStates
                    .Where(s => updateIds.Contains(s.Id))
                    .ToListAsync(ct);

                var updateDict = statesToUpdate.ToDictionary(u => u.StateId);
                foreach (var s in trackedStates)
                {
                    if (updateDict.TryGetValue(s.Id, out var update))
                    {
                        s.PreviousDataHash = update.PreviousHash;
                        s.DataHash = update.DataHash;
                        s.LastResult = update.LastResult;
                        s.LastSyncedAt = DateTime.UtcNow;
                        s.UpdatedAt = DateTime.UtcNow;
                    }
                }
            }

            await writeDb.SaveChangesAsync(ct);
        }

        // Handle stale contacts after processing all source users.
        if (!isDryRun)
        {
            var currentSourceIds = new HashSet<int>(sourceUsers.Select(u => u.Id));
            var staleResult = await staleContactHandler.HandleStaleAsync(
                tunnel, phoneList.Id, mailbox.Id, mailbox.EntraId, currentSourceIds, ct);

            for (int i = 0; i < staleResult.Removed; i++)
            {
                runLogger.AddItem(new SyncRunItem
                {
                    SyncRunId = run.Id,
                    TunnelId = tunnel.Id,
                    PhoneListId = phoneList.Id,
                    TargetMailboxId = mailbox.Id,
                    Action = "removed",
                    CreatedAt = DateTime.UtcNow
                });
            }

            for (int i = 0; i < staleResult.StaleDetected; i++)
            {
                runLogger.AddItem(new SyncRunItem
                {
                    SyncRunId = run.Id,
                    TunnelId = tunnel.Id,
                    PhoneListId = phoneList.Id,
                    TargetMailboxId = mailbox.Id,
                    Action = "stale_detected",
                    CreatedAt = DateTime.UtcNow
                });
            }

            removed += staleResult.Removed;
        }

        return (created, updated, skipped, failed, removed);
    }

    private async Task<List<FieldProfileField>> LoadDefaultFieldProfileAsync(CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var defaultProfile = await db.FieldProfiles
            .Include(fp => fp.FieldProfileFields)
            .FirstOrDefaultAsync(fp => fp.IsDefault, ct);

        return defaultProfile?.FieldProfileFields?.ToList() ?? [];
    }

    private async Task<List<TargetMailbox>> LoadTargetMailboxesAsync(Tunnel tunnel, CancellationToken ct)
    {
        if (tunnel.TargetScope == TargetScope.SpecificUsers)
        {
            logger.LogWarning(
                "Tunnel {TunnelName} uses SpecificUsers scope — not yet implemented, skipping",
                tunnel.Name);
            return [];
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.TargetMailboxes
            .Where(m => m.IsActive)
            .ToListAsync(ct);
    }

    private async Task<int> ReadParallelismSettingAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            var setting = await db.AppSettings
                .FirstOrDefaultAsync(s => s.Key == "parallelism", ct);

            if (setting != null && int.TryParse(setting.Value, out var parsed) && parsed > 0)
                return parsed;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read parallelism from DB, using default {Default}", DefaultParallelism);
        }

        return DefaultParallelism;
    }

    /// <summary>
    /// Builds a simple field-changes JSON string for update audit trail (LOGS-03).
    /// Since we don't store the full previous payload, we record the new payload values
    /// and the old hash for traceability.
    /// </summary>
    private static string BuildFieldChangesJson(SortedDictionary<string, string> newPayload, string? previousHash)
    {
        var changes = newPayload.ToDictionary(
            kvp => kvp.Key,
            kvp => new { @new = kvp.Value });

        return JsonSerializer.Serialize(new
        {
            previousHash,
            fields = changes
        });
    }

    private static SyncStatus DetermineStatus(
        int totalTunnels,
        int tunnelsProcessed,
        int tunnelsWarned,
        int tunnelsFailed,
        int totalFailed)
    {
        if (totalTunnels == 0)
            return SyncStatus.Success;
        if (tunnelsFailed > 0 && tunnelsProcessed == 0 && tunnelsWarned == 0)
            return SyncStatus.Failed;
        if (tunnelsFailed > 0 || tunnelsWarned > 0 || totalFailed > 0)
            return SyncStatus.Warning;
        return SyncStatus.Success;
    }
}
