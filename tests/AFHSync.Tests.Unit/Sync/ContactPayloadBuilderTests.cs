using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;
using AFHSync.Worker.Services;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Unit tests for ContactPayloadBuilder covering all 4 SyncBehavior modes,
/// hash determinism, null handling, whitespace trimming, and field ordering.
/// </summary>
public class ContactPayloadBuilderTests
{
    private readonly ContactPayloadBuilder _builder = new();

    // ==============================
    // SyncBehavior.Always Tests
    // ==============================

    [Fact]
    public void BuildPayload_Always_IncludesNonNullFields()
    {
        var source = CreateSourceUser(displayName: "John Doe", jobTitle: "Agent");
        var fields = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.Always),
        };

        var result = _builder.BuildPayload(source, fields, existingState: null);

        Assert.Equal("John Doe", result.Payload["DisplayName"]);
        Assert.Equal("Agent", result.Payload["JobTitle"]);
        Assert.Equal(2, result.Payload.Count);
    }

    [Fact]
    public void BuildPayload_Always_ExcludesNullFields()
    {
        var source = CreateSourceUser(displayName: "John Doe", jobTitle: null);
        var fields = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.Always),
        };

        var result = _builder.BuildPayload(source, fields, existingState: null);

        Assert.True(result.Payload.ContainsKey("DisplayName"));
        Assert.False(result.Payload.ContainsKey("JobTitle"));
    }

    // ==============================
    // SyncBehavior.Nosync Tests
    // ==============================

    [Fact]
    public void BuildPayload_Nosync_ExcludesFieldFromPayloadAndHash()
    {
        var source = CreateSourceUser(displayName: "John Doe", jobTitle: "Agent");
        var fields = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.Nosync),
        };

        var result = _builder.BuildPayload(source, fields, existingState: null);

        Assert.True(result.Payload.ContainsKey("DisplayName"));
        Assert.False(result.Payload.ContainsKey("JobTitle"),
            "Nosync fields must not appear in payload");
    }

    [Fact]
    public void BuildPayload_Nosync_AffectsHash_WhenFieldIsSuppressed()
    {
        var source = CreateSourceUser(displayName: "John Doe", jobTitle: "Agent");

        var alwaysFields = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.Always),
        };
        var nosyncFields = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.Nosync),
        };

        var resultWith = _builder.BuildPayload(source, alwaysFields, existingState: null);
        var resultWithout = _builder.BuildPayload(source, nosyncFields, existingState: null);

        Assert.NotEqual(resultWith.DataHash, resultWithout.DataHash);
    }

    // ==============================
    // SyncBehavior.AddMissing Tests
    // ==============================

    [Fact]
    public void BuildPayload_AddMissing_IncludesField_WhenExistingStateIsNull()
    {
        var source = CreateSourceUser(jobTitle: "Agent");
        var fields = new List<FieldProfileField>
        {
            CreateField("JobTitle", SyncBehavior.AddMissing),
        };

        var result = _builder.BuildPayload(source, fields, existingState: null);

        Assert.True(result.Payload.ContainsKey("JobTitle"),
            "AddMissing should include field when existingState is null (new contact)");
        Assert.Equal("Agent", result.Payload["JobTitle"]);
    }

    [Fact]
    public void BuildPayload_AddMissing_ExcludesField_WhenExistingStateExists()
    {
        var source = CreateSourceUser(jobTitle: "Director");
        var fields = new List<FieldProfileField>
        {
            CreateField("JobTitle", SyncBehavior.AddMissing),
        };
        var existingState = new ContactSyncState { Id = 1 };

        var result = _builder.BuildPayload(source, fields, existingState);

        Assert.False(result.Payload.ContainsKey("JobTitle"),
            "AddMissing should not overwrite existing value — contact already has this field");
    }

    // ==============================
    // SyncBehavior.RemoveBlank Tests
    // ==============================

    [Fact]
    public void BuildPayload_RemoveBlank_IncludesEmptyString_WhenSourceFieldIsBlank()
    {
        var source = CreateSourceUser(jobTitle: null);
        var fields = new List<FieldProfileField>
        {
            CreateField("JobTitle", SyncBehavior.RemoveBlank),
        };

        var result = _builder.BuildPayload(source, fields, existingState: null);

        Assert.True(result.Payload.ContainsKey("JobTitle"),
            "RemoveBlank should include empty string to signal clearing the target field");
        Assert.Equal(string.Empty, result.Payload["JobTitle"]);
    }

    [Fact]
    public void BuildPayload_RemoveBlank_IncludesTrimmedValue_WhenSourceFieldHasValue()
    {
        var source = CreateSourceUser(jobTitle: "  Senior Agent  ");
        var fields = new List<FieldProfileField>
        {
            CreateField("JobTitle", SyncBehavior.RemoveBlank),
        };

        var result = _builder.BuildPayload(source, fields, existingState: null);

        Assert.Equal("Senior Agent", result.Payload["JobTitle"]);
    }

    // ==============================
    // Hash Determinism Tests
    // ==============================

    [Fact]
    public void Hash_IsDeterministic_ForIdenticalInputs()
    {
        var source = CreateSourceUser(displayName: "Jane Smith", jobTitle: "Advisor");
        var fields = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.Always),
        };

        var result1 = _builder.BuildPayload(source, fields, existingState: null);
        var result2 = _builder.BuildPayload(source, fields, existingState: null);

        Assert.Equal(result1.DataHash, result2.DataHash);
    }

    [Fact]
    public void Hash_Changes_WhenFieldValueChanges()
    {
        var source1 = CreateSourceUser(jobTitle: "Manager");
        var source2 = CreateSourceUser(jobTitle: "Director");
        var fields = new List<FieldProfileField>
        {
            CreateField("JobTitle", SyncBehavior.Always),
        };

        var result1 = _builder.BuildPayload(source1, fields, existingState: null);
        var result2 = _builder.BuildPayload(source2, fields, existingState: null);

        Assert.NotEqual(result1.DataHash, result2.DataHash);
    }

    [Fact]
    public void Hash_IsIdentical_RegardlessOfFieldProfileFieldOrdering()
    {
        // SortedDictionary guarantees alphabetical key ordering in output,
        // so different input orderings must produce the same hash.
        var source = CreateSourceUser(displayName: "John Doe", jobTitle: "Agent");

        var fields1 = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.Always),
        };
        var fields2 = new List<FieldProfileField>
        {
            CreateField("JobTitle", SyncBehavior.Always),  // reversed order
            CreateField("DisplayName", SyncBehavior.Always),
        };

        var result1 = _builder.BuildPayload(source, fields1, existingState: null);
        var result2 = _builder.BuildPayload(source, fields2, existingState: null);

        Assert.Equal(result1.DataHash, result2.DataHash);
    }

    // ==============================
    // Null and Whitespace Handling
    // ==============================

    [Fact]
    public void BuildPayload_NullFields_AreExcludedFromHash()
    {
        var sourceWithNull = CreateSourceUser(displayName: "John Doe", jobTitle: null);
        var sourceWithValue = CreateSourceUser(displayName: "John Doe", jobTitle: "Agent");
        var fields = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.Always),
        };

        var resultWithNull = _builder.BuildPayload(sourceWithNull, fields, existingState: null);
        var resultWithValue = _builder.BuildPayload(sourceWithValue, fields, existingState: null);

        Assert.False(resultWithNull.Payload.ContainsKey("JobTitle"),
            "Null fields should not appear in payload");
        Assert.NotEqual(resultWithNull.DataHash, resultWithValue.DataHash);
    }

    [Fact]
    public void BuildPayload_WhitespaceOnlyFields_AreTrimmedAndTreatedAsEmpty()
    {
        var source = CreateSourceUser(jobTitle: "   ");  // whitespace only
        var fields = new List<FieldProfileField>
        {
            CreateField("JobTitle", SyncBehavior.Always),
        };

        var result = _builder.BuildPayload(source, fields, existingState: null);

        // Whitespace-only treated as empty for Always behavior (excluded)
        Assert.False(result.Payload.ContainsKey("JobTitle"),
            "Whitespace-only fields should be excluded from Always behavior (treated as empty)");
    }

    [Fact]
    public void BuildPayload_ReturnsPayloadAndHash()
    {
        var source = CreateSourceUser(displayName: "Test User");
        var fields = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
        };

        var result = _builder.BuildPayload(source, fields, existingState: null);

        Assert.NotNull(result);
        Assert.NotNull(result.Payload);
        Assert.NotNull(result.DataHash);
        Assert.NotEmpty(result.DataHash);
        // SHA-256 hex string is 64 characters
        Assert.Equal(64, result.DataHash.Length);
        Assert.Matches("^[0-9a-f]{64}$", result.DataHash);
    }

    // ==============================
    // Field Mapping Tests
    // ==============================

    [Fact]
    public void BuildPayload_MapsAllSourceUserFields()
    {
        var source = new SourceUser
        {
            DisplayName = "John Doe",
            FirstName = "John",
            LastName = "Doe",
            JobTitle = "Agent",
            CompanyName = "AFH",
            Email = "john@afh.com",
            BusinessPhone = "555-1234",
            MobilePhone = "555-5678",
            OfficeLocation = "Buckhead",
            Department = "Sales",
            StreetAddress = "123 Main St",
            City = "Atlanta",
            State = "GA",
            PostalCode = "30301",
            Notes = "Test notes",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var fields = new List<FieldProfileField>
        {
            CreateField("DisplayName", SyncBehavior.Always),
            CreateField("GivenName", SyncBehavior.Always),
            CreateField("Surname", SyncBehavior.Always),
            CreateField("JobTitle", SyncBehavior.Always),
            CreateField("CompanyName", SyncBehavior.Always),
            CreateField("EmailAddresses", SyncBehavior.Always),
            CreateField("BusinessPhones", SyncBehavior.Always),
            CreateField("MobilePhone", SyncBehavior.Always),
            CreateField("OfficeLocation", SyncBehavior.Always),
            CreateField("Department", SyncBehavior.Always),
            CreateField("BusinessStreet", SyncBehavior.Always),
            CreateField("BusinessCity", SyncBehavior.Always),
            CreateField("BusinessState", SyncBehavior.Always),
            CreateField("BusinessPostalCode", SyncBehavior.Always),
            CreateField("PersonalNotes", SyncBehavior.Always),
        };

        var result = _builder.BuildPayload(source, fields, existingState: null);

        Assert.Equal("John Doe", result.Payload["DisplayName"]);
        Assert.Equal("John", result.Payload["GivenName"]);
        Assert.Equal("Doe", result.Payload["Surname"]);
        Assert.Equal("Agent", result.Payload["JobTitle"]);
        Assert.Equal("AFH", result.Payload["CompanyName"]);
        Assert.Equal("john@afh.com", result.Payload["EmailAddresses"]);
        Assert.Equal("555-1234", result.Payload["BusinessPhones"]);
        Assert.Equal("555-5678", result.Payload["MobilePhone"]);
        Assert.Equal("Buckhead", result.Payload["OfficeLocation"]);
        Assert.Equal("Sales", result.Payload["Department"]);
        Assert.Equal("123 Main St", result.Payload["BusinessStreet"]);
        Assert.Equal("Atlanta", result.Payload["BusinessCity"]);
        Assert.Equal("GA", result.Payload["BusinessState"]);
        Assert.Equal("30301", result.Payload["BusinessPostalCode"]);
        Assert.Equal("Test notes", result.Payload["PersonalNotes"]);
    }

    // ==============================
    // Helper Methods
    // ==============================

    private static SourceUser CreateSourceUser(
        string? displayName = "Test User",
        string? jobTitle = "Agent",
        string? email = "test@company.com")
    {
        return new SourceUser
        {
            EntraId = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            JobTitle = jobTitle,
            Email = email,
            IsEnabled = true,
            MailboxType = "UserMailbox",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static FieldProfileField CreateField(string fieldName, SyncBehavior behavior)
    {
        return new FieldProfileField
        {
            FieldName = fieldName,
            Behavior = behavior,
            FieldSection = "contact",
            DisplayName = fieldName,
        };
    }
}
