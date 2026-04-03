namespace AFHSync.Shared.Entities;

public class SyncRunItem
{
    public int Id { get; set; }
    public int SyncRunId { get; set; }
    public int? TunnelId { get; set; }
    public int? PhoneListId { get; set; }
    public int? TargetMailboxId { get; set; }
    public int? SourceUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? FieldChanges { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public SyncRun SyncRun { get; set; } = null!;
    public Tunnel? Tunnel { get; set; }
    public PhoneList? PhoneList { get; set; }
    public TargetMailbox? TargetMailbox { get; set; }
    public SourceUser? SourceUser { get; set; }
}
