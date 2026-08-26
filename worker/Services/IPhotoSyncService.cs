using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Hangfire;

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
    /// The <c>prior*</c> parameters let the caller thread cumulative cross-tunnel counts so
    /// mid-tunnel progress writes reflect the correct running totals on the dashboard.
    /// </summary>
    Task<(int updated, int failed)> SyncPhotosForTunnelAsync(
        Tunnel tunnel,
        SyncRun run,
        List<SourceUser> sourceUsers,
        bool isDryRun,
        CancellationToken ct,
        int priorPhotosUpdated = 0,
        int priorPhotosFailed = 0,
        int priorTunnelsProcessed = 0);

    /// <summary>
    /// Entry point for the separate_pass Hangfire job and the post-finalize auto-trigger.
    /// Phase 2 (§2.7): creates and claims its own SyncRun through IRunClaimService (one lane
    /// across run types), so it is a no-op while any run is Running. Never retried by Hangfire.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    Task RunAllAsync(RunType runType, bool isDryRun, CancellationToken ct);
}
