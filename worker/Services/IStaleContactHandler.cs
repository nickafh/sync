using AFHSync.Shared.Entities;

namespace AFHSync.Worker.Services;

public interface IStaleContactHandler
{
    /// <summary>
    /// §5.1: considers every contact_sync_state row of the (tunnel, mailbox) pair, whatever phone list
    /// it was created under, and applies the tunnel's stale policy to rows whose source user is not in
    /// <paramref name="currentSourceUserIds"/>.
    /// </summary>
    Task<StaleResult> HandleStaleAsync(
        Tunnel tunnel,
        int targetMailboxId,
        string mailboxEntraId,
        HashSet<int> currentSourceUserIds,
        CancellationToken ct);
}

public record StaleResult(int Removed, int StaleDetected);
