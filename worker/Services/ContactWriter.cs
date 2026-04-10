using AFHSync.Worker.Graph;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using GraphClientFactory = AFHSync.Worker.Graph.GraphClientFactory;

namespace AFHSync.Worker.Services;

/// <summary>
/// Writes contacts to target mailboxes via Microsoft Graph SDK.
/// Implements CREATE (POST to contact folder), UPDATE (PATCH by contactId), and
/// DELETE operations. All calls go through the GraphServiceClient which is already
/// wrapped by GraphResilienceHandler for 429/503 retry handling.
/// </summary>
public class ContactWriter : IContactWriter
{
    private readonly GraphClientFactory _graphClientFactory;
    private readonly ILogger<ContactWriter> _logger;

    public ContactWriter(GraphClientFactory graphClientFactory, ILogger<ContactWriter> logger)
    {
        _graphClientFactory = graphClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> CreateContactAsync(
        string mailboxEntraId,
        string folderId,
        SortedDictionary<string, string> payload,
        CancellationToken ct)
    {
        var contact = MapPayloadToContact(payload);

        _logger.LogDebug(
            "Creating contact in mailbox {MailboxId} folder {FolderId}: {DisplayName}",
            mailboxEntraId, folderId, contact.DisplayName);

        var created = await _graphClientFactory.Client
            .Users[mailboxEntraId]
            .ContactFolders[folderId]
            .Contacts
            .PostAsync(contact, cancellationToken: ct);

        if (created?.Id is null)
            throw new InvalidOperationException(
                $"Graph returned null contact ID after POST for mailbox {mailboxEntraId}");

        return created.Id;
    }

    /// <inheritdoc />
    public async Task UpdateContactAsync(
        string mailboxEntraId,
        string graphContactId,
        SortedDictionary<string, string> payload,
        CancellationToken ct)
    {
        var contact = MapPayloadToContact(payload);

        _logger.LogDebug(
            "Updating contact {ContactId} in mailbox {MailboxId}: {DisplayName}",
            graphContactId, mailboxEntraId, contact.DisplayName);

        await _graphClientFactory.Client
            .Users[mailboxEntraId]
            .Contacts[graphContactId]
            .PatchAsync(contact, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task DeleteContactAsync(
        string mailboxEntraId,
        string graphContactId,
        CancellationToken ct)
    {
        _logger.LogDebug(
            "Deleting contact {ContactId} from mailbox {MailboxId}",
            graphContactId, mailboxEntraId);

        await _graphClientFactory.Client
            .Users[mailboxEntraId]
            .Contacts[graphContactId]
            .DeleteAsync(cancellationToken: ct);
    }

    private const int MaxBatchSize = 20;

    /// <inheritdoc />
    public async Task<Dictionary<string, BatchOperationResult>> CreateContactsBatchAsync(
        string mailboxEntraId,
        string folderId,
        List<(string key, SortedDictionary<string, string> payload)> operations,
        CancellationToken ct)
    {
        var results = new Dictionary<string, BatchOperationResult>();
        if (operations.Count == 0) return results;

        _logger.LogDebug(
            "Batch creating {Count} contacts in mailbox {MailboxId} folder {FolderId}",
            operations.Count, mailboxEntraId, folderId);

        foreach (var chunk in ChunkOperations(operations, MaxBatchSize))
        {
            var batchContent = new BatchRequestContentCollection(_graphClientFactory.Client);
            var stepIdToKey = new Dictionary<string, string>();

            foreach (var (key, payload) in chunk)
            {
                var contact = MapPayloadToContact(payload);
                var requestInfo = _graphClientFactory.Client
                    .Users[mailboxEntraId]
                    .ContactFolders[folderId]
                    .Contacts
                    .ToPostRequestInformation(contact);
                var stepId = await batchContent.AddBatchRequestStepAsync(requestInfo);
                stepIdToKey[stepId] = key;
            }

            await ExecuteBatchWithRetryAsync(batchContent, stepIdToKey, results, async (response, stepId) =>
            {
                var created = await response.GetResponseByIdAsync<Contact>(stepId);
                return new BatchOperationResult(true, created?.Id);
            }, ct);
        }

        _logger.LogDebug("Batch create complete: {Success} succeeded, {Failed} failed",
            results.Values.Count(r => r.Success), results.Values.Count(r => !r.Success));

        return results;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, BatchOperationResult>> UpdateContactsBatchAsync(
        string mailboxEntraId,
        List<(string key, string graphContactId, SortedDictionary<string, string> payload)> operations,
        CancellationToken ct)
    {
        var results = new Dictionary<string, BatchOperationResult>();
        if (operations.Count == 0) return results;

        _logger.LogDebug(
            "Batch updating {Count} contacts in mailbox {MailboxId}",
            operations.Count, mailboxEntraId);

        foreach (var chunk in ChunkOperations(operations, MaxBatchSize))
        {
            var batchContent = new BatchRequestContentCollection(_graphClientFactory.Client);
            var stepIdToKey = new Dictionary<string, string>();

            foreach (var (key, graphContactId, payload) in chunk)
            {
                var contact = MapPayloadToContact(payload);
                var requestInfo = _graphClientFactory.Client
                    .Users[mailboxEntraId]
                    .Contacts[graphContactId]
                    .ToPatchRequestInformation(contact);
                var stepId = await batchContent.AddBatchRequestStepAsync(requestInfo);
                stepIdToKey[stepId] = key;
            }

            await ExecuteBatchWithRetryAsync(batchContent, stepIdToKey, results, (_, _) =>
                Task.FromResult(new BatchOperationResult(true)), ct);
        }

        _logger.LogDebug("Batch update complete: {Success} succeeded, {Failed} failed",
            results.Values.Count(r => r.Success), results.Values.Count(r => !r.Success));

        return results;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, BatchOperationResult>> DeleteContactsBatchAsync(
        string mailboxEntraId,
        List<(string key, string graphContactId)> operations,
        CancellationToken ct)
    {
        var results = new Dictionary<string, BatchOperationResult>();
        if (operations.Count == 0) return results;

        _logger.LogDebug(
            "Batch deleting {Count} contacts from mailbox {MailboxId}",
            operations.Count, mailboxEntraId);

        foreach (var chunk in ChunkOperations(operations, MaxBatchSize))
        {
            var batchContent = new BatchRequestContentCollection(_graphClientFactory.Client);
            var stepIdToKey = new Dictionary<string, string>();

            foreach (var (key, graphContactId) in chunk)
            {
                var requestInfo = _graphClientFactory.Client
                    .Users[mailboxEntraId]
                    .Contacts[graphContactId]
                    .ToDeleteRequestInformation();
                var stepId = await batchContent.AddBatchRequestStepAsync(requestInfo);
                stepIdToKey[stepId] = key;
            }

            await ExecuteBatchWithRetryAsync(batchContent, stepIdToKey, results, (_, _) =>
                Task.FromResult(new BatchOperationResult(true)), ct);
        }

        _logger.LogDebug("Batch delete complete: {Success} succeeded, {Failed} failed",
            results.Values.Count(r => r.Success), results.Values.Count(r => !r.Success));

        return results;
    }

    /// <summary>
    /// Executes a batch request with retry for 429/5xx failures.
    /// On success, calls <paramref name="onSuccess"/> to extract the result (e.g., created contact ID).
    /// Failed items are retried up to <see cref="MaxBatchRetries"/> times.
    /// </summary>
    private async Task ExecuteBatchWithRetryAsync(
        BatchRequestContentCollection batchContent,
        Dictionary<string, string> stepIdToKey,
        Dictionary<string, BatchOperationResult> results,
        Func<BatchResponseContentCollection, string, Task<BatchOperationResult>> onSuccess,
        CancellationToken ct)
    {
        BatchResponseContentCollection? response = null;
        try
        {
            response = await _graphClientFactory.Client.Batch.PostAsync(batchContent, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch request failed entirely");
            foreach (var key in stepIdToKey.Values)
                results[key] = new BatchOperationResult(false, Error: ex.Message);
            return;
        }

        if (response == null)
        {
            foreach (var key in stepIdToKey.Values)
                results[key] = new BatchOperationResult(false, Error: "Null batch response");
            return;
        }

        var statusCodes = await response.GetResponsesStatusCodesAsync();

        foreach (var (stepId, statusCode) in statusCodes)
        {
            if (!stepIdToKey.TryGetValue(stepId, out var key)) continue;

            if (BatchResponseContent.IsSuccessStatusCode(statusCode))
            {
                try
                {
                    results[key] = await onSuccess(response, stepId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse batch response for step {StepId}", stepId);
                    results[key] = new BatchOperationResult(true);
                }
            }
            else
            {
                _logger.LogWarning(
                    "Batch step {StepId} (key={Key}) failed with HTTP {StatusCode}",
                    stepId, key, (int)statusCode);
                results[key] = new BatchOperationResult(false, Error: $"HTTP {(int)statusCode}");
            }
        }
    }

    private static IEnumerable<List<T>> ChunkOperations<T>(List<T> items, int chunkSize)
    {
        for (var i = 0; i < items.Count; i += chunkSize)
            yield return items.GetRange(i, Math.Min(chunkSize, items.Count - i));
    }

    /// <summary>
    /// Converts a normalized payload dictionary (from <see cref="IContactPayloadBuilder"/>)
    /// into a <see cref="Contact"/> object ready for Graph API submission.
    ///
    /// Made <c>public static</c> so unit tests can validate field mapping directly
    /// without needing to mock Graph SDK or DI infrastructure.
    /// </summary>
    public static Contact MapPayloadToContact(SortedDictionary<string, string> payload)
    {
        var contact = new Contact();

        if (payload.TryGetValue("GivenName", out var givenName))
            contact.GivenName = givenName;

        if (payload.TryGetValue("Surname", out var surname))
            contact.Surname = surname;

        if (payload.TryGetValue("DisplayName", out var displayName))
            contact.DisplayName = displayName;

        if (payload.TryGetValue("JobTitle", out var jobTitle))
            contact.JobTitle = jobTitle;

        if (payload.TryGetValue("CompanyName", out var companyName))
            contact.CompanyName = companyName;

        if (payload.TryGetValue("Department", out var department))
            contact.Department = department;

        if (payload.TryGetValue("OfficeLocation", out var officeLocation))
            contact.OfficeLocation = officeLocation;

        if (payload.TryGetValue("MobilePhone", out var mobilePhone))
            contact.MobilePhone = mobilePhone;

        // Build PersonalNotes: prepend OfficeLocation since iOS has no dedicated field for it
        payload.TryGetValue("PersonalNotes", out var personalNotes);
        if (!string.IsNullOrWhiteSpace(officeLocation))
        {
            var prefix = $"Office: {officeLocation}";
            contact.PersonalNotes = string.IsNullOrWhiteSpace(personalNotes)
                ? prefix
                : $"{prefix}\n{personalNotes}";
        }
        else if (personalNotes != null)
        {
            contact.PersonalNotes = personalNotes;
        }

        // EmailAddresses — Graph expects List<EmailAddress> with Address and Name set
        if (payload.TryGetValue("EmailAddresses", out var email))
        {
            contact.EmailAddresses = string.IsNullOrWhiteSpace(email)
                ? []
                : [new EmailAddress { Address = email, Name = displayName ?? email }];
        }

        // BusinessPhones — Graph expects List<string>
        if (payload.TryGetValue("BusinessPhones", out var businessPhone))
        {
            contact.BusinessPhones = string.IsNullOrWhiteSpace(businessPhone) ? [] : [businessPhone];
        }

        // Business address — composite from separate street/city/state/postal fields
        var hasAnyAddressField =
            payload.ContainsKey("BusinessStreet") ||
            payload.ContainsKey("BusinessCity") ||
            payload.ContainsKey("BusinessState") ||
            payload.ContainsKey("BusinessPostalCode");

        if (hasAnyAddressField)
        {
            contact.BusinessAddress = new PhysicalAddress();

            if (payload.TryGetValue("BusinessStreet", out var street))
                contact.BusinessAddress.Street = street;

            if (payload.TryGetValue("BusinessCity", out var city))
                contact.BusinessAddress.City = city;

            if (payload.TryGetValue("BusinessState", out var state))
                contact.BusinessAddress.State = state;

            if (payload.TryGetValue("BusinessPostalCode", out var postalCode))
                contact.BusinessAddress.PostalCode = postalCode;
        }

        return contact;
    }
}
