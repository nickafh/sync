using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AFHSync.Worker.Services;

/// <summary>
/// Implements stale contact detection via set-difference between current source members
/// and existing ContactSyncState records, applying the tunnel's configured stale policy
/// (AutoRemove, FlagHold, or Leave) to each stale candidate.
/// </summary>
public sealed class StaleContactHandler(
    IDbContextFactory<AFHSyncDbContext> dbContextFactory,
    IContactWriter contactWriter,
    ILogger<StaleContactHandler> logger) : IStaleContactHandler
{
    public async Task<StaleResult> HandleStaleAsync(
        Tunnel tunnel,
        int phoneListId,
        int targetMailboxId,
        string mailboxEntraId,
        HashSet<int> currentSourceUserIds,
        CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        // Load all non-stale ContactSyncState records for this tunnel+phoneList+mailbox.
        // We also need already-stale records for FlagHold hold-period check.
        var existingStates = await db.ContactSyncStates
            .Where(s => s.TunnelId == tunnel.Id
                        && s.PhoneListId == phoneListId
                        && s.TargetMailboxId == targetMailboxId)
            .ToListAsync(ct);

        // Set-difference: find states whose SourceUserId is NOT in the current source set.
        var staleStates = existingStates
            .Where(s => !currentSourceUserIds.Contains(s.SourceUserId))
            .ToList();

        int removed = 0;
        int staleDetected = 0;

        // Collect contacts to delete via batch for AutoRemove and expired FlagHold.
        var pendingDeletes = new List<(string key, string graphContactId, ContactSyncState state)>();

        foreach (var state in staleStates)
        {
            switch (tunnel.StalePolicy)
            {
                case StalePolicy.AutoRemove:
                    if (!string.IsNullOrEmpty(state.GraphContactId))
                    {
                        pendingDeletes.Add((state.Id.ToString(), state.GraphContactId, state));
                    }
                    else
                    {
                        db.ContactSyncStates.Remove(state);
                        removed++;
                    }
                    break;

                case StalePolicy.FlagHold:
                    if (!state.IsStale)
                    {
                        state.IsStale = true;
                        state.StaleDetectedAt = DateTime.UtcNow;
                        staleDetected++;
                        logger.LogInformation(
                            "FlagHold: marked SourceUserId={SourceUserId} stale in mailbox {MailboxId} (hold for {HoldDays} days)",
                            state.SourceUserId, targetMailboxId, tunnel.StaleHoldDays);
                    }
                    else if (state.StaleDetectedAt.HasValue
                             && state.StaleDetectedAt.Value.AddDays(tunnel.StaleHoldDays) < DateTime.UtcNow)
                    {
                        if (!string.IsNullOrEmpty(state.GraphContactId))
                        {
                            pendingDeletes.Add((state.Id.ToString(), state.GraphContactId, state));
                        }
                        else
                        {
                            db.ContactSyncStates.Remove(state);
                            removed++;
                        }
                    }
                    else
                    {
                        staleDetected++;
                    }
                    break;

                case StalePolicy.Leave:
                    state.IsStale = true;
                    state.StaleDetectedAt ??= DateTime.UtcNow;
                    staleDetected++;
                    logger.LogInformation(
                        "Leave: flagged SourceUserId={SourceUserId} as stale in mailbox {MailboxId} (no deletion)",
                        state.SourceUserId, targetMailboxId);
                    break;
            }
        }

        // Execute batch deletes.
        if (pendingDeletes.Count > 0)
        {
            var batchOps = pendingDeletes
                .Select(d => (d.key, d.graphContactId))
                .ToList();

            var batchResults = await contactWriter.DeleteContactsBatchAsync(
                mailboxEntraId, batchOps, ct);

            foreach (var (key, graphContactId, state) in pendingDeletes)
            {
                if (batchResults.TryGetValue(key, out var result) && result.Success)
                {
                    logger.LogInformation(
                        "{Policy}: deleted contact {GraphContactId} for SourceUserId={SourceUserId} in mailbox {MailboxId}",
                        tunnel.StalePolicy, graphContactId, state.SourceUserId, targetMailboxId);
                    db.ContactSyncStates.Remove(state);
                    removed++;
                }
                else
                {
                    logger.LogWarning(
                        "Batch delete failed for contact {GraphContactId} (SourceUserId={SourceUserId}): {Error}",
                        graphContactId, state.SourceUserId, result?.Error ?? "Unknown");
                }
            }
        }

        await db.SaveChangesAsync(ct);

        return new StaleResult(removed, staleDetected);
    }
}
