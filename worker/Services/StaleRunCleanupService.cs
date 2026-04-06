using AFHSync.Shared.Data;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AFHSync.Worker.Services;

/// <summary>
/// Hangfire job that marks sync runs stuck in "Running" for over 2 hours as Failed.
/// Safety net for the rare case where even finalization fails (DB outage, OOM, etc.).
/// </summary>
public sealed class StaleRunCleanupService(
    IDbContextFactory<AFHSyncDbContext> dbContextFactory,
    ILogger<StaleRunCleanupService> logger)
{
    public async Task CleanupAsync()
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        var cutoff = DateTime.UtcNow.AddHours(-2);

        var staleRuns = await db.SyncRuns
            .Where(r => r.Status == SyncStatus.Running && r.StartedAt < cutoff)
            .ToListAsync();

        foreach (var run in staleRuns)
        {
            run.Status = SyncStatus.Failed;
            run.CompletedAt = DateTime.UtcNow;
            run.ErrorSummary = "Automatically marked as failed — stuck in Running status for over 2 hours";
            run.DurationMs = run.StartedAt.HasValue
                ? (int)(DateTime.UtcNow - run.StartedAt.Value).TotalMilliseconds
                : null;

            logger.LogWarning("Stale run cleanup: marked RunId={RunId} as Failed (started {StartedAt})",
                run.Id, run.StartedAt);
        }

        if (staleRuns.Count > 0)
            await db.SaveChangesAsync();
    }
}
