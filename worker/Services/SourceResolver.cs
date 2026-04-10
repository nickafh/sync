using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using GraphContact = Microsoft.Graph.Models.Contact;
using OrgContact = Microsoft.Graph.Models.OrgContact;

namespace AFHSync.Worker.Services;

/// <summary>
/// Resolves source members for a tunnel by querying Microsoft Graph /users
/// with the tunnel's stored $filter, handling pagination via PageIterator,
/// applying post-query source filtering, and upserting results to the database.
///
/// Per D-01: uses $top=999 (Graph max page size) + PageIterator for pagination.
/// Per D-02: accountEnabled filter in Graph query; mailbox type post-filtered.
/// Per D-03: SourceUser records upserted after resolution.
/// </summary>
public class SourceResolver : ISourceResolver
{
    private readonly AFHSync.Worker.Graph.GraphClientFactory _graphClientFactory;
    private readonly IDbContextFactory<AFHSyncDbContext> _dbContextFactory;
    private readonly ILogger<SourceResolver> _logger;

    private static readonly string[] ServiceAccountPrefixes =
        ["svc_", "service.", "admin_", "noreply"];

    public SourceResolver(
        AFHSync.Worker.Graph.GraphClientFactory graphClientFactory,
        IDbContextFactory<AFHSyncDbContext> dbContextFactory,
        ILogger<SourceResolver> logger)
    {
        _graphClientFactory = graphClientFactory;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves source members for the given tunnel: queries Graph for each source,
    /// combines results, applies post-query filtering, upserts to the database, and returns the filtered list.
    /// Routes to different Graph endpoints based on SourceType (DDG vs MailboxContacts).
    /// </summary>
    public async Task<List<SourceUser>> ResolveAsync(Tunnel tunnel, CancellationToken ct)
    {
        _logger.LogInformation("Resolving source members for tunnel {TunnelId} ({TunnelName}) with {SourceCount} source(s)",
            tunnel.Id, tunnel.Name, tunnel.TunnelSources.Count);

        var allSourceUsers = new List<SourceUser>();

        foreach (var source in tunnel.TunnelSources)
        {
            try
            {
                switch (source.SourceType)
                {
                    case SourceType.Ddg:
                    {
                        var graphUsers = await FetchGraphUsersAsync(source.SourceIdentifier, ct);
                        _logger.LogDebug("Fetched {Count} users from Graph for DDG source {SourceId} ({SourceName})",
                            graphUsers.Count, source.Id, source.SourceDisplayName);

                        var mapped = graphUsers.Select(MapGraphUserToSourceUser).ToList();
                        var filtered = ApplySourceFiltersWithLogging(mapped);

                        _logger.LogInformation(
                            "DDG source {SourceId}: {TotalCount} Graph users -> {FilteredCount} after source filtering",
                            source.Id, mapped.Count, filtered.Count);

                        allSourceUsers.AddRange(filtered);
                        break;
                    }

                    case SourceType.MailboxContacts:
                    {
                        var contacts = await FetchMailboxContactsAsync(source.SourceIdentifier, ct);
                        _logger.LogInformation("Fetched {Count} contacts from mailbox {Mailbox} for source {SourceId}",
                            contacts.Count, source.SourceIdentifier, source.Id);
                        allSourceUsers.AddRange(contacts);
                        break;
                    }

                    case SourceType.OrgContacts:
                    {
                        var orgContacts = await FetchOrgContactsAsync(ct);
                        _logger.LogDebug("Fetched {Count} org contacts from Graph for source {SourceId}",
                            orgContacts.Count, source.Id);

                        // Apply exclusion filters from the org_contact_filters table
                        var excludedIds = await GetExcludedOrgContactIdsAsync(tunnel.Id, ct);
                        var filtered = excludedIds.Count > 0
                            ? orgContacts.Where(c => !excludedIds.Contains(c.EntraId)).ToList()
                            : orgContacts;

                        _logger.LogInformation(
                            "OrgContacts source {SourceId}: {TotalCount} org contacts -> {FilteredCount} after exclusion filters",
                            source.Id, orgContacts.Count, filtered.Count);

                        allSourceUsers.AddRange(filtered);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Source {SourceId} ({SourceType}, {SourceIdentifier}) failed for tunnel {TunnelId} — skipping this source",
                    source.Id, source.SourceType, source.SourceIdentifier, tunnel.Id);
            }
        }

        // Deduplicate by EntraId across multiple sources
        var deduped = allSourceUsers
            .GroupBy(u => u.EntraId)
            .Select(g => g.First())
            .ToList();

        _logger.LogDebug("Total {RawCount} source users from all sources, {DedupedCount} after dedup",
            allSourceUsers.Count, deduped.Count);

        await UpsertSourceUsersAsync(deduped, ct);

        // Reload from DB to get actual IDs (raw SQL upsert doesn't populate in-memory IDs)
        var entraIds = deduped.Select(u => u.EntraId).ToList();
        await using var reloadDb = await _dbContextFactory.CreateDbContextAsync(ct);
        var reloaded = await reloadDb.SourceUsers
            .Where(u => entraIds.Contains(u.EntraId))
            .ToListAsync(ct);

        _logger.LogDebug("Reloaded {Count} source users with DB IDs", reloaded.Count);
        return reloaded;
    }

    /// <summary>
    /// Queries Graph /users with the stored $filter using PageIterator for pagination.
    /// Requires ConsistencyLevel=eventual header for advanced filter support (e.g., extension attributes).
    /// Per Pitfall 2 in RESEARCH.md: ConsistencyLevel header must be propagated in the requestConfigurator
    /// callback passed to PageIterator.
    /// </summary>
    private async Task<List<User>> FetchGraphUsersAsync(string graphFilter, CancellationToken ct)
    {
        var users = new List<User>();
        var client = _graphClientFactory.Client;

        var response = await client.Users.GetAsync(config =>
        {
            config.QueryParameters.Filter = graphFilter;
            config.QueryParameters.Top = 999;
            config.QueryParameters.Count = true;
            config.QueryParameters.Select =
            [
                "id", "displayName", "givenName", "surname", "mail", "proxyAddresses",
                "businessPhones", "mobilePhone", "jobTitle", "department",
                "officeLocation", "companyName", "streetAddress", "city",
                "state", "postalCode", "country", "accountEnabled", "userType",
                "showInAddressList", "onPremisesExtensionAttributes"
            ];
            // Required for advanced filters on extension attributes and $count
            config.Headers.Add("ConsistencyLevel", "eventual");
        }, ct);

        if (response == null)
            return users;

        var pageIterator = PageIterator<User, UserCollectionResponse>
            .CreatePageIterator(
                client,
                response,
                user =>
                {
                    // Skip users hidden from the Global Address List
                    if (user.ShowInAddressList == false)
                        return true; // continue iterating, just don't add
                    users.Add(user);
                    return true;
                },
                req =>
                {
                    // Per Pitfall 2: ConsistencyLevel must be propagated on subsequent page requests
                    req.Headers.Add("ConsistencyLevel", "eventual");
                    return req;
                });

        await pageIterator.IterateAsync(ct);
        return users;
    }

    /// <summary>
    /// Queries Graph /users/{mailboxEmail}/contacts to read contacts from a shared mailbox's
    /// Contacts folder, handling pagination via PageIterator.
    /// Returns already-mapped SourceUser entities (no DDG-specific filtering applied).
    /// </summary>
    private async Task<List<SourceUser>> FetchMailboxContactsAsync(string mailboxEmail, CancellationToken ct)
    {
        var sourceUsers = new List<SourceUser>();
        var client = _graphClientFactory.Client;

        var response = await client.Users[mailboxEmail].Contacts.GetAsync(config =>
        {
            config.QueryParameters.Select =
            [
                "id", "displayName", "givenName", "surname", "emailAddresses",
                "businessPhones", "mobilePhone", "jobTitle", "department",
                "companyName", "businessAddress", "personalNotes"
            ];
            config.QueryParameters.Top = 999;
        }, ct);

        if (response == null)
            return sourceUsers;

        var pageIterator = PageIterator<GraphContact, ContactCollectionResponse>
            .CreatePageIterator(
                client,
                response,
                contact =>
                {
                    sourceUsers.Add(MapGraphContactToSourceUser(contact));
                    return true;
                });

        await pageIterator.IterateAsync(ct);
        return sourceUsers;
    }

    /// <summary>
    /// Maps a Graph Contact object (from a shared mailbox) to a SourceUser entity.
    /// Graph Contacts have different properties than User objects:
    /// - emailAddresses (array) instead of mail/proxyAddresses
    /// - businessAddress (object) instead of flat street/city/state fields
    /// - personalNotes maps directly to Notes (not via extensionAttribute5)
    /// - No accountEnabled, userType, showInAddressList, or extensionAttributes
    /// </summary>
    public static SourceUser MapGraphContactToSourceUser(GraphContact contact)
    {
        var email = contact.EmailAddresses?.FirstOrDefault()?.Address;

        return new SourceUser
        {
            EntraId = contact.Id ?? string.Empty,
            DisplayName = contact.DisplayName,
            FirstName = contact.GivenName,
            LastName = contact.Surname,
            Email = email,
            BusinessPhone = contact.BusinessPhones?.FirstOrDefault(),
            MobilePhone = contact.MobilePhone,
            JobTitle = contact.JobTitle,
            Department = contact.Department,
            CompanyName = contact.CompanyName,
            StreetAddress = contact.BusinessAddress?.Street,
            City = contact.BusinessAddress?.City,
            State = contact.BusinessAddress?.State,
            PostalCode = contact.BusinessAddress?.PostalCode,
            Country = contact.BusinessAddress?.CountryOrRegion,
            Notes = contact.PersonalNotes,
            IsEnabled = true,
            MailboxType = "MailboxContact",
            LastFetchedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Maps a Graph User object to a SourceUser entity.
    /// Maps onPremisesExtensionAttributes to ExtensionAttr1-4.
    /// Sets MailboxType based on userType: "Member" -> "UserMailbox".
    /// </summary>
    public static SourceUser MapGraphUserToSourceUser(User graphUser)
    {
        // Treat null/empty userType as "Member" — on-prem synced users may not have userType set
        var userType = graphUser.UserType;
        var mailboxType = (string.IsNullOrEmpty(userType) || userType == "Member") ? "UserMailbox" : userType;

        return new SourceUser
        {
            EntraId = graphUser.Id ?? string.Empty,
            DisplayName = graphUser.DisplayName,
            FirstName = graphUser.GivenName,
            LastName = graphUser.Surname,
            Email = GetPrimarySmtp(graphUser) ?? graphUser.Mail,
            BusinessPhone = graphUser.BusinessPhones?.FirstOrDefault(),
            MobilePhone = graphUser.MobilePhone,
            JobTitle = graphUser.JobTitle,
            Department = graphUser.Department,
            OfficeLocation = graphUser.OfficeLocation,
            CompanyName = graphUser.CompanyName,
            StreetAddress = graphUser.StreetAddress,
            City = graphUser.City,
            State = graphUser.State,
            PostalCode = graphUser.PostalCode,
            Country = graphUser.Country,
            // Treat null accountEnabled as true — org users with mailboxes default to enabled
            IsEnabled = graphUser.AccountEnabled ?? true,
            MailboxType = mailboxType,
            // extensionAttribute5 holds the Entra "Notes" field (AD `info` attribute).
            // Populated by Exchange PowerShell: Get-User | % { Set-User $_.Identity -CustomAttribute5 $_.Notes }
            Notes = graphUser.OnPremisesExtensionAttributes?.ExtensionAttribute5,
            ExtensionAttr1 = graphUser.OnPremisesExtensionAttributes?.ExtensionAttribute1,
            ExtensionAttr2 = graphUser.OnPremisesExtensionAttributes?.ExtensionAttribute2,
            ExtensionAttr3 = graphUser.OnPremisesExtensionAttributes?.ExtensionAttribute3,
            ExtensionAttr4 = graphUser.OnPremisesExtensionAttributes?.ExtensionAttribute4,
            LastFetchedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Applies post-query source filtering to exclude:
    /// - Disabled accounts (IsEnabled = false)
    /// - Non-UserMailbox types (shared, room, equipment mailboxes)
    /// - Service accounts (email prefix: svc_, service., admin_, noreply)
    /// - Users with null or empty email addresses
    ///
    /// Public for unit testability per plan spec.
    /// </summary>
    /// <summary>
    /// Static overload for unit test compatibility. No logging.
    /// </summary>
    public static List<SourceUser> ApplySourceFilters(IEnumerable<SourceUser> users)
    {
        return users.Where(u =>
            u.IsEnabled &&
            u.MailboxType == "UserMailbox" &&
            !string.IsNullOrWhiteSpace(u.Email) &&
            !IsServiceAccount(u.Email!)
        ).ToList();
    }

    /// <summary>
    /// Instance method with diagnostic logging showing exactly why each user was filtered out.
    /// </summary>
    private List<SourceUser> ApplySourceFiltersWithLogging(IEnumerable<SourceUser> users)
    {
        var userList = users.ToList();
        var filtered = new List<SourceUser>();

        foreach (var u in userList)
        {
            if (!u.IsEnabled)
            {
                _logger.LogDebug("Filtered out {Email} ({EntraId}): disabled account", u.Email, u.EntraId);
                continue;
            }
            if (u.MailboxType != "UserMailbox")
            {
                _logger.LogDebug("Filtered out {Email} ({EntraId}): mailbox type '{Type}'", u.Email, u.EntraId, u.MailboxType);
                continue;
            }
            if (string.IsNullOrWhiteSpace(u.Email))
            {
                _logger.LogDebug("Filtered out {DisplayName} ({EntraId}): no email address", u.DisplayName, u.EntraId);
                continue;
            }
            if (IsServiceAccount(u.Email!))
            {
                _logger.LogDebug("Filtered out {Email} ({EntraId}): service account prefix", u.Email, u.EntraId);
                continue;
            }
            filtered.Add(u);
        }

        if (filtered.Count < userList.Count)
        {
            _logger.LogInformation("Source filter removed {Count} users: {Disabled} disabled, {MailboxType} non-user mailbox, {NoEmail} no email, {ServiceAcct} service accounts",
                userList.Count - filtered.Count,
                userList.Count(u => !u.IsEnabled),
                userList.Count(u => u.IsEnabled && u.MailboxType != "UserMailbox"),
                userList.Count(u => u.IsEnabled && u.MailboxType == "UserMailbox" && string.IsNullOrWhiteSpace(u.Email)),
                userList.Count(u => u.IsEnabled && u.MailboxType == "UserMailbox" && !string.IsNullOrWhiteSpace(u.Email) && IsServiceAccount(u.Email!)));
        }

        return filtered;
    }

    /// <summary>
    /// Extracts the primary SMTP address from proxyAddresses.
    /// The primary is the entry prefixed with uppercase "SMTP:" (e.g., "SMTP:David@example.com").
    /// </summary>
    private static string? GetPrimarySmtp(User graphUser)
    {
        var primary = graphUser.ProxyAddresses?
            .FirstOrDefault(p => p.StartsWith("SMTP:", StringComparison.Ordinal));
        return primary?[5..]; // Strip "SMTP:" prefix
    }

    private static bool IsServiceAccount(string email)
    {
        var localPart = email.Split('@')[0];
        return ServiceAccountPrefixes.Any(prefix =>
            localPart.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Queries Graph /contacts to fetch all tenant organizational contacts (external contacts
    /// managed in Exchange Admin Center). These are NOT user accounts — they are MailContact
    /// objects with different properties than Entra users.
    /// No profile photos, no extensionAttributes, no officeLocation, no accountEnabled.
    /// </summary>
    private async Task<List<SourceUser>> FetchOrgContactsAsync(CancellationToken ct)
    {
        var sourceUsers = new List<SourceUser>();
        var client = _graphClientFactory.Client;

        var response = await client.Contacts.GetAsync(config =>
        {
            config.QueryParameters.Select =
            [
                "id", "displayName", "givenName", "surname", "mail",
                "phones", "addresses", "companyName", "department", "jobTitle"
            ];
            config.QueryParameters.Top = 999;
        }, ct);

        if (response == null)
            return sourceUsers;

        var pageIterator = PageIterator<OrgContact, OrgContactCollectionResponse>
            .CreatePageIterator(
                client,
                response,
                orgContact =>
                {
                    sourceUsers.Add(MapOrgContactToSourceUser(orgContact));
                    return true;
                });

        await pageIterator.IterateAsync(ct);
        return sourceUsers;
    }

    /// <summary>
    /// Maps a Graph OrgContact object to a SourceUser entity.
    /// OrgContacts have different property shapes than Users or mailbox Contacts:
    /// - phones (collection of Phone with type/number) instead of businessPhones/mobilePhone
    /// - addresses (collection of PhysicalOfficeAddress) instead of flat street/city/state
    /// - No extensionAttributes, officeLocation, accountEnabled, or photos
    /// </summary>
    public static SourceUser MapOrgContactToSourceUser(OrgContact orgContact)
    {
        var businessPhone = orgContact.Phones?
            .FirstOrDefault(p => string.Equals(p.Type?.ToString(), "business", StringComparison.OrdinalIgnoreCase))?.Number;
        var mobilePhone = orgContact.Phones?
            .FirstOrDefault(p => string.Equals(p.Type?.ToString(), "mobile", StringComparison.OrdinalIgnoreCase))?.Number;
        // Fall back to first available phone if no business phone
        businessPhone ??= orgContact.Phones?.FirstOrDefault()?.Number;

        var address = orgContact.Addresses?.FirstOrDefault();

        return new SourceUser
        {
            EntraId = orgContact.Id ?? string.Empty,
            DisplayName = orgContact.DisplayName,
            FirstName = orgContact.GivenName,
            LastName = orgContact.Surname,
            Email = orgContact.Mail,
            BusinessPhone = businessPhone,
            MobilePhone = mobilePhone,
            JobTitle = orgContact.JobTitle,
            Department = orgContact.Department,
            CompanyName = orgContact.CompanyName,
            StreetAddress = address?.Street,
            City = address?.City,
            State = address?.State,
            PostalCode = address?.PostalCode,
            Country = address?.CountryOrRegion,
            IsEnabled = true,
            MailboxType = "OrgContact",
            LastFetchedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Returns the set of org contact IDs that are excluded for a given tunnel.
    /// </summary>
    private async Task<HashSet<string>> GetExcludedOrgContactIdsAsync(int tunnelId, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var excludedIds = await db.OrgContactFilters
            .Where(f => f.TunnelId == tunnelId && f.IsExcluded)
            .Select(f => f.OrgContactId)
            .ToListAsync(ct);
        return excludedIds.ToHashSet();
    }

    /// <summary>
    /// Upserts resolved SourceUser records to the database using batched raw SQL
    /// INSERT ... ON CONFLICT (entra_id) DO UPDATE for performance.
    /// Batches of 25 rows (25 columns * 25 rows = 625 params, well within PG limits).
    /// Per D-03 and Pattern 8 in RESEARCH.md.
    /// </summary>
    private async Task UpsertSourceUsersAsync(List<SourceUser> users, CancellationToken ct)
    {
        if (users.Count == 0)
            return;

        const int batchSize = 25;
        const int colCount = 26;
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        for (int offset = 0; offset < users.Count; offset += batchSize)
        {
            var batch = users.Skip(offset).Take(batchSize).ToList();
            var sql = new System.Text.StringBuilder();
            sql.AppendLine("""
                INSERT INTO source_users (
                    entra_id, display_name, first_name, last_name, email,
                    business_phone, mobile_phone, job_title, department,
                    office_location, company_name, street_address, city,
                    state, postal_code, country, notes, is_enabled, mailbox_type,
                    extension_attr_1, extension_attr_2, extension_attr_3, extension_attr_4,
                    last_fetched_at, created_at, updated_at
                ) VALUES
                """);

            var parameters = new List<object?>();
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sql.AppendLine(",");
                var p = i * colCount;
                sql.Append($"({{{p}}}, {{{p+1}}}, {{{p+2}}}, {{{p+3}}}, {{{p+4}}}, {{{p+5}}}, {{{p+6}}}, {{{p+7}}}, {{{p+8}}}, {{{p+9}}}, {{{p+10}}}, {{{p+11}}}, {{{p+12}}}, {{{p+13}}}, {{{p+14}}}, {{{p+15}}}, {{{p+16}}}, {{{p+17}}}, {{{p+18}}}, {{{p+19}}}, {{{p+20}}}, {{{p+21}}}, {{{p+22}}}, {{{p+23}}}, {{{p+24}}}, {{{p+25}}})");

                var user = batch[i];
                parameters.AddRange([
                    user.EntraId, (object?)user.DisplayName, (object?)user.FirstName,
                    (object?)user.LastName, (object?)user.Email,
                    (object?)user.BusinessPhone, (object?)user.MobilePhone,
                    (object?)user.JobTitle, (object?)user.Department,
                    (object?)user.OfficeLocation, (object?)user.CompanyName,
                    (object?)user.StreetAddress, (object?)user.City,
                    (object?)user.State, (object?)user.PostalCode,
                    (object?)user.Country, (object?)user.Notes, user.IsEnabled, (object?)user.MailboxType,
                    (object?)user.ExtensionAttr1, (object?)user.ExtensionAttr2,
                    (object?)user.ExtensionAttr3, (object?)user.ExtensionAttr4,
                    user.LastFetchedAt, user.CreatedAt, user.UpdatedAt
                ]);
            }

            sql.AppendLine("""

                ON CONFLICT (entra_id) DO UPDATE SET
                    display_name = EXCLUDED.display_name,
                    first_name = EXCLUDED.first_name,
                    last_name = EXCLUDED.last_name,
                    email = EXCLUDED.email,
                    business_phone = EXCLUDED.business_phone,
                    mobile_phone = EXCLUDED.mobile_phone,
                    job_title = EXCLUDED.job_title,
                    department = EXCLUDED.department,
                    office_location = EXCLUDED.office_location,
                    company_name = EXCLUDED.company_name,
                    street_address = EXCLUDED.street_address,
                    city = EXCLUDED.city,
                    state = EXCLUDED.state,
                    postal_code = EXCLUDED.postal_code,
                    country = EXCLUDED.country,
                    notes = EXCLUDED.notes,
                    is_enabled = EXCLUDED.is_enabled,
                    mailbox_type = EXCLUDED.mailbox_type,
                    extension_attr_1 = EXCLUDED.extension_attr_1,
                    extension_attr_2 = EXCLUDED.extension_attr_2,
                    extension_attr_3 = EXCLUDED.extension_attr_3,
                    extension_attr_4 = EXCLUDED.extension_attr_4,
                    last_fetched_at = EXCLUDED.last_fetched_at,
                    updated_at = EXCLUDED.updated_at
                """);

            await db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray()!, ct);
        }

        _logger.LogDebug("Upserted {Count} source users to database in batches of {BatchSize}", users.Count, batchSize);
    }
}
