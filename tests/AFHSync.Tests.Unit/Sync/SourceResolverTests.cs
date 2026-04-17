using AFHSync.Shared.Entities;
using AFHSync.Worker.Services;
using Microsoft.Graph.Models;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Unit tests for SourceResolver filtering and mapping logic.
/// Tests use SourceResolver's public static methods directly to avoid mocking GraphServiceClient.
/// Per plan: test ApplySourceFilters (6 tests) and MapGraphUserToSourceUser (1 test).
/// </summary>
public class SourceResolverTests
{
    // ==============================
    // ApplySourceFilters Tests
    // ==============================

    [Fact]
    public void ApplySourceFilters_ExcludesDisabledUsers()
    {
        var users = new List<SourceUser>
        {
            CreateValidUser(entraId: "1", isEnabled: false),
            CreateValidUser(entraId: "2", isEnabled: true),
        };

        var result = SourceResolver.ApplySourceFilters(users);

        Assert.Single(result);
        Assert.Equal("2", result[0].EntraId);
    }

    [Theory]
    [InlineData("SharedMailbox")]
    [InlineData("RoomMailbox")]
    [InlineData("EquipmentMailbox")]
    public void ApplySourceFilters_ExcludesNonUserMailboxTypes(string mailboxType)
    {
        var users = new List<SourceUser>
        {
            CreateValidUser(entraId: "1", mailboxType: mailboxType),
            CreateValidUser(entraId: "2", mailboxType: "UserMailbox"),
        };

        var result = SourceResolver.ApplySourceFilters(users);

        Assert.Single(result);
        Assert.Equal("2", result[0].EntraId);
    }

    [Theory]
    [InlineData("svc_exchange@company.com")]
    [InlineData("service.health@company.com")]
    [InlineData("admin_tool@company.com")]
    [InlineData("noreply@company.com")]
    [InlineData("SVC_Exchange@company.com")]      // case-insensitive
    [InlineData("SERVICE.health@company.com")]    // case-insensitive
    [InlineData("ADMIN_tool@company.com")]         // case-insensitive
    [InlineData("NOREPLY@company.com")]            // case-insensitive
    public void ApplySourceFilters_ExcludesServiceAccounts(string email)
    {
        var users = new List<SourceUser>
        {
            CreateValidUser(entraId: "1", email: email),
            CreateValidUser(entraId: "2", email: "john.doe@company.com"),
        };

        var result = SourceResolver.ApplySourceFilters(users);

        Assert.Single(result);
        Assert.Equal("2", result[0].EntraId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ApplySourceFilters_ExcludesNullOrEmptyEmail(string? email)
    {
        var users = new List<SourceUser>
        {
            CreateValidUser(entraId: "1", email: email),
            CreateValidUser(entraId: "2", email: "valid@company.com"),
        };

        var result = SourceResolver.ApplySourceFilters(users);

        Assert.Single(result);
        Assert.Equal("2", result[0].EntraId);
    }

    [Fact]
    public void ApplySourceFilters_RetainsValidUsers()
    {
        var users = new List<SourceUser>
        {
            CreateValidUser(entraId: "1"),
            CreateValidUser(entraId: "2"),
            CreateValidUser(entraId: "3"),
        };

        var result = SourceResolver.ApplySourceFilters(users);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ApplySourceFilters_FiltersOutMultipleExclusionTypes()
    {
        var users = new List<SourceUser>
        {
            CreateValidUser(entraId: "valid", email: "user@company.com"),
            CreateValidUser(entraId: "disabled", isEnabled: false),
            CreateValidUser(entraId: "shared", mailboxType: "SharedMailbox"),
            CreateValidUser(entraId: "service", email: "svc_test@company.com"),
            CreateValidUser(entraId: "noemail", email: null),
        };

        var result = SourceResolver.ApplySourceFilters(users);

        Assert.Single(result);
        Assert.Equal("valid", result[0].EntraId);
    }

    // ==============================
    // MapGraphUserToSourceUser Tests
    // ==============================

    [Fact]
    public void MapGraphUserToSourceUser_MapsAllFieldsCorrectly()
    {
        var graphUser = new User
        {
            Id = "entra-id-123",
            DisplayName = "John Doe",
            GivenName = "John",
            Surname = "Doe",
            Mail = "john.doe@company.com",
            BusinessPhones = ["555-1234"],
            MobilePhone = "555-5678",
            JobTitle = "Agent",
            Department = "Sales",
            OfficeLocation = "Buckhead",
            CompanyName = "Atlanta Fine Homes",
            StreetAddress = "3520 Piedmont Rd",
            City = "Atlanta",
            State = "GA",
            PostalCode = "30305",
            Country = "USA",
            AccountEnabled = true,
            UserType = "Member",
            OnPremisesExtensionAttributes = new OnPremisesExtensionAttributes
            {
                ExtensionAttribute1 = "attr1-value",
                ExtensionAttribute2 = "AFH",
                ExtensionAttribute3 = "Advisor",
                ExtensionAttribute4 = "Residential"
            }
        };

        var result = SourceResolver.MapGraphUserToSourceUser(graphUser);

        Assert.Equal("entra-id-123", result.EntraId);
        Assert.Equal("John Doe", result.DisplayName);
        Assert.Equal("John", result.FirstName);
        Assert.Equal("Doe", result.LastName);
        Assert.Equal("john.doe@company.com", result.Email);
        Assert.Equal("555-1234", result.BusinessPhone);
        Assert.Equal("555-5678", result.MobilePhone);
        Assert.Equal("Agent", result.JobTitle);
        Assert.Equal("Sales", result.Department);
        Assert.Equal("Buckhead", result.OfficeLocation);
        Assert.Equal("Atlanta Fine Homes", result.CompanyName);
        Assert.Equal("3520 Piedmont Rd", result.StreetAddress);
        Assert.Equal("Atlanta", result.City);
        Assert.Equal("GA", result.State);
        Assert.Equal("30305", result.PostalCode);
        Assert.Equal("USA", result.Country);
        Assert.True(result.IsEnabled);
        Assert.Equal("UserMailbox", result.MailboxType);
        Assert.Equal("attr1-value", result.ExtensionAttr1);
        Assert.Equal("AFH", result.ExtensionAttr2);
        Assert.Equal("Advisor", result.ExtensionAttr3);
        Assert.Equal("Residential", result.ExtensionAttr4);
    }

    [Fact]
    public void MapGraphUserToSourceUser_SetsMailboxType_UserMailbox_ForMemberUserType()
    {
        var graphUser = new User
        {
            Id = "id-1",
            UserType = "Member",
            AccountEnabled = true
        };

        var result = SourceResolver.MapGraphUserToSourceUser(graphUser);

        Assert.Equal("UserMailbox", result.MailboxType);
    }

    [Fact]
    public void MapGraphUserToSourceUser_HandlesNullOnPremisesExtensionAttributes()
    {
        var graphUser = new User
        {
            Id = "id-2",
            UserType = "Member",
            OnPremisesExtensionAttributes = null
        };

        var result = SourceResolver.MapGraphUserToSourceUser(graphUser);

        Assert.Null(result.ExtensionAttr1);
        Assert.Null(result.ExtensionAttr2);
        Assert.Null(result.ExtensionAttr3);
        Assert.Null(result.ExtensionAttr4);
    }

    [Fact]
    public void MapGraphUserToSourceUser_FirstBusinessPhone_UsedWhenMultiple()
    {
        var graphUser = new User
        {
            Id = "id-3",
            UserType = "Member",
            BusinessPhones = ["555-1111", "555-2222", "555-3333"]
        };

        var result = SourceResolver.MapGraphUserToSourceUser(graphUser);

        Assert.Equal("555-1111", result.BusinessPhone);
    }

    // ==============================
    // Directory Enrichment Tests
    // ==============================

    [Fact]
    public void BuildEnrichmentCandidates_IncludesContactsWithEmptyBothPhones_AndNonEmptyEmail()
    {
        var contacts = new List<SourceUser>
        {
            CreateContact(entraId: "a", email: "a@x.com", bp: null,       mp: null),
            CreateContact(entraId: "b", email: "b@x.com", bp: "555-0001", mp: null),
            CreateContact(entraId: "c", email: "c@x.com", bp: null,       mp: "555-0002"),
            CreateContact(entraId: "d", email: null,      bp: null,       mp: null),
            CreateContact(entraId: "e", email: "e@x.com", bp: "   ",      mp: " "),
        };

        var result = SourceResolver.BuildEnrichmentCandidates(contacts);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, c => c.EntraId == "a");
        Assert.Contains(result, c => c.EntraId == "e");
    }

    [Fact]
    public void BuildDirectoryPhoneMap_IndexesByMailAndProxyAddresses_CaseInsensitive()
    {
        var users = new List<User>
        {
            new()
            {
                Id = "u1",
                Mail = "A@X.com",
                ProxyAddresses = new List<string> { "SMTP:A@X.com", "smtp:alias@x.com" },
                BusinessPhones = new List<string> { "555-1111" },
                MobilePhone = "555-2222"
            }
        };

        var map = SourceResolver.BuildDirectoryPhoneMap(users);

        Assert.True(map.ContainsKey("a@x.com"));
        Assert.True(map.ContainsKey("alias@x.com"));
        // Case-insensitive lookup:
        Assert.True(map.ContainsKey("A@X.COM"));
        Assert.Equal("555-1111", map["a@x.com"].BusinessPhone);
        Assert.Equal("555-2222", map["alias@x.com"].MobilePhone);
    }

    [Fact]
    public void BuildDirectoryPhoneMap_StripsSmtpPrefix_AndHandlesMissingMailOrPhones()
    {
        var users = new List<User>
        {
            new()
            {
                Id = "u2",
                Mail = null,
                ProxyAddresses = new List<string> { "smtp:only@x.com" },
                BusinessPhones = null,
                MobilePhone = null
            }
        };

        var map = SourceResolver.BuildDirectoryPhoneMap(users);

        Assert.Single(map);
        Assert.True(map.ContainsKey("only@x.com"));
        Assert.Null(map["only@x.com"].BusinessPhone);
        Assert.Null(map["only@x.com"].MobilePhone);
    }

    [Fact]
    public void ApplyDirectoryEnrichment_BackfillsEmptyOnly_NeverOverwritesExisting()
    {
        var candidates = new List<SourceUser>
        {
            CreateContact(entraId: "a", email: "a@x.com", bp: null,       mp: " "),
            CreateContact(entraId: "b", email: "b@x.com", bp: "555-0001", mp: null),
            CreateContact(entraId: "c", email: "c@x.com", bp: "555-0003", mp: "555-0004"),
        };
        var map = new Dictionary<string, (string? BusinessPhone, string? MobilePhone)>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["a@x.com"] = ("AAA", "BBB"),
            ["b@x.com"] = ("CCC", "DDD"),
            ["c@x.com"] = ("EEE", "FFF"),
        };

        var (matched, unmatched) = SourceResolver.ApplyDirectoryEnrichment(candidates, map);

        Assert.Equal(3, matched);
        Assert.Equal(0, unmatched);
        Assert.Equal("AAA", candidates[0].BusinessPhone);
        Assert.Equal("BBB", candidates[0].MobilePhone);
        Assert.Equal("555-0001", candidates[1].BusinessPhone);   // preserved
        Assert.Equal("DDD",      candidates[1].MobilePhone);     // filled
        Assert.Equal("555-0003", candidates[2].BusinessPhone);   // preserved
        Assert.Equal("555-0004", candidates[2].MobilePhone);     // preserved
    }

    [Fact]
    public void ApplyDirectoryEnrichment_CountsUnmatchedWhenEmailNotInMap()
    {
        var candidates = new List<SourceUser>
        {
            CreateContact(entraId: "a", email: "a@x.com", bp: null, mp: null),
            CreateContact(entraId: "b", email: "unknown@x.com", bp: null, mp: null),
        };
        var map = new Dictionary<string, (string? BusinessPhone, string? MobilePhone)>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["a@x.com"] = ("AAA", "BBB"),
        };

        var (matched, unmatched) = SourceResolver.ApplyDirectoryEnrichment(candidates, map);

        Assert.Equal(1, matched);
        Assert.Equal(1, unmatched);
        Assert.Equal("AAA", candidates[0].BusinessPhone);
        Assert.Null(candidates[1].BusinessPhone);
        Assert.Null(candidates[1].MobilePhone);
    }

    [Fact]
    public void ChunkEmailsForFilter_StaysUnderOrClauseLimit()
    {
        // Each email produces TWO OR terms (smtp: + SMTP:), so a chunk size budget
        // of 14 OR clauses means at most 7 emails per chunk.
        var emails = Enumerable.Range(0, 50).Select(i => $"user{i:D2}@example.com").ToList();

        var chunks = SourceResolver.ChunkEmailsForFilter(emails, maxOrClauses: 14);

        Assert.NotEmpty(chunks);
        // Reassembly preserves all emails exactly once, in order.
        var flattened = chunks.SelectMany(c => c).ToList();
        Assert.Equal(emails.Count, flattened.Count);
        Assert.Equal(emails, flattened);

        // Each chunk emits ≤ maxOrClauses OR terms when each email yields 2 terms.
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Count * 2 <= 14,
                $"Chunk holds {chunk.Count} emails (=> {chunk.Count * 2} OR clauses), exceeds limit of 14.");
        }
    }

    [Fact]
    public void ChunkEmailsForFilter_SingleEmailAlwaysEmitted()
    {
        var emails = new List<string> { "solo@x.com" };

        var chunks = SourceResolver.ChunkEmailsForFilter(emails, maxOrClauses: 1);

        Assert.Single(chunks);
        Assert.Single(chunks[0]);
        Assert.Equal("solo@x.com", chunks[0][0]);
    }

    // ==============================
    // MatchedUserEntraId capture (260417-1lx photo enrichment)
    // ==============================

    [Fact]
    public void BuildDirectoryUserIdMap_IndexesByMailAndProxyAddresses_CaseInsensitive()
    {
        var users = new List<User>
        {
            new()
            {
                Id = "user-guid-1",
                Mail = "Immy@AtlantaFineHomes.com",
                ProxyAddresses = new List<string>
                {
                    "SMTP:Immy@AtlantaFineHomes.com",
                    "smtp:immy-alias@atlantafinehomes.com"
                }
            },
            new()
            {
                Id = "user-guid-2",
                Mail = null,
                ProxyAddresses = new List<string> { "smtp:otto@atlantafinehomes.com" }
            },
            new()
            {
                // No Id — should be skipped entirely.
                Id = null,
                Mail = "ghost@x.com",
                ProxyAddresses = new List<string> { "smtp:ghost@x.com" }
            }
        };

        var map = SourceResolver.BuildDirectoryUserIdMap(users);

        Assert.Equal("user-guid-1", map["immy@atlantafinehomes.com"]);
        Assert.Equal("user-guid-1", map["IMMY@ATLANTAFINEHOMES.COM"]); // case-insensitive
        Assert.Equal("user-guid-1", map["immy-alias@atlantafinehomes.com"]);
        Assert.Equal("user-guid-2", map["otto@atlantafinehomes.com"]);
        Assert.False(map.ContainsKey("ghost@x.com"));
    }

    [Fact]
    public void ApplyDirectoryEnrichment_WritesMatchedUserEntraId_WhenUserIdMapHasMatch()
    {
        var candidates = new List<SourceUser>
        {
            CreateContact(entraId: "contact-immy", email: "immy@atlantafinehomes.com", bp: null, mp: null),
            CreateContact(entraId: "contact-no-user", email: "stub-no-link@x.com", bp: null, mp: null),
        };
        var phoneMap = new Dictionary<string, (string? BusinessPhone, string? MobilePhone)>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["immy@atlantafinehomes.com"]  = ("555-1111", "555-2222"),
            ["stub-no-link@x.com"]         = (null, null),
        };
        var userIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["immy@atlantafinehomes.com"] = "user-guid-immy",
            // stub-no-link is in phoneMap but NOT in userIdMap (e.g. a directory contact without a User record).
        };

        var (matched, unmatched) = SourceResolver.ApplyDirectoryEnrichment(candidates, phoneMap, userIdMap);

        Assert.Equal(2, matched);
        Assert.Equal(0, unmatched);
        Assert.Equal("user-guid-immy", candidates[0].MatchedUserEntraId);
        Assert.Null(candidates[1].MatchedUserEntraId); // matched in phoneMap but not in userIdMap -> stays null
    }

    [Fact]
    public void ApplyDirectoryEnrichment_LeavesMatchedUserEntraIdNull_WhenUserIdMapIsNull()
    {
        // Backwards-compat: 2-arg overload (and 3-arg with null userIdMap) must not touch MatchedUserEntraId.
        var candidates = new List<SourceUser>
        {
            CreateContact(entraId: "a", email: "a@x.com", bp: null, mp: null),
        };
        var phoneMap = new Dictionary<string, (string? BusinessPhone, string? MobilePhone)>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["a@x.com"] = ("AAA", "BBB"),
        };

        var (matched, _) = SourceResolver.ApplyDirectoryEnrichment(candidates, phoneMap);

        Assert.Equal(1, matched);
        Assert.Equal("AAA", candidates[0].BusinessPhone);
        Assert.Null(candidates[0].MatchedUserEntraId);
    }

    [Fact]
    public void ApplyDirectoryEnrichment_DoesNotOverwriteExistingMatchedUserEntraId_WhenEmailUnmatched()
    {
        // Pre-existing MatchedUserEntraId on a candidate that doesn't match this run -> preserved.
        var candidates = new List<SourceUser>
        {
            CreateContact(entraId: "x", email: "unknown@x.com", bp: null, mp: null),
        };
        candidates[0].MatchedUserEntraId = "previously-matched-guid";

        var phoneMap = new Dictionary<string, (string? BusinessPhone, string? MobilePhone)>(
            StringComparer.OrdinalIgnoreCase);
        var userIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var (matched, unmatched) = SourceResolver.ApplyDirectoryEnrichment(candidates, phoneMap, userIdMap);

        Assert.Equal(0, matched);
        Assert.Equal(1, unmatched);
        Assert.Equal("previously-matched-guid", candidates[0].MatchedUserEntraId);
    }

    // ==============================
    // BuildMailboxContactDedupKey Tests
    // ==============================

    [Fact]
    public void BuildMailboxContactDedupKey_SameNameAndEmail_ProducesEqualKeys_CaseAndWhitespaceInsensitive()
    {
        var a = new SourceUser
        {
            EntraId = "folder-A-id",
            DisplayName = "Jane Doe",
            Email = "jane@x.com",
            MailboxType = "MailboxContact",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var b = new SourceUser
        {
            EntraId = "folder-B-id",
            DisplayName = "  jane doe ",
            Email = "JANE@x.com",
            MailboxType = "MailboxContact",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var keyA = SourceResolver.BuildMailboxContactDedupKey(a);
        var keyB = SourceResolver.BuildMailboxContactDedupKey(b);

        Assert.NotNull(keyA);
        Assert.Equal(keyA, keyB, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMailboxContactDedupKey_SameEmailDifferentName_ProducesDifferentKeys()
    {
        var a = new SourceUser
        {
            EntraId = "id-a",
            DisplayName = "Jane Doe",
            Email = "jane@x.com",
            MailboxType = "MailboxContact",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var b = new SourceUser
        {
            EntraId = "id-b",
            DisplayName = "J. Doe",
            Email = "jane@x.com",
            MailboxType = "MailboxContact",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var keyA = SourceResolver.BuildMailboxContactDedupKey(a);
        var keyB = SourceResolver.BuildMailboxContactDedupKey(b);

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void BuildMailboxContactDedupKey_FallsBackToEntraId_WhenNameAndEmailBothEmpty()
    {
        var user = new SourceUser
        {
            EntraId = "abc-123",
            DisplayName = null,
            Email = null,
            MailboxType = "MailboxContact",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var key = SourceResolver.BuildMailboxContactDedupKey(user);

        Assert.Equal("id:abc-123", key);
    }

    [Fact]
    public void BuildMailboxContactDedupKey_ReturnsNull_WhenNameEmailAndEntraIdAllEmpty()
    {
        var user = new SourceUser
        {
            EntraId = "",
            DisplayName = "  ",
            Email = "  ",
            MailboxType = "MailboxContact",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var key = SourceResolver.BuildMailboxContactDedupKey(user);

        Assert.Null(key);
    }

    [Fact]
    public void BuildMailboxContactDedupKey_NameOnlyOrEmailOnly_FallsBackToEntraId()
    {
        var nameOnly = new SourceUser
        {
            EntraId = "xyz",
            DisplayName = "Jane",
            Email = "",
            MailboxType = "MailboxContact",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var emailOnly = new SourceUser
        {
            EntraId = "pqr",
            DisplayName = null,
            Email = "jane@x.com",
            MailboxType = "MailboxContact",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        Assert.Equal("id:xyz", SourceResolver.BuildMailboxContactDedupKey(nameOnly));
        Assert.Equal("id:pqr", SourceResolver.BuildMailboxContactDedupKey(emailOnly));
    }

    [Fact]
    public void BuildMailboxContactDedupKey_GroupBy_DedupsAcrossFoldersByNameAndEmail()
    {
        // Two MailboxContact SourceUsers with matching name+email but different EntraIds
        // (the per-folder Graph Contact.Id) — GroupBy on the dedup key should collapse
        // them to a single entry.
        var users = new List<SourceUser>
        {
            new()
            {
                EntraId = "agents-folder-contact-id",
                DisplayName = "Jane Doe",
                Email = "jane@x.com",
                MailboxType = "MailboxContact",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new()
            {
                EntraId = "brokers-folder-contact-id",
                DisplayName = "JANE DOE",
                Email = "jane@x.com",
                MailboxType = "MailboxContact",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new()
            {
                EntraId = "other-person-contact-id",
                DisplayName = "Alex Q",
                Email = "alex@x.com",
                MailboxType = "MailboxContact",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
        };

        var deduped = users
            .Select(u => (User: u, Key: SourceResolver.BuildMailboxContactDedupKey(u)))
            .Where(t => t.Key != null)
            .GroupBy(t => t.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First().User)
            .ToList();

        Assert.Equal(2, deduped.Count);
        Assert.Contains(deduped, u => u.Email == "jane@x.com");
        Assert.Contains(deduped, u => u.Email == "alex@x.com");
    }

    // ==============================
    // Helper Methods
    // ==============================

    private static SourceUser CreateValidUser(
        string entraId = "default-id",
        bool isEnabled = true,
        string mailboxType = "UserMailbox",
        string? email = "user@company.com")
    {
        return new SourceUser
        {
            EntraId = entraId,
            IsEnabled = isEnabled,
            MailboxType = mailboxType,
            Email = email,
            DisplayName = "Test User",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static SourceUser CreateContact(
        string entraId,
        string? email,
        string? bp,
        string? mp) => new SourceUser
        {
            EntraId = entraId,
            Email = email,
            BusinessPhone = bp,
            MobilePhone = mp,
            IsEnabled = true,
            MailboxType = "MailboxContact",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
}
