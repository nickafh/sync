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
}
