using AFHSync.Shared.Data;
using AFHSync.Shared.Enums;
using AFHSync.Shared.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;

namespace AFHSync.Worker.Services;

/// <summary>
/// Hangfire-driven runner that executes a tenant-wide cleanup of contact folders.
/// Per quick-260417-48z: the API enqueues a job (returning 202 immediately), the
/// worker processes folder deletions in parallel using a semaphore-bounded loop,
/// and persists progress to the cleanup_jobs row every <see cref="ProgressFlushEvery"/>
/// items plus once on completion (or failure).
///
/// CRITICAL: NEVER share a DbContext across the parallel deletion loop — each
/// progress flush opens its own scoped context via <see cref="IDbContextFactory{TContext}"/>
/// (see Pitfall 1 in Phase 02 RESEARCH.md, mirrored by StaleRunCleanupService).
///
/// Hangfire automatic retry is disabled: a tenant wipe is destructive and not safe
/// to re-attempt automatically. Operators re-trigger manually if a partial run failed.
/// </summary>
public sealed class CleanupJobRunner(
    IDbContextFactory<AFHSyncDbContext> dbContextFactory,
    GraphServiceClient graphClient,
    ILogger<CleanupJobRunner> logger) : ICleanupJobRunner
{
    private const int MailboxParallelism = 8;
    internal const int ProgressFlushEvery = 25;

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid jobId, CleanupJobItem[] items, CancellationToken ct)
    {
        // Phase 1: flip Queued → Running and stamp StartedAt.
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            var job = await db.CleanupJobs.FindAsync([jobId], ct);
            if (job is null)
            {
                logger.LogWarning("CleanupJob {JobId} not found at start — abandoning", jobId);
                return;
            }
            job.Status = CleanupJobStatus.Running;
            job.StartedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        int deleted = 0, failed = 0, processed = 0;
        string? lastError = null;
        var counterLock = new object();
        using var semaphore = new SemaphoreSlim(MailboxParallelism);

        try
        {
            var tasks = items.Select(async item =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    // Pre-flight: verify this is a SUBfolder, not the user's root Contacts folder.
                    // Root folder has ParentFolderId == null; we must NEVER delete it.
                    // Behaviour preserved verbatim from CleanupController.DeleteFolders pre-async.
                    var folder = await graphClient.Users[item.EntraId].ContactFolders[item.FolderId]
                        .GetAsync(c => c.QueryParameters.Select = ["id", "parentFolderId", "displayName"], ct);

                    if (folder?.ParentFolderId is null)
                    {
                        lock (counterLock)
                        {
                            failed++;
                            lastError = $"BLOCKED root folder for {item.Email}";
                        }
                        logger.LogWarning("BLOCKED: refusing to delete root Contacts folder for {Email}", item.Email);
                    }
                    else
                    {
                        // PermanentDelete bypasses Exchange retention (regular DELETE soft-deletes
                        // to recoverable items and fails when retention policies are active).
                        // See: https://learn.microsoft.com/en-us/graph/api/contactfolder-permanentdelete
                        await graphClient.Users[item.EntraId].ContactFolders[item.FolderId]
                            .PermanentDelete.PostAsync(cancellationToken: ct);

                        lock (counterLock) { deleted++; }
                        logger.LogInformation("Permanently deleted contact folder {FolderName} from {Email}",
                            item.FolderName, item.Email);
                    }
                }
                catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
                {
                    lock (counterLock)
                    {
                        failed++;
                        lastError = $"[{odataEx.Error?.Code ?? "unknown"}] {odataEx.Error?.Message ?? odataEx.Message}";
                    }
                    logger.LogWarning("Failed to delete folder {FolderName} from {Email}: {Err}",
                        item.FolderName, item.Email, lastError);
                }
                catch (Exception ex)
                {
                    lock (counterLock)
                    {
                        failed++;
                        lastError = ex.Message;
                    }
                    logger.LogWarning(ex, "Failed to delete folder {FolderName} from {Email}",
                        item.FolderName, item.Email);
                }
                finally
                {
                    int p;
                    lock (counterLock) { p = ++processed; }
                    if (p % ProgressFlushEvery == 0)
                    {
                        await FlushProgressAsync(jobId, deleted, failed, lastError,
                            status: null, completed: false, ct);
                    }
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            // Final flush — mark completed.
            await FlushProgressAsync(jobId, deleted, failed, lastError,
                status: CleanupJobStatus.Completed, completed: true, ct);

            logger.LogInformation("CleanupJob {JobId} completed: {Deleted} deleted, {Failed} failed of {Total}",
                jobId, deleted, failed, items.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CleanupJob {JobId} failed mid-run", jobId);
            // Use CancellationToken.None so we still record the failure even if the
            // outer token was cancelled.
            await FlushProgressAsync(jobId, deleted, failed, ex.Message,
                status: CleanupJobStatus.Failed, completed: true, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Persists progress to the CleanupJob row using a fresh DbContext.
    /// Internal so unit tests can call it directly to verify the write semantics
    /// without spinning up a Graph client stub.
    /// </summary>
    internal async Task FlushProgressAsync(
        Guid jobId,
        int deleted,
        int failed,
        string? lastError,
        CleanupJobStatus? status,
        bool completed,
        CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var job = await db.CleanupJobs.FindAsync([jobId], ct);
        if (job is null) return;

        job.Deleted = deleted;
        job.Failed = failed;
        if (lastError is not null) job.LastError = lastError;
        if (status.HasValue) job.Status = status.Value;
        if (completed) job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }
}
