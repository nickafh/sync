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
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public ICollection<SyncRunItem> SyncRunItems { get; set; } = [];
}
