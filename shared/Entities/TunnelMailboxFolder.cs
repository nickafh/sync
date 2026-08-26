namespace AFHSync.Shared.Entities;

/// <summary>
/// Phase 2 (§2.5): the Graph contact folder the worker last used for a (tunnel, mailbox)
/// pair. Lets the folder be found by id after the tunnel is renamed, so a rename becomes a
/// PATCH of displayName on every phone instead of a brand-new folder.
/// </summary>
public class TunnelMailboxFolder
{
    public int Id { get; set; }
    public int TunnelId { get; set; }
    public int TargetMailboxId { get; set; }
    public string GraphFolderId { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public Tunnel Tunnel { get; set; } = null!;
    public TargetMailbox TargetMailbox { get; set; } = null!;
}
