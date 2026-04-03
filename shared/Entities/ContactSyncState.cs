namespace AFHSync.Shared.Entities;

public class ContactSyncState
{
    public int Id { get; set; }
    public int SourceUserId { get; set; }
    public int PhoneListId { get; set; }
    public int TargetMailboxId { get; set; }
    public int? TunnelId { get; set; }
    public string? GraphContactId { get; set; }
    public string? DataHash { get; set; }
    public string? PhotoHash { get; set; }
    public string? PreviousDataHash { get; set; }
    public string? PreviousPhotoHash { get; set; }
    public bool IsStale { get; set; }
    public DateTime? StaleDetectedAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public string? LastResult { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public SourceUser SourceUser { get; set; } = null!;
    public PhoneList PhoneList { get; set; } = null!;
    public TargetMailbox TargetMailbox { get; set; } = null!;
    public Tunnel? Tunnel { get; set; }
}
