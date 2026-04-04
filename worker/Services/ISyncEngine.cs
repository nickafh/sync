using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;

namespace AFHSync.Worker.Services;

/// <summary>
/// Top-level orchestrator for the sync pipeline.
/// Resolves source members, builds payloads, delta-compares via hash,
/// writes to Graph, handles stale contacts, and produces a full audit trail.
/// </summary>
public interface ISyncEngine
{
    /// <summary>
    /// Executes a complete sync run for the specified tunnel (or all active tunnels).
    /// </summary>
    /// <param name="tunnelId">If specified, syncs only this tunnel. If null, syncs all active tunnels.</param>
    /// <param name="runType">The type of run (Manual, Scheduled, DryRun).</param>
    /// <param name="isDryRun">When true, computes all actions but does not write to Graph.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The completed SyncRun record with aggregate counts and final status.</returns>
    Task<SyncRun> RunAsync(
        int? tunnelId,
        RunType runType,
        bool isDryRun,
        CancellationToken ct);
}
