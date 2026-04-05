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

        foreach (var state in staleStates)
        {
            switch (tunnel.StalePolicy)
            {
                case StalePolicy.AutoRemove:
                    // Delete from Graph immediately, then remove the sync state record.
                    if (!string.IsNullOrEmpty(state.GraphContactId))
                    {
                        await contactWriter.DeleteContactAsync(mailboxEntraId, state.GraphContactId, ct);
                        logger.LogInformation(
                            "AutoRemove: deleted contact {GraphContactId} for SourceUserId={SourceUserId} in mailbox {MailboxId}",
                            state.GraphContactId, state.SourceUserId, targetMailboxId);
                    }
                    db.ContactSyncStates.Remove(state);
                    removed++;
                    break;

                case StalePolicy.FlagHold:
                    if (!state.IsStale)
                    {
                        // First time we've detected this contact as stale — flag it.
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
                        // Hold period has expired — delete from Graph and remove record.
                        if (!string.IsNullOrEmpty(state.GraphContactId))
                        {
                            await contactWriter.DeleteContactAsync(mailboxEntraId, state.GraphContactId, ct);
                        }
                        db.ContactSyncStates.Remove(state);
                        removed++;
                        logger.LogInformation(
                            "FlagHold: hold expired, deleted contact {GraphContactId} for SourceUserId={SourceUserId}",
                            state.GraphContactId, state.SourceUserId);
                    }
                    else
                    {
                        // Still in hold period — report as stale-detected but do not delete.
                        staleDetected++;
                    }
                    break;

                case StalePolicy.Leave:
                    // Mark as stale but never delete.
                    state.IsStale = true;
                    state.StaleDetectedAt ??= DateTime.UtcNow;
                    staleDetected++;
                    logger.LogInformation(
                        "Leave: flagged SourceUserId={SourceUserId} as stale in mailbox {MailboxId} (no deletion)",
                        state.SourceUserId, targetMailboxId);
                    break;
            }
        }

        await db.SaveChangesAsync(ct);

        return new StaleResult(removed, staleDetected);
    }
}
