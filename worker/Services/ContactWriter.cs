using System.Net;
using AFHSync.Worker.Graph;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
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
    private readonly ThrottleCounter _throttleCounter;
    private readonly ILogger<ContactWriter> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">
    /// Phase 4 (§4.2): how to wait between batch-step retries. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>;
    /// unit tests inject a recorder so they never sleep. The <see cref="CancellationToken"/> lets a
    /// Hangfire shutdown abandon the wait instead of blocking it uninterruptibly.
    /// </param>
    public ContactWriter(
        GraphClientFactory graphClientFactory,
        ThrottleCounter throttleCounter,
        ILogger<ContactWriter> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _graphClientFactory = graphClientFactory;
        _throttleCounter = throttleCounter;
        _logger = logger;
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>Phase 4 (§4.2): a throttled / timed-out batch step is re-posted at most this many times.</summary>
    internal const int MaxBatchStepRetries = 3;
    private static readonly TimeSpan StepRetryBaseDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>Statuses Graph uses for "try this step again later": 429, 503, 504.</summary>
    internal static bool IsRetryableStepStatus(HttpStatusCode status)
        => (int)status is 429 or 503 or 504;

    /// <summary>
    /// The wait before retry number <paramref name="attempt"/> (1-based): the server's Retry-After
    /// clamped to [0, 5 min] when it sent one, otherwise 2 s × attempt.
    /// </summary>
    internal static TimeSpan RetryDelayFor(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is TimeSpan ra)
        {
            if (ra < TimeSpan.Zero) return TimeSpan.Zero;
            return ra > MaxRetryAfter ? MaxRetryAfter : ra;
        }
        return StepRetryBaseDelay * attempt;
    }

    /// <inheritdoc />
    public async Task<string> CreateContactAsync(
        string mailboxEntraId,
        string folderId,
        SortedDictionary<string, string> payload,
        CancellationToken ct)
    {
        var contact = MapPayloadToContact(payload, isCreate: true);

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
        var contact = MapPayloadToContact(payload, isCreate: false);

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

    /// <summary>Phase 2 (§2.2): a 2xx batch step whose body has no contact id (or does not parse).</summary>
    public const string NoContactIdError = "no contact id in response";

    /// <summary>Maps a create-step response to a result; no id ⇒ failure, so no state row is written for it.</summary>
    internal static BatchOperationResult MapCreateResponse(Contact? created)
        => string.IsNullOrEmpty(created?.Id)
            ? new BatchOperationResult(false, Error: NoContactIdError)
            : new BatchOperationResult(true, created.Id);

    /// <inheritdoc />
    public async Task<Dictionary<string, BatchOperationResult>> CreateContactsBatchAsync(
        string mailboxEntraId,
        string folderId,
        List<(string key, SortedDictionary<string, string> payload)> operations,
        Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,
        CancellationToken ct)
    {
        var results = new Dictionary<string, BatchOperationResult>();
        if (operations.Count == 0) return results;

        _logger.LogDebug(
            "Batch creating {Count} contacts in mailbox {MailboxId} folder {FolderId}",
            operations.Count, mailboxEntraId, folderId);

        foreach (var chunk in ChunkOperations(operations, MaxBatchSize))
        {
            // Chunk boundary is the cancellation granularity (§2.6a follow-up): once a batch
            // is posted it always runs to completion, so we only ever check for shutdown
            // before starting a new one.
            ct.ThrowIfCancellationRequested();

            var byKey = chunk.ToDictionary(c => c.key);
            var keys = chunk.Select(c => c.key).ToList();

            await ExecuteBatchWithRetryAsync(
                mailboxEntraId,
                keys,
                key => _graphClientFactory.Client
                    .Users[mailboxEntraId]
                    .ContactFolders[folderId]
                    .Contacts
                    .ToPostRequestInformation(MapPayloadToContact(byKey[key].payload, isCreate: true)),
                results,
                async (response, stepId) =>
                {
                    var created = await response.GetResponseByIdAsync<Contact>(stepId);
                    return MapCreateResponse(created);
                },
                ct);
            await NotifyChunkCompletedAsync(onChunkCompleted, keys, results);
        }

        _logger.LogDebug("Batch create complete: {Success} succeeded, {Failed} failed",
            results.Values.Count(r => r.Success), results.Values.Count(r => !r.Success));

        return results;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, BatchOperationResult>> UpdateContactsBatchAsync(
        string mailboxEntraId,
        List<(string key, string graphContactId, SortedDictionary<string, string> payload)> operations,
        Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,
        CancellationToken ct)
    {
        var results = new Dictionary<string, BatchOperationResult>();
        if (operations.Count == 0) return results;

        _logger.LogDebug(
            "Batch updating {Count} contacts in mailbox {MailboxId}",
            operations.Count, mailboxEntraId);

        foreach (var chunk in ChunkOperations(operations, MaxBatchSize))
        {
            // Chunk boundary is the cancellation granularity — see CreateContactsBatchAsync.
            ct.ThrowIfCancellationRequested();

            var byKey = chunk.ToDictionary(c => c.key);
            var keys = chunk.Select(c => c.key).ToList();

            await ExecuteBatchWithRetryAsync(
                mailboxEntraId,
                keys,
                key => _graphClientFactory.Client
                    .Users[mailboxEntraId]
                    .Contacts[byKey[key].graphContactId]
                    .ToPatchRequestInformation(MapPayloadToContact(byKey[key].payload, isCreate: false)),
                results,
                (_, _) => Task.FromResult(new BatchOperationResult(true)),
                ct);
            await NotifyChunkCompletedAsync(onChunkCompleted, keys, results);
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
            // Chunk boundary is the cancellation granularity — see CreateContactsBatchAsync.
            ct.ThrowIfCancellationRequested();

            var byKey = chunk.ToDictionary(c => c.key);
            var keys = chunk.Select(c => c.key).ToList();

            await ExecuteBatchWithRetryAsync(
                mailboxEntraId,
                keys,
                key => _graphClientFactory.Client
                    .Users[mailboxEntraId]
                    .Contacts[byKey[key].graphContactId]
                    .ToDeleteRequestInformation(),
                results,
                (_, _) => Task.FromResult(new BatchOperationResult(true)),
                ct);
        }

        _logger.LogDebug("Batch delete complete: {Success} succeeded, {Failed} failed",
            results.Values.Count(r => r.Success), results.Values.Count(r => !r.Success));

        return results;
    }

    /// <summary>
    /// Builds and posts a batch for <paramref name="keys"/> (via <paramref name="buildStep"/>) and
    /// maps each step's answer into <paramref name="results"/>.
    ///
    /// Phase 4 (§4.2): steps answering 429/503/504 are retried — up to <see cref="MaxBatchStepRetries"/>
    /// times, waiting the largest per-step Retry-After (else 2 s × attempt) — by rebuilding a brand
    /// new batch from just the retried keys on each attempt. <see cref="BatchRequestContentCollection.NewBatchWithFailedRequests"/>
    /// was tried first, but Microsoft.Graph.Core 3.2.5's implementation re-adds each failed step via
    /// <c>AddBatchRequestStep(HttpRequestMessage)</c>, which always mints a fresh <see cref="Guid"/> for
    /// the new step id — it does not preserve the original id. Rebuilding the batch ourselves keeps
    /// step-id assignment entirely in our control, so the id ↔ key mapping is always correct. Each
    /// retried step bumps <see cref="ThrottleCounter"/> so SyncRun.ThrottleEvents reflects batch-level
    /// throttling too.
    ///
    /// Phase 4 (§4.2) / Phase 2 (§2.6a follow-up): batches — the initial post and every retry
    /// attempt alike — are built HERE from <paramref name="buildStep"/>. Each POST runs to
    /// completion with <see cref="CancellationToken.None"/>, so a shutdown mid-POST never turns a
    /// definitive Graph answer into a swallowed "canceled" failure. The shutdown token is consulted
    /// only before, or while, waiting for a retry: a shutdown during that wait abandons the
    /// remaining retries and returns the definitive <c>HTTP {status}</c> failures already recorded
    /// for them. A retried POST step that Graph did in fact apply before answering with a 5xx
    /// leaves a stray contact behind; the §3.7 reconcile (next run, via the reconcile flag) removes
    /// or adopts it.
    /// </summary>
    private async Task ExecuteBatchWithRetryAsync(
        string mailboxEntraId,
        IReadOnlyList<string> keys,
        Func<string, RequestInformation> buildStep,
        Dictionary<string, BatchOperationResult> results,
        Func<BatchResponseContentCollection, string, Task<BatchOperationResult>> onSuccess,
        CancellationToken ct)
    {
        var pendingKeys = keys;

        for (var attempt = 0; ; attempt++)
        {
            var batchContent = new BatchRequestContentCollection(_graphClientFactory.Client);
            var stepIdToKey = new Dictionary<string, string>();
            foreach (var key in pendingKeys)
            {
                var stepId = await batchContent.AddBatchRequestStepAsync(buildStep(key));
                stepIdToKey[stepId] = key;
            }

            BatchResponseContentCollection? response;
            try
            {
                response = await _graphClientFactory.Client.Batch.PostAsync(batchContent, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch request failed entirely (post {Attempt})", attempt + 1);
                // Phase 3 (§3.7): the request may have reached Graph — the caller reconciles the folder.
                // Only the keys in THIS post are unknown; earlier definitive answers stand.
                foreach (var key in pendingKeys)
                    results[key] = new BatchOperationResult(false, Error: ex.Message, OutcomeUnknown: true);
                return;
            }

            if (response == null)
            {
                foreach (var key in pendingKeys)
                    results[key] = new BatchOperationResult(false, Error: "Null batch response", OutcomeUnknown: true);
                return;
            }

            var statusCodes = await response.GetResponsesStatusCodesAsync();
            var retryKeys = new List<string>();
            TimeSpan? retryAfter = null;

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
                        results[key] = new BatchOperationResult(false, Error: NoContactIdError);
                    }
                }
                else if (IsRetryableStepStatus(statusCode) && attempt < MaxBatchStepRetries)
                {
                    retryKeys.Add(key);
                    _throttleCounter.Increment();
                    var stepRetryAfter = await ReadRetryAfterAsync(response, stepId);
                    if (stepRetryAfter is not null && (retryAfter is null || stepRetryAfter > retryAfter))
                        retryAfter = stepRetryAfter;
                    // Provisional — overwritten by the retry's answer (or kept if it keeps failing).
                    results[key] = new BatchOperationResult(false, Error: $"HTTP {(int)statusCode}");
                }
                else
                {
                    if (IsRetryableStepStatus(statusCode))
                    {
                        _logger.LogWarning(
                            "Batch step {StepId} (key={Key}) still failing with HTTP {StatusCode} after {Retries} retries",
                            stepId, key, (int)statusCode, MaxBatchStepRetries);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Batch step {StepId} (key={Key}) failed with HTTP {StatusCode}",
                            stepId, key, (int)statusCode);
                    }
                    results[key] = new BatchOperationResult(
                        false,
                        Error: $"HTTP {(int)statusCode}",
                        NotFound: (int)statusCode == 404);
                }
            }

            if (retryKeys.Count == 0)
                return;

            if (ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Shutdown requested; abandoning {Count} batch-step retries for mailbox {MailboxId}",
                    retryKeys.Count, mailboxEntraId);
                return;
            }

            var delay = RetryDelayFor(attempt + 1, retryAfter);
            _logger.LogWarning(
                "Retrying {Count} throttled batch step(s) for mailbox {MailboxId}, attempt {Attempt}/{Max}, after {DelayMs}ms",
                retryKeys.Count, mailboxEntraId, attempt + 1, MaxBatchStepRetries, delay.TotalMilliseconds);
            try
            {
                await _delay(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Shutdown requested; abandoning {Count} batch-step retries for mailbox {MailboxId}",
                    retryKeys.Count, mailboxEntraId);
                return;
            }
            pendingKeys = retryKeys;
        }
    }

    /// <summary>One step's Retry-After (delta seconds or an HTTP date), or null when absent or unreadable.</summary>
    private static async Task<TimeSpan?> ReadRetryAfterAsync(BatchResponseContentCollection response, string stepId)
    {
        try
        {
            using var http = await response.GetResponseByIdAsync(stepId);
            var header = http.Headers.RetryAfter;
            if (header?.Delta is TimeSpan delta) return delta;
            if (header?.Date is DateTimeOffset date) return date - DateTimeOffset.UtcNow;
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Phase 2 (§2.6a): hands the just-completed chunk's results (and only those) to the caller
    /// so state rows can be persisted before the next chunk goes out.
    /// </summary>
    private static async Task NotifyChunkCompletedAsync(
        Func<IReadOnlyDictionary<string, BatchOperationResult>, Task>? onChunkCompleted,
        IReadOnlyList<string> keys,
        Dictionary<string, BatchOperationResult> results)
    {
        if (onChunkCompleted is null) return;

        var chunkResults = new Dictionary<string, BatchOperationResult>();
        foreach (var key in keys)
        {
            if (results.TryGetValue(key, out var result))
                chunkResults[key] = result;
        }
        await onChunkCompleted(chunkResults);
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
    /// Phase 2 (§2.8): PersonalNotes is written only when the payload carries a "PersonalNotes"
    /// key or <paramref name="isCreate"/> is true. On updates where the field profile omits
    /// notes (AddMissing), phone-side edits survive even when OfficeLocation is synced.
    ///
    /// Made <c>public static</c> so unit tests can validate field mapping directly
    /// without needing to mock Graph SDK or DI infrastructure.
    /// </summary>
    public static Contact MapPayloadToContact(SortedDictionary<string, string> payload, bool isCreate)
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

        // Build PersonalNotes: prepend OfficeLocation since iOS has no dedicated field for it.
        // Phase 2 (§2.8): only when notes are part of this payload, or on create — a PATCH that
        // omits PersonalNotes leaves whatever the user typed on the phone alone.
        var hasNotesKey = payload.TryGetValue("PersonalNotes", out var personalNotes);
        if (hasNotesKey || isCreate)
        {
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

        // iOS/Android show GivenName + Surname, not DisplayName. When both are empty
        // (common for shared mailbox contacts like "IMMY-AFH"), phones fall back to
        // showing the email address. Populate GivenName from DisplayName as a fallback.
        if (string.IsNullOrWhiteSpace(contact.GivenName) && string.IsNullOrWhiteSpace(contact.Surname)
            && !string.IsNullOrWhiteSpace(contact.DisplayName))
        {
            contact.GivenName = contact.DisplayName;
        }

        return contact;
    }
}
