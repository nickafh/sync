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

    /// <summary>
    /// Phase 3 (§3.7): set by the worker before the first create batch for this (tunnel, mailbox)
    /// and cleared only after every chunk's state rows are persisted. A non-null value at the
    /// start of a run means a crash or shutdown may have left Graph contacts with no state row —
    /// the folder is reconciled before classification.
    /// </summary>
    public DateTime? ReconcilePendingAt { get; set; }

    // Navigation properties
    public Tunnel Tunnel { get; set; } = null!;
    public TargetMailbox TargetMailbox { get; set; } = null!;
}
