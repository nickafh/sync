namespace AFHSync.Api.DTOs;

/// <summary>
/// Per-tunnel breakdown in run detail. Phase 3 (§3.1): contact counts come from
/// sync_run_tunnels when the run has records; <see cref="Status"/> and <see cref="TargetsCount"/>
/// are null on the items-only fallback (photo-sync runs and pre-Phase-3 history).
/// </summary>
public record TunnelRunSummaryDto(
    int? TunnelId,
    string TunnelName,
    int ContactsCreated,
    int ContactsUpdated,
    int ContactsRemoved,
    int ContactsSkipped,
    int ContactsFailed,
    int PhotosUpdated,
    int PhotosFailed,
    string[] Errors,
    string? Status = null,
    int? TargetsCount = null
);
