using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;

namespace AFHSync.Worker.Services;

/// <summary>
/// Fetches source user photos from Microsoft Graph, computes SHA-256 hashes for delta
/// comparison, and writes changed photos to target contact records. Supports three modes:
/// included (trailing pass within SyncEngine), separate_pass (own Hangfire job), disabled.
/// </summary>
public interface IPhotoSyncService
{
    /// <summary>
    /// Runs photo sync for a single tunnel. Called by SyncEngine (included mode) or RunAllAsync (separate_pass).
    /// Returns (photosUpdated, photosFailed).
    /// </summary>
    Task<(int updated, int failed)> SyncPhotosForTunnelAsync(
        Tunnel tunnel,
        SyncRun run,
        List<SourceUser> sourceUsers,
        bool isDryRun,
        CancellationToken ct);

    /// <summary>
    /// Entry point for separate_pass Hangfire job. Creates its own SyncRun,
    /// loads active tunnels, orchestrates per-tunnel photo sync.
    /// </summary>
    Task RunAllAsync(RunType runType, bool isDryRun, CancellationToken ct);
}
