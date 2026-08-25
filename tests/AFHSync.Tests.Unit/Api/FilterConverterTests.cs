using AFHSync.Api.DTOs;
using AFHSync.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Api;

public class FilterConverterTests
{
    private readonly FilterConverter _converter = new(NullLogger<FilterConverter>.Instance);

    // Test 1: Simple office filter
    [Fact]
    public void Convert_SimpleOfficeFilter_ReturnsODataFilter()
    {
        var result = _converter.Convert("(Office -eq 'Buckhead')");

        Assert.True(result.Success);
        Assert.Contains("officeLocation eq 'Buckhead'", result.Filter);
        Assert.Null(result.Warning);
    }

    // Test 2: Compound AND filter
    [Fact]
    public void Convert_CompoundAndFilter_ReturnsODataFilter()
    {
        var result = _converter.Convert("(Office -eq 'Buckhead') -and (CustomAttribute2 -eq 'AFH')");

        Assert.True(result.Success);
        Assert.Contains("officeLocation eq 'Buckhead'", result.Filter);
        Assert.Contains("onPremisesExtensionAttributes/extensionAttribute2 eq 'AFH'", result.Filter);
        Assert.Contains(" and ", result.Filter);
    }

    // Test 3: OR filter
    [Fact]
    public void Convert_OrFilter_ReturnsODataFilter()
    {
        var result = _converter.Convert("(Office -eq 'Buckhead') -or (Office -eq 'Intown')");

        Assert.True(result.Success);
        Assert.Contains("officeLocation eq 'Buckhead'", result.Filter);
        Assert.Contains("officeLocation eq 'Intown'", result.Filter);
        Assert.Contains(" or ", result.Filter);
    }

    // Test 4: Department filter
    [Fact]
    public void Convert_DepartmentFilter_ReturnsODataFilter()
    {
        var result = _converter.Convert("(Department -eq 'Sales')");

        Assert.True(result.Success);
        Assert.Contains("department eq 'Sales'", result.Filter);
    }

    // Test 5: Title filter
    [Fact]
    public void Convert_TitleFilter_ReturnsODataFilter()
    {
        var result = _converter.Convert("(Title -eq 'Agent')");

        Assert.True(result.Success);
        Assert.Contains("jobTitle eq 'Agent'", result.Filter);
    }

    // Test 6: NOT operator (-ne)
    [Fact]
    public void Convert_NeOperator_ReturnsODataFilter()
    {
        var result = _converter.Convert("(Office -ne 'Buckhead')");

        Assert.True(result.Success);
        Assert.Contains("officeLocation ne 'Buckhead'", result.Filter);
    }

    // Test 7: Complex nested filter with multiple attributes
    [Fact]
    public void Convert_ComplexNestedFilter_ReturnsODataFilter()
    {
        var result = _converter.Convert(
            "(Office -eq 'Buckhead') -and (CustomAttribute2 -eq 'AFH') -and (Department -eq 'Sales')");

        Assert.True(result.Success);
        Assert.Contains("officeLocation eq 'Buckhead'", result.Filter);
        Assert.Contains("onPremisesExtensionAttributes/extensionAttribute2 eq 'AFH'", result.Filter);
        Assert.Contains("department eq 'Sales'", result.Filter);
    }

    // Test 8: Unsupported attribute is a FAILURE — a filter Graph will reject must never be
    // stored or used. (Previously a warning with Success=true; that is how the Avalon
    // target set silently collapsed to 6 mailboxes.)
    [Fact]
    public void Convert_UnsupportedAttribute_ReturnsFailureWithWarning()
    {
        var result = _converter.Convert("(SomeUnknownAttr -eq 'Value')");

        Assert.False(result.Success);
        Assert.NotNull(result.Warning);
        Assert.Contains("SomeUnknownAttr", result.Warning);
        Assert.Contains("SomeUnknownAttr", result.UnknownAttributes!);
    }

    // Test 9: Exchange auto-appends a GAL-visibility clause to most DDG RecipientFilters.
    // 'HiddenFromAddressListsEnabled' is not a Graph user property; passing it through makes
    // Graph 400, collapsing the DDG to ZERO members. It must be stripped so the remaining
    // filter is valid and the DDG resolves its real members.
    [Fact]
    public void Convert_StripsHiddenFromAddressListsEnabledClause_QuotedBoolean()
    {
        var result = _converter.Convert(
            "((Office -eq 'Blue Ridge') -and (CustomAttribute3 -eq 'Staff')) -and (HiddenFromAddressListsEnabled -eq 'False')");

        Assert.True(result.Success);
        Assert.DoesNotContain("HiddenFromAddressListsEnabled", result.Filter);
        Assert.Contains("officeLocation eq 'Blue Ridge'", result.Filter);
        Assert.Contains("onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'", result.Filter);
        Assert.Null(result.Warning);
    }

    // Test 10: same clause in PowerShell $false form (some DDGs render it this way).
    [Fact]
    public void Convert_StripsHiddenFromAddressListsEnabledClause_DollarBoolean()
    {
        var result = _converter.Convert(
            "(Office -eq 'Sandy Springs') -and (HiddenFromAddressListsEnabled -eq $false)");

        Assert.True(result.Success);
        Assert.DoesNotContain("HiddenFromAddressListsEnabled", result.Filter);
        Assert.Contains("officeLocation eq 'Sandy Springs'", result.Filter);
        Assert.Null(result.Warning);
    }

    // Test 9: Empty/null input returns failure
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Convert_EmptyOrNullInput_ReturnsFailure(string? input)
    {
        var result = _converter.Convert(input!);

        Assert.False(result.Success);
        Assert.NotNull(result.Warning);
    }

    // Test 10: Case-insensitive attribute and operator matching
    [Fact]
    public void Convert_CaseInsensitive_ReturnsODataFilter()
    {
        var result = _converter.Convert("(office -EQ 'Buckhead')");

        Assert.True(result.Success);
        Assert.Contains("officeLocation eq 'Buckhead'", result.Filter);
    }

    // Additional tests for ToPlainLanguage
    [Fact]
    public void ToPlainLanguage_SimpleOfficeFilter_ReturnsReadableText()
    {
        var plain = _converter.ToPlainLanguage("(Office -eq 'Buckhead')");

        Assert.Contains("Office", plain);
        Assert.Contains("=", plain);
        Assert.Contains("Buckhead", plain);
        Assert.DoesNotContain("-eq", plain);
    }

    [Fact]
    public void ToPlainLanguage_CompoundFilter_ReturnsReadableText()
    {
        var plain = _converter.ToPlainLanguage(
            "(Office -eq 'Buckhead') -and (CustomAttribute2 -eq 'AFH')");

        Assert.Contains("Office", plain);
        Assert.Contains("AND", plain);
        Assert.Contains("Brand", plain);
        Assert.Contains("AFH", plain);
    }

    // Test CustomAttribute3 -> Role mapping
    [Fact]
    public void Convert_CustomAttribute3_MapsToExtensionAttribute3()
    {
        var result = _converter.Convert("(CustomAttribute3 -eq 'Advisor')");

        Assert.True(result.Success);
        Assert.Contains("onPremisesExtensionAttributes/extensionAttribute3 eq 'Advisor'", result.Filter);
    }

    // Test Company attribute
    [Fact]
    public void Convert_CompanyFilter_ReturnsODataFilter()
    {
        var result = _converter.Convert("(Company -eq 'Atlanta Fine Homes')");

        Assert.True(result.Success);
        Assert.Contains("companyName eq 'Atlanta Fine Homes'", result.Filter);
    }

    // ---- Real tenant filters (captured 2026-08-25) -------------------------------------

    private static IReadOnlyList<(string Name, string Filter)> LoadFixtures()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ddg-recipient-filters.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("displayName").GetString()!, e.GetProperty("recipientFilter").GetString()!))
            .ToList();
    }

    private static readonly System.Text.RegularExpressions.Regex ExchangeOnlyAttribute = new(
        @"RecipientTypeDetails|RecipientType\b|HiddenFromAddressListsEnabled|\bName\b|MailboxPlan|SystemMailbox",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    [Fact]
    public void Convert_AllTenantFixtures_SucceedWithoutExchangeOnlyAttributes()
    {
        var fixtures = LoadFixtures();
        Assert.Equal(16, fixtures.Count);

        foreach (var (name, filter) in fixtures)
        {
            var result = _converter.Convert(filter);

            Assert.True(result.Success, $"{name}: {result.Warning}");
            Assert.DoesNotMatch(ExchangeOnlyAttribute, result.Filter);
            Assert.Null(result.Warning);
        }
    }

    [Theory]
    [InlineData("Buckhead Staff", "officeLocation eq 'Buckhead' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("Intown Staff", "officeLocation eq 'Intown' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("Cobb Staff", "officeLocation eq 'Cobb' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("North Atlanta Staff", "officeLocation eq 'North Atlanta' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("Blue Ridge Staff", "officeLocation eq 'Blue Ridge' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("Clayton Staff", "officeLocation eq 'Clayton' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("North Atlanta Office", "officeLocation eq 'North Atlanta' or (onPremisesExtensionAttributes/extensionAttribute2 eq 'AFH' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff')")]
    [InlineData("All Atlanta Fine Homes Staff", "onPremisesExtensionAttributes/extensionAttribute2 eq 'AFH' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    [InlineData("All Mountain Staff", "onPremisesExtensionAttributes/extensionAttribute2 eq 'MSIR' and onPremisesExtensionAttributes/extensionAttribute3 eq 'Staff'")]
    public void Convert_TenantFixture_ProducesExpectedGraphFilter(string name, string expected)
    {
        var (_, filter) = LoadFixtures().Single(f => f.Name == name);

        var result = _converter.Convert(filter);

        Assert.True(result.Success, result.Warning);
        Assert.Equal(expected, result.Filter);
    }

    // ---- Folding rules ------------------------------------------------------------------

    [Fact]
    public void Convert_RecipientTypeDetailsOrGroup_IsFoldedAway()
    {
        var result = _converter.Convert(
            "((Office -eq 'Buckhead') -and (((RecipientTypeDetails -eq 'UserMailbox') -or (RecipientTypeDetails -eq 'SharedMailbox'))))");

        Assert.True(result.Success);
        Assert.Equal("officeLocation eq 'Buckhead'", result.Filter);
    }

    [Fact]
    public void Convert_MailContactBranch_IsDropped()
    {
        var result = _converter.Convert(
            "((Office -eq 'Buckhead') -and (RecipientTypeDetails -eq 'UserMailbox')) -or ((RecipientTypeDetails -eq 'MailContact') -and (CustomAttribute4 -eq 'DDL'))");

        Assert.True(result.Success);
        Assert.Equal("officeLocation eq 'Buckhead'", result.Filter);
    }

    [Fact]
    public void Convert_ExchangeSystemExclusions_AreFoldedAway()
    {
        var result = _converter.Convert(
            "(Office -eq 'Buckhead') -and (-not(Name -like 'SystemMailbox{*')) -and (-not(RecipientTypeDetailsValue -eq 'MailboxPlan'))");

        Assert.True(result.Success);
        Assert.Equal("officeLocation eq 'Buckhead'", result.Filter);
    }

    [Fact]
    public void Convert_FilterThatFoldsToAllUsers_Fails()
    {
        var result = _converter.Convert("(RecipientTypeDetails -eq 'UserMailbox') -and (HiddenFromAddressListsEnabled -eq 'False')");

        Assert.False(result.Success);
        Assert.Contains("all users", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Convert_FilterThatFoldsToNoUsers_Fails()
    {
        var result = _converter.Convert("(RecipientTypeDetails -eq 'MailContact') -and (Office -eq 'Buckhead')");

        Assert.False(result.Success);
        Assert.Contains("no users", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Value safety and operators -----------------------------------------------------

    [Fact]
    public void Convert_AttributeNameInsideQuotedValue_IsNotRewritten()
    {
        var result = _converter.Convert("(Title -like 'Office Manager*') -and (Department -eq 'Sales Office')");

        Assert.True(result.Success);
        Assert.Equal("startsWith(jobTitle, 'Office Manager') and department eq 'Sales Office'", result.Filter);
    }

    [Theory]
    [InlineData("(Title -like 'Agent*')", "startsWith(jobTitle, 'Agent')")]
    [InlineData("(Title -like '*Agent')", "endsWith(jobTitle, 'Agent')")]
    [InlineData("(Title -like '*Agent*')", "contains(jobTitle, 'Agent')")]
    [InlineData("(Title -like 'Agent')", "jobTitle eq 'Agent'")]
    [InlineData("(Title -notlike 'Agent*')", "not(startsWith(jobTitle, 'Agent'))")]
    public void Convert_LikeOperators_MapToODataFunctions(string opath, string expected)
    {
        var result = _converter.Convert(opath);

        Assert.True(result.Success, result.Warning);
        Assert.Equal(expected, result.Filter);
    }

    [Fact]
    public void Convert_QuoteInValue_IsEscapedForOData()
    {
        var result = _converter.Convert("(Company -eq 'Sotheby''s')");

        Assert.True(result.Success);
        Assert.Equal("companyName eq 'Sotheby''s'", result.Filter);
    }

    [Fact]
    public void Convert_UnparseableFilter_Fails()
    {
        var result = _converter.Convert("(Office -eq 'Buckhead'");

        Assert.False(result.Success);
        Assert.Contains("parsed", result.Warning);
    }

    [Fact]
    public void ToPlainLanguage_DropsExchangeOnlyClauses_AndKeepsValuesIntact()
    {
        var (_, filter) = LoadFixtures().Single(f => f.Name == "Buckhead Staff");

        var plain = _converter.ToPlainLanguage(filter);

        Assert.Equal("Office = Buckhead AND Role = Staff", plain);
    }

    [Fact]
    public void ToPlainLanguage_UnparseableInput_FallsBackToTextReplacement()
    {
        var plain = _converter.ToPlainLanguage("(Office -eq 'Buckhead'");

        Assert.Contains("Office = Buckhead", plain);
    }

    // ---- Safety net: neither method may throw, no matter how malformed the input ---------

    // Note: the deeply-nested-parens case is capped at 500 levels, not the 5,000 one might
    // reach for. OpathParser is recursive-descent with no depth guard, and the xUnit test
    // host thread has a much smaller stack than a normal process main thread: empirically,
    // 5,000 levels crashes the test host with an *uncatchable* StackOverflowException at
    // ~1,636 levels (verified directly against this test host, not a synthetic harness). 500
    // stays a comfortable 3x below that observed crash point while still exercising deep
    // recursion. The unbounded-recursion-depth gap in OpathParser itself is out of scope for
    // this fix (limited to FilterConverter.cs and this test file) and is reported as a concern.
    private static readonly string[] AdversarialInputs =
    [
        "(Office -eq 'A') -and",
        "((Office -eq 'A')",
        "-not",
        "Office",
        "'unterminated",
        new string('(', 500),
    ];

    [Fact]
    public void Convert_AdversarialInputs_NeverThrow()
    {
        foreach (var input in AdversarialInputs)
        {
            var result = _converter.Convert(input);

            Assert.False(result.Success);
            Assert.NotNull(result.Warning);
        }
    }

    [Fact]
    public void ToPlainLanguage_AdversarialInputs_NeverThrow()
    {
        foreach (var input in AdversarialInputs)
        {
            var plain = _converter.ToPlainLanguage(input);

            Assert.False(string.IsNullOrEmpty(plain));
        }
    }
}
