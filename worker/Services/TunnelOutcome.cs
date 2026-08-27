namespace AFHSync.Worker.Services;

/// <summary>
/// Phase 3 (§3.1): what one tunnel did in one run. <see cref="TargetsCount"/> is the number of
/// target mailboxes the tunnel resolved to (after scope filtering and unavailable exclusion);
/// it is 0 when the tunnel returned before resolving targets (no source members).
/// </summary>
internal sealed record TunnelOutcome(int Created, int Updated, int Skipped, int Failed, int Removed, int TargetsCount)
{
    public static readonly TunnelOutcome Empty = new(0, 0, 0, 0, 0, 0);
}
