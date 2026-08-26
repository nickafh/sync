using AFHSync.Shared.Entities;

namespace AFHSync.Worker.Services;

/// <summary>
/// Manages contact folders in target mailboxes — lazily creating them when they don't
/// exist and caching folder IDs for the duration of a sync run to avoid redundant
/// Graph API calls across parallel mailbox tasks.
/// </summary>
public interface IContactFolderManager
{
    /// <summary>
    /// Returns the ID of the tunnel's contact folder in the given mailbox, creating it if it
    /// doesn't exist. Results are cached per (mailbox, tunnel) for the duration of the sync run
    /// (reset between runs via <see cref="ResetCache"/>).
    /// </summary>
    /// <param name="tunnel">The tunnel; its Name is the folder's display name.</param>
    /// <param name="mailbox">The target mailbox (EntraId is used for Graph, Id for bookkeeping).</param>
    /// <param name="isDryRun">
    /// Phase 2 (§2.2): when true the folder is only looked up, never created (and never renamed).
    /// A missing folder yields <c>folderId = null</c> — every contact is then "would create".
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// (folderId, wasCreated). folderId is null only in a dry run when the folder does not exist.
    /// wasCreated is true only when this call created the folder (never in a dry run).
    /// </returns>
    Task<(string? folderId, bool wasCreated)> GetOrCreateFolderAsync(
        Tunnel tunnel,
        TargetMailbox mailbox,
        bool isDryRun,
        CancellationToken ct);

    /// <summary>
    /// Clears the folder ID cache. Called at the start of each sync run so that
    /// folders deleted between runs are re-discovered rather than returning stale IDs.
    /// </summary>
    void ResetCache();
}
