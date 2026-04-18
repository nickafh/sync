using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Shared.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AFHSync.Worker.Services;

/// <summary>
/// Hangfire job that marks sync runs stuck in "Running" too long as Failed.
/// Thresholds are run-type aware — photo backfills legitimately run for hours, while
/// contact syncs should finish within ~30 min. When a run is flipped, also raises the
/// <c>cancel_sync</c> flag so any still-running worker bails at its next tunnel/mailbox
/// boundary check, and additionally asks Hangfire to Delete the tracked background
/// job IDs so a stuck worker is actively cancelled instead of only signalled.
/// The cancel_sync flag is auto-cleared at the start of every subsequent sync run,
/// so it does not affect future syncs. Safety net for the rare case where even
/// finalization fails (DB outage, OOM, etc.).
/// </summary>
public sealed class StaleRunCleanupService(
    IDbContextFactory<AFHSyncDbContext> dbContextFactory,
    IBackgroundJobClient backgroundJobs,
    ILogger<StaleRunCleanupService> logger) : IStaleRunCleanupService
{
    private static readonly TimeSpan ContactRunStaleAfter = TimeSpan.FromHours(2);
    private static readonly TimeSpan PhotoRunStaleAfter = TimeSpan.FromHours(6);

    public async Task CleanupAsync()
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();

        var now = DateTime.UtcNow;
        var contactCutoff = now - ContactRunStaleAfter;
        var photoCutoff = now - PhotoRunStaleAfter;

        var staleRuns = await db.SyncRuns
            .Where(r => r.Status == SyncStatus.Running
                && ((r.RunType == RunType.PhotoSync && r.StartedAt < photoCutoff)
                    || (r.RunType != RunType.PhotoSync && r.StartedAt < contactCutoff)))
            .ToListAsync();

        if (staleRuns.Count == 0)
            return;

        foreach (var run in staleRuns)
        {
            var hours = run.RunType == RunType.PhotoSync ? PhotoRunStaleAfter.TotalHours : ContactRunStaleAfter.TotalHours;
            run.Status = SyncStatus.Failed;
            run.CompletedAt = now;
            run.ErrorSummary = $"Automatically marked as failed — stuck in Running status for over {hours:0} hours";
            run.DurationMs = run.StartedAt.HasValue
                ? (int)(now - run.StartedAt.Value).TotalMilliseconds
                : null;

            logger.LogWarning("Stale run cleanup: marked RunId={RunId} (RunType={RunType}) as Failed (started {StartedAt})",
                run.Id, run.RunType, run.StartedAt);
        }

        // Signal the still-running worker to bail at its next boundary check. Without this
        // the cleanup only flips the DB row; the worker keeps grinding the Graph API and
        // may race against a subsequent sync that starts after the row reads Failed.
        // The flag is auto-cleared at the start of every new sync run (SyncEngine.cs,
        // PhotoSyncService.RunAllAsync), so this does not affect future syncs.
        var cancelFlag = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "cancel_sync");
        if (cancelFlag != null)
            cancelFlag.Value = "true";
        else
            db.AppSettings.Add(new AppSetting { Key = "cancel_sync", Value = "true" });

        await db.SaveChangesAsync();

        // Belt-and-suspenders: actively delete tracked Hangfire jobs so a worker blocked
        // in a Graph call can be cancelled via Hangfire's job-cancellation token, rather
        // than waiting to notice cancel_sync on the next tunnel/mailbox boundary. Delete
        // is idempotent — if the job already finished or was never recorded, it's a no-op.
        foreach (var run in staleRuns)
        {
            if (string.IsNullOrWhiteSpace(run.HangfireJobIds)) continue;
            foreach (var id in run.HangfireJobIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try { backgroundJobs.Delete(id); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete Hangfire job {JobId} for stale run {RunId}", id, run.Id);
                }
            }
        }
    }
}
