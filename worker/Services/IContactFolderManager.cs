namespace AFHSync.Worker.Services;

/// <summary>
/// Manages contact folders in target mailboxes — lazily creating them when they don't
/// exist and caching folder IDs for the duration of a sync run to avoid redundant
/// Graph API calls across parallel mailbox tasks.
/// </summary>
public interface IContactFolderManager
{
    /// <summary>
    /// Returns the ID of the named contact folder within the specified mailbox,
    /// creating it if it doesn't already exist. Results are cached per mailbox
    /// for the duration of the sync run (reset between runs via <see cref="ResetCache"/>).
    /// </summary>
    /// <param name="mailboxEntraId">Entra ID (object ID or UPN) of the target mailbox.</param>
    /// <param name="folderName">Display name of the contact folder (e.g., "AFH Contacts").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The Graph contact folder ID.</returns>
    Task<string> GetOrCreateFolderAsync(
        string mailboxEntraId,
        string folderName,
        CancellationToken ct);

    /// <summary>
    /// Clears the folder ID cache. Called at the start of each sync run so that
    /// folders deleted between runs are re-discovered rather than returning stale IDs.
    /// </summary>
    void ResetCache();
}
