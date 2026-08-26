namespace AFHSync.Shared.Entities;

public class TargetMailbox
{
    public int Id { get; set; }
    public string EntraId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastVerifiedAt { get; set; }

    /// <summary>
    /// Phase 2 (§2.1): set the first time Graph reports the mailbox is not REST-enabled
    /// (soft-deleted / on-prem / unlicensed). Null = available. IsActive is unrelated: it
    /// still means "exists and enabled in Entra".
    /// </summary>
    public DateTime? MailboxUnavailableAt { get; set; }

    /// <summary>Last time the worker probed this mailbox and found it unavailable. Re-probed weekly.</summary>
    public DateTime? MailboxLastProbedAt { get; set; }

    /// <summary>The Graph error message from the last unavailable probe.</summary>
    public string? MailboxUnavailableReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
