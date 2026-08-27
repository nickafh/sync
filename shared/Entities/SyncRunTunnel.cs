using AFHSync.Shared.Enums;

namespace AFHSync.Shared.Entities;

/// <summary>
/// Phase 3 (§3.1): one row per tunnel per sync run, written by the worker when the tunnel
/// finishes (success, warning, failure, or cancellation). Run detail and the tunnels list read
/// these instead of re-deriving per-tunnel outcomes from sync_run_items.
/// </summary>
public class SyncRunTunnel
{
    public int Id { get; set; }
    public int SyncRunId { get; set; }

    /// <summary>Null after the tunnel is deleted (SET NULL); <see cref="TunnelName"/> keeps the name.</summary>
    public int? TunnelId { get; set; }
    public string TunnelName { get; set; } = string.Empty;
    public SyncStatus Status { get; set; }

    /// <summary>Target mailboxes the tunnel resolved to this run (after scope and unavailable filtering).</summary>
    public int TargetsCount { get; set; }
    public int ContactsCreated { get; set; }
    public int ContactsUpdated { get; set; }
    public int ContactsRemoved { get; set; }
    public int ContactsSkipped { get; set; }
    public int ContactsFailed { get; set; }
    public string? ErrorSummary { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }

    // Navigation properties
    public SyncRun SyncRun { get; set; } = null!;
    public Tunnel? Tunnel { get; set; }
}
