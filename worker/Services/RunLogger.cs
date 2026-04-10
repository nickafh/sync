using System.Collections.Concurrent;
using System.Text;
using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AFHSync.Worker.Services;

/// <summary>
/// Manages SyncRun lifecycle (create, finalize) and SyncRunItem logging.
/// Items are buffered in a thread-safe ConcurrentBag and batch-inserted via raw SQL
/// for performance (D-21), avoiding EF Core change-tracking overhead for bulk inserts.
/// </summary>
public sealed class RunLogger(
    IDbContextFactory<AFHSyncDbContext> dbContextFactory,
    ILogger<RunLogger> logger) : IRunLogger
{
    // Thread-safe buffer for per-item results collected from parallel mailbox tasks (D-14).
    private readonly ConcurrentBag<SyncRunItem> _itemBuffer = [];

    /// <summary>
    /// Creates a new SyncRun record with status=Running and records the start time (D-19).
    /// </summary>
    public async Task<SyncRun> CreateRunAsync(RunType runType, bool isDryRun, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        var run = new SyncRun
        {
            RunType = runType,
            Status = SyncStatus.Running,
            IsDryRun = isDryRun,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "SyncRun {RunId} created: RunType={RunType}, IsDryRun={IsDryRun}",
            run.Id, runType, isDryRun);

        return run;
    }

    /// <summary>
    /// Adds a SyncRunItem to the internal thread-safe buffer (D-14).
    /// Items are NOT immediately persisted — call <see cref="FlushItemsAsync"/> to commit.
    /// </summary>
    public void AddItem(SyncRunItem item) => _itemBuffer.Add(item);

    /// <summary>
    /// Batch-inserts all buffered SyncRunItems to the database (D-21).
    /// Uses raw SQL for performance; falls back to EF Core AddRange for InMemory databases
    /// (used in unit tests). Clears the buffer after a successful flush.
    /// </summary>
    public async Task FlushItemsAsync(CancellationToken ct)
    {
        var items = _itemBuffer.ToList();
        if (items.Count == 0)
        {
            logger.LogDebug("FlushItemsAsync: buffer is empty, nothing to flush");
            return;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        // Use EF Core AddRange for InMemory databases (unit tests).
        // Production uses raw SQL batch insert for performance.
        if (db.Database.IsInMemory())
        {
            // InMemory path for unit tests — EF Core AddRange works correctly.
            db.SyncRunItems.AddRange(items);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            // Production path: raw SQL batch insert (INSERT INTO sync_run_items ...).
            // Filter out items referencing deleted tunnels to avoid FK violations.
            var validTunnelIds = await db.Tunnels.Select(t => t.Id).ToListAsync(ct);
            var validTunnelIdSet = new HashSet<int>(validTunnelIds);
            var validItems = items.Where(i => i.TunnelId == null || validTunnelIdSet.Contains(i.TunnelId.Value)).ToList();
            if (validItems.Count < items.Count)
                logger.LogWarning("FlushItemsAsync: filtered out {Count} items referencing deleted tunnels", items.Count - validItems.Count);
            await BatchInsertSqlAsync(db, validItems, ct);
        }

        // Clear the buffer after successful flush.
        while (_itemBuffer.TryTake(out _)) { }

        logger.LogInformation("FlushItemsAsync: flushed {Count} SyncRunItems", items.Count);
    }

    /// <summary>
    /// Batch-inserts SyncRunItems in groups of 100 using raw SQL for performance.
    /// Groups of 100 * 9 params = 900 parameters per batch, well within PostgreSQL's limit.
    /// </summary>
    private static async Task BatchInsertSqlAsync(AFHSyncDbContext db, List<SyncRunItem> items, CancellationToken ct)
    {
        const int batchSize = 100;

        for (int offset = 0; offset < items.Count; offset += batchSize)
        {
            var batch = items.Skip(offset).Take(batchSize).ToList();
            await InsertBatchAsync(db, batch, ct);
        }
    }

    private static async Task InsertBatchAsync(AFHSyncDbContext db, List<SyncRunItem> batch, CancellationToken ct)
    {
        // Build: INSERT INTO sync_run_items (col1, ...) VALUES (@p0, ...), (@p9, ...), ...
        var sql = new StringBuilder();
        sql.AppendLine("""
            INSERT INTO sync_run_items (sync_run_id, tunnel_id, phone_list_id, target_mailbox_id, source_user_id, action, field_changes, error_message, created_at)
            VALUES
            """);

        var parameters = new List<object?>();
        int paramIdx = 0;

        for (int i = 0; i < batch.Count; i++)
        {
            var item = batch[i];
            if (i > 0) sql.AppendLine(",");

            // field_changes (param index 6 per row) needs ::jsonb cast for PostgreSQL
            sql.Append($"({{{paramIdx++}}}, {{{paramIdx++}}}, {{{paramIdx++}}}, {{{paramIdx++}}}, {{{paramIdx++}}}, {{{paramIdx++}}}, {{{paramIdx}}}::jsonb, {{{paramIdx + 1}}}, {{{paramIdx + 2}}})");
            paramIdx += 3;

            parameters.Add(item.SyncRunId);
            parameters.Add((object?)item.TunnelId);
            parameters.Add((object?)item.PhoneListId);
            parameters.Add((object?)item.TargetMailboxId);
            parameters.Add((object?)item.SourceUserId);
            parameters.Add(item.Action);
            parameters.Add((object?)item.FieldChanges);
            parameters.Add((object?)item.ErrorMessage);
            parameters.Add(item.CreatedAt);
        }

        await db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray(), ct);
    }

    /// <summary>
    /// Finalizes the SyncRun by updating status, timestamps, duration, and aggregate counts (D-19).
    /// </summary>
    public async Task FinalizeRunAsync(
        SyncRun run,
        SyncStatus status,
        string? errorSummary,
        int contactsCreated,
        int contactsUpdated,
        int contactsSkipped,
        int contactsFailed,
        int contactsRemoved,
        int tunnelsProcessed,
        int tunnelsWarned,
        int tunnelsFailed,
        int throttleEvents,
        int photosUpdated,
        int photosFailed,
        CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        var dbRun = await db.SyncRuns.FindAsync([run.Id], ct)
            ?? throw new InvalidOperationException($"SyncRun {run.Id} not found during finalization");

        var completedAt = DateTime.UtcNow;
        dbRun.Status = status;
        dbRun.CompletedAt = completedAt;
        dbRun.DurationMs = run.StartedAt.HasValue
            ? (int)(completedAt - run.StartedAt.Value).TotalMilliseconds
            : null;
        dbRun.ErrorSummary = errorSummary;
        dbRun.ContactsCreated = contactsCreated;
        dbRun.ContactsUpdated = contactsUpdated;
        dbRun.ContactsSkipped = contactsSkipped;
        dbRun.ContactsFailed = contactsFailed;
        dbRun.ContactsRemoved = contactsRemoved;
        dbRun.TunnelsProcessed = tunnelsProcessed;
        dbRun.TunnelsWarned = tunnelsWarned;
        dbRun.TunnelsFailed = tunnelsFailed;
        dbRun.ThrottleEvents = throttleEvents;
        dbRun.PhotosUpdated = photosUpdated;
        dbRun.PhotosFailed = photosFailed;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "SyncRun {RunId} finalized: Status={Status}, Created={Created}, Updated={Updated}, Skipped={Skipped}, Failed={Failed}, Removed={Removed}, Duration={DurationMs}ms",
            run.Id, status, contactsCreated, contactsUpdated, contactsSkipped, contactsFailed, contactsRemoved, dbRun.DurationMs);
    }
}
