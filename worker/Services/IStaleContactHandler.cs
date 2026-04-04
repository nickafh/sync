using AFHSync.Shared.Entities;

namespace AFHSync.Worker.Services;

/// <summary>
/// Detects stale contacts via set-difference and applies the tunnel's stale policy
/// (AutoRemove, FlagHold, or Leave) to each stale candidate.
/// </summary>
public interface IStaleContactHandler
{
    /// <summary>
    /// Compares existing ContactSyncState records for the given tunnel/phonelist/mailbox
    /// against the current source user ID set, and applies the tunnel's stale policy to
    /// any records not present in <paramref name="currentSourceUserIds"/>.
    /// </summary>
    /// <param name="tunnel">The tunnel whose stale policy applies.</param>
    /// <param name="phoneListId">The phone list being synced.</param>
    /// <param name="targetMailboxId">The target mailbox being processed.</param>
    /// <param name="mailboxEntraId">The Entra ID of the mailbox (used for Graph delete calls).</param>
    /// <param name="currentSourceUserIds">IDs of SourceUsers currently in the source — contacts whose SourceUserId is NOT in this set are stale candidates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="StaleResult"/> with counts of removed and stale-detected contacts.</returns>
    Task<StaleResult> HandleStaleAsync(
        Tunnel tunnel,
        int phoneListId,
        int targetMailboxId,
        string mailboxEntraId,
        HashSet<int> currentSourceUserIds,
        CancellationToken ct);
}

/// <summary>
/// Result of a stale handling operation for a single tunnel/phone-list/mailbox combination.
/// </summary>
/// <param name="Removed">Number of contacts deleted from Graph and removed from DB.</param>
/// <param name="StaleDetected">Number of contacts marked stale but not yet deleted (FlagHold in hold period, or Leave policy).</param>
public record StaleResult(int Removed, int StaleDetected);
