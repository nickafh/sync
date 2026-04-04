using AFHSync.Api.Data;
using AFHSync.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Graph.Models;

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
    /// Resolves source members for the given tunnel: queries Graph, maps to SourceUser entities,
    /// applies post-query filtering, upserts to the database, and returns the filtered list.
    /// </summary>
    public async Task<List<SourceUser>> ResolveAsync(Tunnel tunnel, CancellationToken ct)
    {
        _logger.LogInformation("Resolving source members for tunnel {TunnelId} ({TunnelName})",
            tunnel.Id, tunnel.Name);

        var graphUsers = await FetchGraphUsersAsync(tunnel.SourceIdentifier, ct);

        _logger.LogDebug("Fetched {Count} users from Graph for tunnel {TunnelId}",
            graphUsers.Count, tunnel.Id);

        var sourceUsers = graphUsers
            .Select(MapGraphUserToSourceUser)
            .ToList();

        var filtered = ApplySourceFilters(sourceUsers);

        _logger.LogInformation(
            "Tunnel {TunnelId}: {TotalCount} Graph users -> {FilteredCount} after source filtering",
            tunnel.Id, sourceUsers.Count, filtered.Count);

        await UpsertSourceUsersAsync(filtered, ct);

        return filtered;
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
                "id", "displayName", "givenName", "surname", "mail",
                "businessPhones", "mobilePhone", "jobTitle", "department",
                "officeLocation", "companyName", "streetAddress", "city",
                "state", "postalCode", "country", "accountEnabled", "userType",
                "onPremisesExtensionAttributes"
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
    /// Maps a Graph User object to a SourceUser entity.
    /// Maps onPremisesExtensionAttributes to ExtensionAttr1-4.
    /// Sets MailboxType based on userType: "Member" -> "UserMailbox".
    /// </summary>
    public static SourceUser MapGraphUserToSourceUser(User graphUser)
    {
        var mailboxType = graphUser.UserType == "Member" ? "UserMailbox" : graphUser.UserType;

        return new SourceUser
        {
            EntraId = graphUser.Id ?? string.Empty,
            DisplayName = graphUser.DisplayName,
            FirstName = graphUser.GivenName,
            LastName = graphUser.Surname,
            Email = graphUser.Mail,
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
            IsEnabled = graphUser.AccountEnabled ?? false,
            MailboxType = mailboxType,
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
    public static List<SourceUser> ApplySourceFilters(IEnumerable<SourceUser> users)
    {
        return users.Where(u =>
            u.IsEnabled &&
            u.MailboxType == "UserMailbox" &&
            !string.IsNullOrWhiteSpace(u.Email) &&
            !IsServiceAccount(u.Email!)
        ).ToList();
    }

    private static bool IsServiceAccount(string email)
    {
        var localPart = email.Split('@')[0];
        return ServiceAccountPrefixes.Any(prefix =>
            localPart.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Upserts resolved SourceUser records to the database using raw SQL
    /// INSERT ... ON CONFLICT (entra_id) DO UPDATE for performance.
    /// Per D-03 and Pattern 8 in RESEARCH.md.
    /// </summary>
    private async Task UpsertSourceUsersAsync(List<SourceUser> users, CancellationToken ct)
    {
        if (users.Count == 0)
            return;

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        foreach (var user in users)
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO source_users (
                    entra_id, display_name, first_name, last_name, email,
                    business_phone, mobile_phone, job_title, department,
                    office_location, company_name, street_address, city,
                    state, postal_code, country, is_enabled, mailbox_type,
                    extension_attr1, extension_attr2, extension_attr3, extension_attr4,
                    last_fetched_at, created_at, updated_at
                )
                VALUES (
                    {0}, {1}, {2}, {3}, {4},
                    {5}, {6}, {7}, {8},
                    {9}, {10}, {11}, {12},
                    {13}, {14}, {15}, {16}, {17},
                    {18}, {19}, {20}, {21},
                    {22}, {23}, {24}
                )
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
                    is_enabled = EXCLUDED.is_enabled,
                    mailbox_type = EXCLUDED.mailbox_type,
                    extension_attr1 = EXCLUDED.extension_attr1,
                    extension_attr2 = EXCLUDED.extension_attr2,
                    extension_attr3 = EXCLUDED.extension_attr3,
                    extension_attr4 = EXCLUDED.extension_attr4,
                    last_fetched_at = EXCLUDED.last_fetched_at,
                    updated_at = EXCLUDED.updated_at
                """,
                user.EntraId, user.DisplayName, user.FirstName, user.LastName, user.Email,
                user.BusinessPhone, user.MobilePhone, user.JobTitle, user.Department,
                user.OfficeLocation, user.CompanyName, user.StreetAddress, user.City,
                user.State, user.PostalCode, user.Country, user.IsEnabled, user.MailboxType,
                user.ExtensionAttr1, user.ExtensionAttr2, user.ExtensionAttr3, user.ExtensionAttr4,
                user.LastFetchedAt, user.CreatedAt, user.UpdatedAt);
        }

        _logger.LogDebug("Upserted {Count} source users to database", users.Count);
    }
}
