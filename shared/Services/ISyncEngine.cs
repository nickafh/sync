using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Hangfire;

namespace AFHSync.Shared.Services;

/// <summary>
/// Top-level orchestrator for the sync pipeline.
/// Resolves source members, builds payloads, delta-compares via hash,
/// writes to Graph, handles stale contacts, and produces a full audit trail.
/// Interface in shared project so API can reference it for Hangfire job enqueue
/// without a circular project dependency (Worker references API for DbContext).
/// </summary>
public interface ISyncEngine
{
    /// <summary>
    /// Executes a sync run.
    /// </summary>
    /// <param name="runId">
    /// Phase 2 (§2.7). When set, the worker claims that <c>sync_runs</c> row (Pending → Running)
    /// under the run-start advisory lock and reads RunType, IsDryRun and RequestedTunnelIds
    /// from it; a row that is no longer Pending is returned untouched and no work is done.
    /// When null (cron), a new row is created from <paramref name="runType"/> / <paramref name="isDryRun"/>.
    /// </param>
    /// <param name="runType">Used only when <paramref name="runId"/> is null.</param>
    /// <param name="isDryRun">Used only when <paramref name="runId"/> is null.</param>
    /// <param name="ct">
    /// Hangfire replaces the token passed at enqueue time (callers pass CancellationToken.None)
    /// with its own, which is signalled on worker shutdown and on job deletion.
    /// </param>
    /// <returns>The run record with its final status.</returns>
    [AutomaticRetry(Attempts = 0)]
    Task<SyncRun> RunAsync(
        int? runId,
        RunType runType,
        bool isDryRun,
        CancellationToken ct);
}
