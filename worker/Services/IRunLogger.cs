using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;

namespace AFHSync.Worker.Services;

/// <summary>
/// Manages SyncRun lifecycle and SyncRunItem logging for the sync pipeline.
/// Creates run records at start, buffers per-item results thread-safely,
/// batch-inserts items for performance, and finalizes run status and aggregate counts.
/// </summary>
public interface IRunLogger
{
    /// <summary>
    /// Creates a new SyncRun record with status=Running and records the start time.
    /// </summary>
    /// <param name="runType">The type of run (Manual, Scheduled, DryRun).</param>
    /// <param name="isDryRun">Whether this is a dry run (no Graph writes).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created SyncRun with its generated database Id.</returns>
    Task<SyncRun> CreateRunAsync(RunType runType, bool isDryRun, CancellationToken ct);

    /// <summary>
    /// Adds a SyncRunItem to the internal thread-safe buffer.
    /// Items are NOT immediately persisted — call <see cref="FlushItemsAsync"/> to commit.
    /// </summary>
    /// <param name="item">The run item to buffer.</param>
    void AddItem(SyncRunItem item);

    /// <summary>
    /// Batch-inserts all buffered SyncRunItems to the database using raw SQL for performance.
    /// Clears the buffer after a successful flush.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task FlushItemsAsync(CancellationToken ct);

    /// <summary>
    /// Finalizes the SyncRun by updating its status, timestamps, duration, and aggregate counts.
    /// </summary>
    Task FinalizeRunAsync(
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
        CancellationToken ct);
}
