using AFHSync.Worker.Services;
using Microsoft.Graph.Models;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Tests for ContactWriter payload mapping logic.
/// ContactWriter.MapPayloadToContact is public static so it can be tested without
/// Graph SDK mocking — tests verify field mapping correctness in isolation.
/// </summary>
public class ContactWriterTests
{
    // ── Test 1: Core scalar fields mapped correctly ──────────────────────────

    [Fact]
    public void MapPayloadToContact_MapsGivenName_Surname_DisplayName_Correctly()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["GivenName"] = "John",
            ["Surname"] = "Smith",
            ["DisplayName"] = "John Smith"
        };

        var contact = ContactWriter.MapPayloadToContact(payload);

        Assert.Equal("John", contact.GivenName);
        Assert.Equal("Smith", contact.Surname);
        Assert.Equal("John Smith", contact.DisplayName);
    }

    // ── Test 2: EmailAddresses mapped to List<EmailAddress> ─────────────────

    [Fact]
    public void MapPayloadToContact_Maps_EmailAddresses_To_EmailAddressList()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Jane Doe",
            ["EmailAddresses"] = "jane.doe@atlantafinehomes.com"
        };

        var contact = ContactWriter.MapPayloadToContact(payload);

        Assert.NotNull(contact.EmailAddresses);
        Assert.Single(contact.EmailAddresses);
        Assert.Equal("jane.doe@atlantafinehomes.com", contact.EmailAddresses[0].Address);
    }

    // ── Test 3: BusinessPhones mapped to List<string> ────────────────────────

    [Fact]
    public void MapPayloadToContact_Maps_BusinessPhones_To_StringList()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Bob Brown",
            ["BusinessPhones"] = "+1 404-555-1234"
        };

        var contact = ContactWriter.MapPayloadToContact(payload);

        Assert.NotNull(contact.BusinessPhones);
        Assert.Single(contact.BusinessPhones);
        Assert.Equal("+1 404-555-1234", contact.BusinessPhones[0]);
    }

    // ── Test 4: Business address fields mapped to PhysicalAddress ─────────────

    [Fact]
    public void MapPayloadToContact_Maps_BusinessAddress_Fields_To_PhysicalAddress()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Alice Green",
            ["BusinessStreet"] = "3290 Northside Parkway NW",
            ["BusinessCity"] = "Atlanta",
            ["BusinessState"] = "GA",
            ["BusinessPostalCode"] = "30327"
        };

        var contact = ContactWriter.MapPayloadToContact(payload);

        Assert.NotNull(contact.BusinessAddress);
        Assert.Equal("3290 Northside Parkway NW", contact.BusinessAddress.Street);
        Assert.Equal("Atlanta", contact.BusinessAddress.City);
        Assert.Equal("GA", contact.BusinessAddress.State);
        Assert.Equal("30327", contact.BusinessAddress.PostalCode);
    }

    // ── Test 5: Optional fields mapped when present ──────────────────────────

    [Fact]
    public void MapPayloadToContact_Maps_OptionalFields_When_Present()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Carol White",
            ["JobTitle"] = "Advisor",
            ["CompanyName"] = "Atlanta Fine Homes",
            ["Department"] = "Buckhead",
            ["OfficeLocation"] = "Buckhead",
            ["MobilePhone"] = "+1 404-555-9999",
            ["PersonalNotes"] = "Test notes"
        };

        var contact = ContactWriter.MapPayloadToContact(payload);

        Assert.Equal("Advisor", contact.JobTitle);
        Assert.Equal("Atlanta Fine Homes", contact.CompanyName);
        Assert.Equal("Buckhead", contact.Department);
        Assert.Equal("Buckhead", contact.OfficeLocation);
        Assert.Equal("+1 404-555-9999", contact.MobilePhone);
        Assert.Equal("Test notes", contact.PersonalNotes);
    }

    // ── Test 6: Missing fields do not set properties (no exceptions) ──────────

    [Fact]
    public void MapPayloadToContact_Empty_Payload_Returns_Valid_Contact_With_No_Fields()
    {
        var payload = new SortedDictionary<string, string>();

        var contact = ContactWriter.MapPayloadToContact(payload);

        // Must not throw — returns a valid (mostly-empty) Contact object
        Assert.NotNull(contact);
        Assert.Null(contact.GivenName);
        Assert.Null(contact.BusinessAddress);
        Assert.Null(contact.EmailAddresses);
        Assert.Null(contact.BusinessPhones);
    }
}
