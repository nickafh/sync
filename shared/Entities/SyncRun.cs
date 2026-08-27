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
    /// Hangfire background-job ID enqueued for this run (Phase 2: exactly one job per run,
    /// addressed by run id). The stop endpoint / StaleRunCleanupService call
    /// BackgroundJob.Delete on it so a queued-but-not-yet-started job can't resurrect a
    /// cancelled run. Kept as a string (historically comma-separated) for compatibility.
    /// </summary>
    public string? HangfireJobIds { get; set; }

    /// <summary>
    /// Phase 2 (§2.7): JSON array of tunnel ids this run was asked to process (e.g. "[3,5]").
    /// Null = all active tunnels. Written by the API when it creates the row; the worker
    /// reads it after claiming the row and never trusts the job arguments for this.
    /// </summary>
    public string? RequestedTunnelIds { get; set; }

    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public ICollection<SyncRunItem> SyncRunItems { get; set; } = [];
    public ICollection<SyncRunTunnel> TunnelRecords { get; set; } = [];
}
