using AFHSync.Shared.Enums;

namespace AFHSync.Shared.Entities;

/// <summary>
/// Tracks an asynchronous tenant-wide CiraSync folder cleanup. The HTTP layer
/// (POST /api/cleanup/delete) inserts the row in Status=Queued, enqueues a
/// Hangfire job, and returns 202. The worker's CleanupJobRunner flips the row
/// to Running, processes folder deletions in parallel, and persists progress
/// every 25 items. Frontend polls GET /api/cleanup/jobs/{id} every 2s.
/// </summary>
public class CleanupJob
{
    public Guid Id { get; set; }
    public CleanupJobStatus Status { get; set; }
    public int Total { get; set; }
    public int Deleted { get; set; }
    public int Failed { get; set; }

    /// <summary>Last error message captured during the run (most recent failure).</summary>
    public string? LastError { get; set; }

    /// <summary>Optional jsonb aggregate of error categories — free-form for future analytics.</summary>
    public string? ErrorSummary { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
