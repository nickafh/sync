using AFHSync.Shared.Enums;

namespace AFHSync.Shared.Entities;

public class SyncRun
{
    public int Id { get; set; }
    public RunType RunType { get; set; } = RunType.Manual;
    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public bool IsDryRun { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
    public int TunnelsProcessed { get; set; }
    public int TunnelsWarned { get; set; }
    public int TunnelsFailed { get; set; }
    public int ContactsCreated { get; set; }
    public int ContactsUpdated { get; set; }
    public int ContactsRemoved { get; set; }
    public int ContactsSkipped { get; set; }
    public int ContactsFailed { get; set; }
    public int PhotosUpdated { get; set; }
    public int PhotosFailed { get; set; }
    public int ThrottleEvents { get; set; }
    public string? ErrorSummary { get; set; }

    /// <summary>
    /// Comma-separated list of Hangfire background-job IDs enqueued for this run.
    /// A single manual sync fan-outs to N jobs (one per tunnel) but only the first
    /// to claim the Pending row runs the sync; tracking all IDs lets the stop
    /// endpoint / StaleRunCleanupService call BackgroundJob.Delete on the whole set
    /// so queued-but-not-yet-started jobs don't resurrect a cancelled run.
    /// </summary>
    public string? HangfireJobIds { get; set; }

    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public ICollection<SyncRunItem> SyncRunItems { get; set; } = [];
}
