using AFHSync.Shared.Data;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AFHSync.Worker.Services;

/// <summary>
/// Phase 2 (§2.7): runs once at worker startup, BEFORE the Hangfire server starts. Any row still
/// Running belonged to a process that died (crash, OOM, ungraceful stop) — mark it Failed and
/// clear the cancel_sync flag it may have left behind. Nothing is auto-restarted.
/// </summary>
public sealed class RunReconciler(
    IDbContextFactory<AFHSyncDbContext> dbContextFactory,
    ILogger<RunReconciler> logger)
{
    public const string InterruptedSummary = "interrupted by worker restart";

    /// <returns>The number of Running rows marked Failed.</returns>
    public async Task<int> ReconcileAsync(CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;

        var running = await db.SyncRuns
            .Where(r => r.Status == SyncStatus.Running)
            .ToListAsync(ct);

        foreach (var run in running)
        {
            run.Status = SyncStatus.Failed;
            run.CompletedAt = now;
            run.DurationMs = run.StartedAt.HasValue ? (int)(now - run.StartedAt.Value).TotalMilliseconds : null;
            run.ErrorSummary = InterruptedSummary;
            logger.LogWarning("Startup reconcile: RunId={RunId} ({RunType}, started {StartedAt}) was left Running — marked Failed",
                run.Id, run.RunType, run.StartedAt);
        }

        var cancelFlag = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "cancel_sync", ct);
        if (cancelFlag is not null && cancelFlag.Value != "false")
        {
            cancelFlag.Value = "false";
            cancelFlag.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return running.Count;
    }
}
