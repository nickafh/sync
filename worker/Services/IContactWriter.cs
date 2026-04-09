namespace AFHSync.Worker.Services;

/// <summary>
/// Result of a single operation within a batch request.
/// </summary>
public record BatchOperationResult(
    bool Success,
    string? GraphContactId = null,
    string? Error = null);

/// <summary>
/// Writes contacts to a target mailbox's contact folder via Microsoft Graph.
/// Handles CREATE (POST), UPDATE (PATCH), and DELETE operations.
/// Supports both individual and batch (up to 20 per HTTP call) operations.
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

    /// <summary>
    /// Creates multiple contacts in a single mailbox using Graph JSON batching ($batch).
    /// Bundles up to 20 requests per HTTP call for ~10-15x fewer round trips.
    /// </summary>
    /// <param name="mailboxEntraId">Entra ID of the target mailbox.</param>
    /// <param name="folderId">Graph contact folder ID within the mailbox.</param>
    /// <param name="operations">List of (correlationKey, payload) tuples.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary mapping correlationKey to result (success + graphContactId or error).</returns>
    Task<Dictionary<string, BatchOperationResult>> CreateContactsBatchAsync(
        string mailboxEntraId,
        string folderId,
        List<(string key, SortedDictionary<string, string> payload)> operations,
        CancellationToken ct);

    /// <summary>
    /// Updates multiple contacts in a single mailbox using Graph JSON batching ($batch).
    /// </summary>
    /// <param name="mailboxEntraId">Entra ID of the target mailbox.</param>
    /// <param name="operations">List of (correlationKey, graphContactId, payload) tuples.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary mapping correlationKey to result (success or error).</returns>
    Task<Dictionary<string, BatchOperationResult>> UpdateContactsBatchAsync(
        string mailboxEntraId,
        List<(string key, string graphContactId, SortedDictionary<string, string> payload)> operations,
        CancellationToken ct);

    /// <summary>
    /// Deletes multiple contacts from a single mailbox using Graph JSON batching ($batch).
    /// </summary>
    /// <param name="mailboxEntraId">Entra ID of the target mailbox.</param>
    /// <param name="operations">List of (correlationKey, graphContactId) tuples.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary mapping correlationKey to result (success or error).</returns>
    Task<Dictionary<string, BatchOperationResult>> DeleteContactsBatchAsync(
        string mailboxEntraId,
        List<(string key, string graphContactId)> operations,
        CancellationToken ct);
}
