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
                        var contacts = await FetchMailboxContactsAsync(source.SourceIdentifier, source.ContactFolderId, ct);
                        _logger.LogInformation("Fetched {Count} contacts from mailbox {Mailbox} folder {Folder} for source {SourceId}",
                            contacts.Count, source.SourceIdentifier, source.ContactFolderName ?? "root", source.Id);
                        await EnrichMailboxContactsFromDirectoryAsync(contacts, source.SourceIdentifier, ct);
                        allSourceUsers.AddRange(contacts);
                        break;
                    }

                    case SourceType.OrgContacts:
                    {
                        var orgContacts = await FetchOrgContactsAsync(ct);
                        _logger.LogDebug("Fetched {Count} org contacts from Graph for source {SourceId}",
                            orgContacts.Count, source.Id);

                        // Check if tunnel uses TunnelContactExclusions (Contact Filters UI).
                        // If so, skip legacy OrgContactFilters — Contact Filters is the single
                        // source of truth for include/exclude decisions across all source types.
                        await using var checkDb = await _dbContextFactory.CreateDbContextAsync(ct);
                        var hasTunnelExclusions = await checkDb.TunnelContactExclusions
                            .AnyAsync(e => e.TunnelId == tunnel.Id, ct);

                        List<SourceUser> filtered;
                        if (hasTunnelExclusions)
                        {
                            filtered = orgContacts;
                            _logger.LogInformation(
                                "OrgContacts source {SourceId}: {Count} org contacts (skipping OrgContactFilters — tunnel uses Contact Filters)",
                                source.Id, orgContacts.Count);
                        }
                        else
                        {
                            var excludedIds = await GetExcludedOrgContactIdsAsync(tunnel.Id, ct);
                            filtered = excludedIds.Count > 0
                                ? orgContacts.Where(c => !excludedIds.Contains(c.EntraId)).ToList()
                                : orgContacts;
                            _logger.LogInformation(
                                "OrgContacts source {SourceId}: {TotalCount} org contacts -> {FilteredCount} after OrgContactFilters",
                                source.Id, orgContacts.Count, filtered.Count);
                        }

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

        // Deduplicate across multiple sources.
        // - Non-MailboxContact users (DDG, OrgContacts) dedup on EntraId, case-insensitive.
        // - MailboxContact users dedup on a composite name+email key (with EntraId fallback) so
        //   the same person appearing in two selected mailbox folders (e.g. "Agents" + "Brokers")
        //   only produces one contact in the resulting phone list. EntraId for MailboxContacts is
        //   the per-folder Graph Contact.Id, which differs across folders even for the same person.
        var (mailboxContacts, otherUsers) = allSourceUsers
            .GroupBy(u => u.MailboxType == "MailboxContact")
            .Aggregate(
                (Mailbox: new List<SourceUser>(), Other: new List<SourceUser>()),
                (acc, g) =>
                {
                    if (g.Key) acc.Mailbox.AddRange(g);
                    else acc.Other.AddRange(g);
                    return acc;
                });

        var otherDeduped = otherUsers
            .Where(u => !string.IsNullOrEmpty(u.EntraId))
            .GroupBy(u => u.EntraId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var mailboxDeduped = mailboxContacts
            .Select(u => (User: u, Key: BuildMailboxContactDedupKey(u)))
            .Where(t => t.Key != null)
            .GroupBy(t => t.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First().User)
            .ToList();

        var deduped = otherDeduped.Concat(mailboxDeduped).ToList();

        _logger.LogInformation(
            "Tunnel {TunnelName}: MailboxContact dedup: {MailboxRaw} raw -> {MailboxDeduped} deduped; Other dedup: {OtherRaw} raw -> {OtherDeduped} deduped",
            tunnel.Name, mailboxContacts.Count, mailboxDeduped.Count, otherUsers.Count, otherDeduped.Count);

        _logger.LogInformation("Tunnel {TunnelName}: {RawCount} source users from all sources, {DedupedCount} after dedup",
            tunnel.Name, allSourceUsers.Count, deduped.Count);

        await UpsertSourceUsersAsync(deduped, ct);

        // Reload from DB to get actual IDs (raw SQL upsert doesn't populate in-memory IDs)
        var entraIds = deduped.Select(u => u.EntraId).ToList();
        await using var reloadDb = await _dbContextFactory.CreateDbContextAsync(ct);
        var reloaded = await reloadDb.SourceUsers
            .Where(u => entraIds.Contains(u.EntraId))
            .ToListAsync(ct);

        _logger.LogInformation("Tunnel {TunnelName}: reloaded {ReloadedCount} source users from DB (expected {ExpectedCount})",
            tunnel.Name, reloaded.Count, deduped.Count);
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
    /// Queries Graph for contacts from a mailbox. Reads from the root Contacts folder
    /// by default, or from a specific subfolder when contactFolderId is provided.
    /// Handles pagination via PageIterator.
    /// </summary>
    private async Task<List<SourceUser>> FetchMailboxContactsAsync(string mailboxEmail, string? contactFolderId, CancellationToken ct)
    {
        var sourceUsers = new List<SourceUser>();
        var client = _graphClientFactory.Client;

        var selectFields = new[]
        {
            "id", "displayName", "givenName", "surname", "emailAddresses",
            "businessPhones", "mobilePhone", "jobTitle", "department",
            "companyName", "businessAddress", "personalNotes"
        };

        ContactCollectionResponse? response;
        if (!string.IsNullOrEmpty(contactFolderId))
        {
            response = await client.Users[mailboxEmail].ContactFolders[contactFolderId].Contacts.GetAsync(config =>
            {
                config.QueryParameters.Select = selectFields;
                config.QueryParameters.Top = 999;
            }, ct);
        }
        else
        {
            response = await client.Users[mailboxEmail].Contacts.GetAsync(config =>
            {
                config.QueryParameters.Select = selectFields;
                config.QueryParameters.Top = 999;
            }, ct);
        }

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
            HiddenFromGal = !(graphUser.ShowInAddressList ?? true),
            MailboxType = mailboxType,
            // Cloud-only: Graph's User.aboutMe is the "Notes" field visible in Teams/OWA contact
            // cards and edited via M365 admin center / Entra portal. Falls back to
            // onPremisesExtensionAttributes.extensionAttribute5 for any remaining AD-synced users
            // whose on-prem `info` attribute still flows up via AD Connect.
            Notes = graphUser.AboutMe ?? graphUser.OnPremisesExtensionAttributes?.ExtensionAttribute5,
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
    /// Builds a dedup key for a MailboxContact-sourced SourceUser so the same person appearing
    /// in multiple selected mailbox folders only produces one contact in the resulting phone list.
    ///
    /// Key strategy (in order):
    /// - If both DisplayName and Email are non-whitespace: composite "{name}|{email}" lowercase trimmed.
    /// - Else if EntraId is non-empty: "id:{entraId}" (lowercase prefix avoids collision with name+email keys).
    /// - Else: null (no usable identity — caller should skip).
    ///
    /// EntraId for MailboxContacts is the per-folder Graph Contact.Id, which differs across folders
    /// even for the same person — so we cannot dedup on EntraId alone.
    ///
    /// Public for unit testability.
    /// </summary>
    public static string? BuildMailboxContactDedupKey(SourceUser user)
    {
        var hasName = !string.IsNullOrWhiteSpace(user.DisplayName);
        var hasEmail = !string.IsNullOrWhiteSpace(user.Email);

        if (hasName && hasEmail)
        {
            return $"{user.DisplayName!.Trim().ToLowerInvariant()}|{user.Email!.Trim().ToLowerInvariant()}";
        }

        if (!string.IsNullOrEmpty(user.EntraId))
        {
            return $"id:{user.EntraId}";
        }

        return null;
    }

    /// <summary>
    /// Returns the subset of mailbox contacts that need directory enrichment:
    /// contacts with a non-empty email AND both BusinessPhone and MobilePhone null/whitespace.
    /// MailboxContacts-sourced Contacts referencing other tenant shared mailboxes don't carry
    /// phone fields on the Contact object — those live on the referenced User record.
    ///
    /// Public for unit testability.
    /// </summary>
    public static List<SourceUser> BuildEnrichmentCandidates(IEnumerable<SourceUser> contacts)
    {
        return contacts.Where(c =>
            !string.IsNullOrWhiteSpace(c.Email) &&
            string.IsNullOrWhiteSpace(c.BusinessPhone) &&
            string.IsNullOrWhiteSpace(c.MobilePhone)
        ).ToList();
    }

    /// <summary>
    /// Indexes directory users by lowercased email from both <see cref="User.Mail"/> and
    /// <see cref="User.ProxyAddresses"/> (with "smtp:"/"SMTP:" prefix stripped). Returns a
    /// case-insensitive dictionary mapping email -> (BusinessPhone, MobilePhone). Multiple keys
    /// can map to the same user. Users with no phones are still indexed (match produces a no-op).
    ///
    /// Public for unit testability.
    /// </summary>
    public static Dictionary<string, (string? BusinessPhone, string? MobilePhone)>
        BuildDirectoryPhoneMap(IEnumerable<User> users)
    {
        var map = new Dictionary<string, (string? BusinessPhone, string? MobilePhone)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            var phones = (user.BusinessPhones?.FirstOrDefault(), user.MobilePhone);

            if (!string.IsNullOrWhiteSpace(user.Mail))
            {
                var key = user.Mail.Trim().ToLowerInvariant();
                map[key] = phones;
            }

            if (user.ProxyAddresses != null)
            {
                foreach (var proxy in user.ProxyAddresses)
                {
                    if (string.IsNullOrWhiteSpace(proxy))
                        continue;

                    // Strip "smtp:" / "SMTP:" prefix (case-insensitive, 5 chars)
                    var address = proxy.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase)
                        ? proxy[5..]
                        : proxy;

                    if (string.IsNullOrWhiteSpace(address))
                        continue;

                    var key = address.Trim().ToLowerInvariant();
                    map[key] = phones;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Indexes directory users by lowercased email (from <see cref="User.Mail"/> and
    /// <see cref="User.ProxyAddresses"/>) -> <see cref="User.Id"/>. Mirrors
    /// <see cref="BuildDirectoryPhoneMap"/>'s key-collection strategy: strips
    /// "smtp:"/"SMTP:" prefix, case-insensitive, multiple keys per user. Users with
    /// null/empty Id are skipped entirely.
    ///
    /// Public for unit testability.
    /// </summary>
    public static Dictionary<string, string> BuildDirectoryUserIdMap(
        IEnumerable<User> users)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            if (string.IsNullOrWhiteSpace(user.Id))
                continue;

            if (!string.IsNullOrWhiteSpace(user.Mail))
            {
                map[user.Mail.Trim().ToLowerInvariant()] = user.Id;
            }

            if (user.ProxyAddresses != null)
            {
                foreach (var proxy in user.ProxyAddresses)
                {
                    if (string.IsNullOrWhiteSpace(proxy))
                        continue;

                    var address = proxy.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase)
                        ? proxy[5..]
                        : proxy;

                    if (string.IsNullOrWhiteSpace(address))
                        continue;

                    map[address.Trim().ToLowerInvariant()] = user.Id;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Mutates candidates in place: for each candidate, if the lowercased email is in the map,
    /// backfill BusinessPhone ONLY when the candidate's current BusinessPhone is null/whitespace,
    /// and backfill MobilePhone ONLY when the candidate's current MobilePhone is null/whitespace.
    /// Non-empty existing values are always preserved.
    ///
    /// Returns (matchedCount, unmatchedCount).
    /// Public for unit testability.
    /// </summary>
    public static (int Matched, int Unmatched) ApplyDirectoryEnrichment(
        List<SourceUser> candidates,
        IReadOnlyDictionary<string, (string? BusinessPhone, string? MobilePhone)> map)
        => ApplyDirectoryEnrichment(candidates, map, userIdMap: null);

    /// <summary>
    /// Same backfill behavior as the 2-arg overload, plus: when <paramref name="userIdMap"/>
    /// is non-null and contains the candidate's email, writes the matched tenant user's
    /// Entra Id to <see cref="SourceUser.MatchedUserEntraId"/>. Used by the photo-sync
    /// path (260417-1lx) to route MailboxContact photo fetches to the linked tenant user.
    ///
    /// Existing MatchedUserEntraId values on unmatched candidates are preserved
    /// (we never null out a previously-matched value during a run that doesn't match).
    /// </summary>
    public static (int Matched, int Unmatched) ApplyDirectoryEnrichment(
        List<SourceUser> candidates,
        IReadOnlyDictionary<string, (string? BusinessPhone, string? MobilePhone)> phoneMap,
        IReadOnlyDictionary<string, string>? userIdMap)
    {
        int matched = 0;
        int unmatched = 0;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Email))
            {
                unmatched++;
                continue;
            }

            var key = candidate.Email.Trim().ToLowerInvariant();
            if (!phoneMap.TryGetValue(key, out var phones))
            {
                unmatched++;
                continue;
            }

            matched++;

            if (string.IsNullOrWhiteSpace(candidate.BusinessPhone))
            {
                candidate.BusinessPhone = phones.BusinessPhone;
            }

            if (string.IsNullOrWhiteSpace(candidate.MobilePhone))
            {
                candidate.MobilePhone = phones.MobilePhone;
            }

            if (userIdMap != null && userIdMap.TryGetValue(key, out var userId))
            {
                candidate.MatchedUserEntraId = userId;
            }
        }

        return (matched, unmatched);
    }

    /// <summary>
    /// Chunks emails so each chunk stays within Graph's OR-clause limit for
    /// <c>proxyAddresses/any(...)</c> advanced-query filters. Graph caps advanced-query
    /// <c>$filter</c> expressions at ~15 OR clauses per <c>any()</c>. Each email emits
    /// two OR terms (lowercase <c>smtp:</c> aliases AND uppercase <c>SMTP:</c> primary),
    /// so each chunk holds at most <paramref name="maxOrClauses"/> / 2 emails.
    ///
    /// Default maxOrClauses=14 leaves one slot of headroom.
    /// Public for unit testability.
    /// </summary>
    public static List<List<string>> ChunkEmailsForFilter(IEnumerable<string> emails, int maxOrClauses = 14)
    {
        var emailsPerChunk = Math.Max(1, maxOrClauses / 2);
        var chunks = new List<List<string>>();
        var currentChunk = new List<string>();

        foreach (var email in emails)
        {
            if (currentChunk.Count >= emailsPerChunk)
            {
                chunks.Add(currentChunk);
                currentChunk = new List<string>();
            }
            currentChunk.Add(email);
        }

        if (currentChunk.Count > 0)
            chunks.Add(currentChunk);

        return chunks;
    }

    /// <summary>
    /// Enriches MailboxContacts-sourced contacts by backfilling empty BusinessPhone / MobilePhone
    /// values from the tenant directory. OWA shows phones on Contact references to shared mailboxes
    /// via a server-side join against the User/shared-mailbox directory record; the Contact object
    /// itself doesn't carry the phones. This method replicates that join so phones sync to phones.
    ///
    /// Conservative:
    /// - Empty-only backfill (never overwrites a non-empty existing phone).
    /// - Case-insensitive email match against User.Mail and every entry in ProxyAddresses.
    /// - Chunked filter expressions stay under Graph's ~15KB cap.
    /// - ConsistencyLevel: eventual on initial request + PageIterator requestConfigurator.
    /// - Graceful degradation: a failed chunk logs a warning and continues; the whole sync is never
    ///   blocked by enrichment failures.
    /// </summary>
    private async Task EnrichMailboxContactsFromDirectoryAsync(
        List<SourceUser> contacts,
        string mailboxEmail,
        CancellationToken ct)
    {
        var candidates = BuildEnrichmentCandidates(contacts);
        if (candidates.Count == 0)
        {
            _logger.LogDebug("No MailboxContacts need directory enrichment for mailbox {Mailbox}", mailboxEmail);
            return;
        }

        // Fetch all tenant users in one paginated call. Graph's proxyAddresses/any()
        // advanced filter is unreliable with compound OR expressions — it returns
        // "Unsupported Query" for many tenants. Fetching the full directory is
        // simpler and more reliable for the small tenants this tool targets.
        var allUsers = new List<User>();
        try
        {
            var client = _graphClientFactory.Client;
            var response = await client.Users.GetAsync(config =>
            {
                config.QueryParameters.Top = 999;
                config.QueryParameters.Select =
                [
                    "id", "mail", "proxyAddresses", "businessPhones", "mobilePhone"
                ];
            }, ct);

            if (response != null)
            {
                var pageIterator = PageIterator<User, UserCollectionResponse>
                    .CreatePageIterator(
                        client,
                        response,
                        user =>
                        {
                            allUsers.Add(user);
                            return true;
                        });

                await pageIterator.IterateAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Directory enrichment fetch failed for mailbox {Mailbox} — skipping enrichment (contacts stay with current phone values)",
                mailboxEmail);
            return;
        }

        var phoneMap = BuildDirectoryPhoneMap(allUsers);
        var userIdMap = BuildDirectoryUserIdMap(allUsers);
        var (matched, unmatched) = ApplyDirectoryEnrichment(candidates, phoneMap, userIdMap);

        var matchedUsers = candidates.Count(c => !string.IsNullOrWhiteSpace(c.MatchedUserEntraId));
        _logger.LogInformation(
            "MailboxContacts directory enrichment for mailbox {Mailbox}: {Candidates} candidates, {Matched} matched from directory, {Unmatched} unmatched, {MatchedUsers} matched to tenant user (will use linked user photo), {UserCount} tenant users scanned",
            mailboxEmail, candidates.Count, matched, unmatched, matchedUsers, allUsers.Count);
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
            !u.HiddenFromGal &&
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
            if (u.HiddenFromGal)
            {
                _logger.LogDebug("Filtered out {Email} ({EntraId}): hidden from GAL", u.Email, u.EntraId);
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
            _logger.LogInformation("Source filter removed {Count} users: {Disabled} disabled, {HiddenGal} hidden from GAL, {MailboxType} non-user mailbox, {NoEmail} no email, {ServiceAcct} service accounts",
                userList.Count - filtered.Count,
                userList.Count(u => !u.IsEnabled),
                userList.Count(u => u.IsEnabled && u.HiddenFromGal),
                userList.Count(u => u.IsEnabled && !u.HiddenFromGal && u.MailboxType != "UserMailbox"),
                userList.Count(u => u.IsEnabled && !u.HiddenFromGal && u.MailboxType == "UserMailbox" && string.IsNullOrWhiteSpace(u.Email)),
                userList.Count(u => u.IsEnabled && !u.HiddenFromGal && u.MailboxType == "UserMailbox" && !string.IsNullOrWhiteSpace(u.Email) && IsServiceAccount(u.Email!)));
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
    /// <summary>
    /// Batched upsert: 25 rows per batch (26 columns × 25 = 650 params, within PG limit).
    /// Reduces DB round trips from N to N/25.
    /// </summary>
    private async Task UpsertSourceUsersAsync(List<SourceUser> users, CancellationToken ct)
    {
        if (users.Count == 0)
            return;

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        const int batchSize = 25;
        const int colCount = 27;
        const string onConflict = """

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
                matched_user_entra_id = EXCLUDED.matched_user_entra_id,
                extension_attr_1 = EXCLUDED.extension_attr_1,
                extension_attr_2 = EXCLUDED.extension_attr_2,
                extension_attr_3 = EXCLUDED.extension_attr_3,
                extension_attr_4 = EXCLUDED.extension_attr_4,
                last_fetched_at = EXCLUDED.last_fetched_at,
                updated_at = EXCLUDED.updated_at
            """;

        for (int offset = 0; offset < users.Count; offset += batchSize)
        {
            var batch = users.Skip(offset).Take(batchSize).ToList();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("""
                INSERT INTO source_users (
                    entra_id, display_name, first_name, last_name, email,
                    business_phone, mobile_phone, job_title, department,
                    office_location, company_name, street_address, city,
                    state, postal_code, country, notes, is_enabled, mailbox_type,
                    matched_user_entra_id,
                    extension_attr_1, extension_attr_2, extension_attr_3, extension_attr_4,
                    last_fetched_at, created_at, updated_at
                ) VALUES
                """);

            var parameters = new List<object?>();
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.AppendLine(",");
                var p = i * colCount;
                sb.Append($"({{{p}}},{{{p+1}}},{{{p+2}}},{{{p+3}}},{{{p+4}}},{{{p+5}}},{{{p+6}}},{{{p+7}}},{{{p+8}}},{{{p+9}}},{{{p+10}}},{{{p+11}}},{{{p+12}}},{{{p+13}}},{{{p+14}}},{{{p+15}}},{{{p+16}}},{{{p+17}}},{{{p+18}}},{{{p+19}}},{{{p+20}}},{{{p+21}}},{{{p+22}}},{{{p+23}}},{{{p+24}}},{{{p+25}}},{{{p+26}}})");

                var user = batch[i];
                parameters.AddRange([
                    Trunc(user.EntraId, 500)!, (object?)Trunc(user.DisplayName, 200), (object?)Trunc(user.FirstName, 100),
                    (object?)Trunc(user.LastName, 100), (object?)Trunc(user.Email, 300),
                    (object?)Trunc(user.BusinessPhone, 50), (object?)Trunc(user.MobilePhone, 50),
                    (object?)Trunc(user.JobTitle, 200), (object?)Trunc(user.Department, 200),
                    (object?)Trunc(user.OfficeLocation, 100), (object?)Trunc(user.CompanyName, 200),
                    (object?)Trunc(user.StreetAddress, 500), (object?)Trunc(user.City, 100),
                    (object?)Trunc(user.State, 100), (object?)Trunc(user.PostalCode, 20),
                    (object?)Trunc(user.Country, 100), (object?)user.Notes, user.IsEnabled, (object?)Trunc(user.MailboxType, 50),
                    (object?)Trunc(user.MatchedUserEntraId, 500),
                    (object?)Trunc(user.ExtensionAttr1, 200), (object?)Trunc(user.ExtensionAttr2, 200),
                    (object?)Trunc(user.ExtensionAttr3, 200), (object?)Trunc(user.ExtensionAttr4, 200),
                    user.LastFetchedAt, user.CreatedAt, user.UpdatedAt
                ]);
            }

            sb.Append(onConflict);
            await db.Database.ExecuteSqlRawAsync(sb.ToString(), parameters.ToArray()!, ct);
        }

        _logger.LogDebug("Upserted {Count} source users to database in {Batches} batch(es)",
            users.Count, (users.Count + batchSize - 1) / batchSize);
    }

    private static string? Trunc(string? value, int maxLength) =>
        value?.Length > maxLength ? value[..maxLength] : value;
}
