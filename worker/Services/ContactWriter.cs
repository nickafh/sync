using AFHSync.Worker.Graph;
using Microsoft.Graph.Models;

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

        if (payload.TryGetValue("PersonalNotes", out var personalNotes))
            contact.PersonalNotes = personalNotes;

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
