namespace AFHSync.Worker.Services;

/// <summary>
/// Writes contacts to a target mailbox's contact folder via Microsoft Graph.
/// Handles CREATE (POST), UPDATE (PATCH), and DELETE operations.
/// </summary>
public interface IContactWriter
{
    /// <summary>
    /// Creates a new contact in the specified folder within the target mailbox.
    /// </summary>
    /// <param name="mailboxEntraId">Entra ID (object ID or UPN) of the target mailbox.</param>
    /// <param name="folderId">Graph contact folder ID within the mailbox.</param>
    /// <param name="payload">Normalized field dictionary from ContactPayloadBuilder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The Graph contact ID (stored in ContactSyncState.GraphContactId).</returns>
    Task<string> CreateContactAsync(
        string mailboxEntraId,
        string folderId,
        SortedDictionary<string, string> payload,
        CancellationToken ct);

    /// <summary>
    /// Updates an existing contact via PATCH using the stored Graph contact ID.
    /// </summary>
    /// <param name="mailboxEntraId">Entra ID of the target mailbox.</param>
    /// <param name="graphContactId">Graph contact ID stored in ContactSyncState.GraphContactId.</param>
    /// <param name="payload">Updated normalized field dictionary.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateContactAsync(
        string mailboxEntraId,
        string graphContactId,
        SortedDictionary<string, string> payload,
        CancellationToken ct);

    /// <summary>
    /// Deletes a contact from the target mailbox.
    /// </summary>
    /// <param name="mailboxEntraId">Entra ID of the target mailbox.</param>
    /// <param name="graphContactId">Graph contact ID to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteContactAsync(
        string mailboxEntraId,
        string graphContactId,
        CancellationToken ct);
}
