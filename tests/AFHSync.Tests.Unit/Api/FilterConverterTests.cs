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

    // Test 8: Unsupported attribute falls through with warning (D-06)
    [Fact]
    public void Convert_UnsupportedAttribute_ReturnsSuccessWithWarning()
    {
        var result = _converter.Convert("(SomeUnknownAttr -eq 'Value')");

        Assert.True(result.Success);
        Assert.NotNull(result.Warning);
        Assert.Contains("SomeUnknownAttr", result.Warning);
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
}
