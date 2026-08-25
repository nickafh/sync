namespace AFHSync.Api.Services;

using System.Text.RegularExpressions;
using AFHSync.Api.DTOs;
using AFHSync.Api.Services.Opath;
using Microsoft.Extensions.Logging;

/// <summary>
/// Converts Exchange OPATH RecipientFilter syntax to Microsoft Graph OData $filter syntax.
///
/// Pipeline: parse (OpathParser) → fold Exchange-only predicates to constants (OpathFolder.Fold)
/// → simplify (OpathFolder.Simplify) → render (ODataRenderer).
///
/// A filter that still references an attribute with no Graph equivalent, that cannot be parsed,
/// or that collapses to "everyone"/"no one" is a FAILURE (Success=false). Callers must not store
/// or query with a failed result — Graph rejects such filters with Request_UnsupportedQuery,
/// which is how a DDG silently resolves to zero members.
/// </summary>
public class FilterConverter : IFilterConverter
{
    private readonly ILogger<FilterConverter> _logger;

    // AFH-specific OPATH attribute -> OData field mapping (case-insensitive)
    internal static readonly Dictionary<string, string> AttributeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Office"] = "officeLocation",
        ["CustomAttribute1"] = "onPremisesExtensionAttributes/extensionAttribute1",
        ["CustomAttribute2"] = "onPremisesExtensionAttributes/extensionAttribute2",
        ["CustomAttribute3"] = "onPremisesExtensionAttributes/extensionAttribute3",
        ["CustomAttribute4"] = "onPremisesExtensionAttributes/extensionAttribute4",
        ["CustomAttribute5"] = "onPremisesExtensionAttributes/extensionAttribute5",
        ["Department"] = "department",
        ["DisplayName"] = "displayName",
        ["Title"] = "jobTitle",
        ["Company"] = "companyName",
        ["City"] = "city",
        ["State"] = "state",
        ["Country"] = "country",
    };

    // Human-readable display names for ToPlainLanguage
    internal static readonly Dictionary<string, string> PlainNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Office"] = "Office",
        ["CustomAttribute1"] = "Custom1",
        ["CustomAttribute2"] = "Brand",
        ["CustomAttribute3"] = "Role",
        ["CustomAttribute4"] = "Department Code",
        ["CustomAttribute5"] = "Custom5",
        ["Department"] = "Department",
        ["DisplayName"] = "Name",
        ["Title"] = "Title",
        ["Company"] = "Company",
        ["City"] = "City",
        ["State"] = "State",
        ["Country"] = "Country",
    };

    public FilterConverter(ILogger<FilterConverter> logger)
    {
        _logger = logger;
    }

    public FilterConversionResult Convert(string opathFilter)
    {
        if (string.IsNullOrWhiteSpace(opathFilter))
            return new FilterConversionResult(false, opathFilter ?? string.Empty, "Filter is empty or null", []);

        OpathNode ast;
        try
        {
            ast = OpathParser.Parse(opathFilter.Trim());
        }
        catch (OpathParseException ex)
        {
            _logger.LogWarning("OPATH filter could not be parsed: {Message}. Filter: {Filter}", ex.Message, opathFilter);
            return new FilterConversionResult(false, opathFilter, $"Filter could not be parsed: {ex.Message}", []);
        }

        var unknown = new List<string>();
        var simplified = OpathFolder.Simplify(OpathFolder.Fold(ast, unknown));

        if (unknown.Count > 0)
        {
            var distinct = unknown.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _logger.LogWarning("OPATH filter uses attribute(s) with no Graph equivalent: {Attrs}. Filter: {Filter}",
                string.Join(", ", distinct), opathFilter);
            return new FilterConversionResult(false, opathFilter,
                $"Unsupported attribute(s): {string.Join(", ", distinct)} — this filter cannot be evaluated by Graph",
                distinct);
        }

        if (simplified is OpathConst constant)
        {
            return new FilterConversionResult(false, opathFilter,
                constant.Value
                    ? "Filter matches all users once Exchange-only conditions are removed; a source filter must be selective"
                    : "Filter matches no users (it only selects non-user recipients such as mail contacts)",
                []);
        }

        return new FilterConversionResult(true, ODataRenderer.Render(simplified), null, []);
    }

    public string ToPlainLanguage(string opathFilter)
    {
        if (string.IsNullOrWhiteSpace(opathFilter))
            return string.Empty;

        try
        {
            var ast = OpathParser.Parse(opathFilter.Trim());
            var simplified = OpathFolder.Simplify(OpathFolder.Fold(ast, new List<string>()));
            return PlainLanguageRenderer.Render(simplified);
        }
        catch (OpathParseException)
        {
            return ToPlainLanguageLegacy(opathFilter);
        }
    }

    /// <summary>Text-replacement fallback for filters the parser rejects (kept so the UI never shows nothing).</summary>
    private static string ToPlainLanguageLegacy(string opathFilter)
    {
        var plain = opathFilter.Trim();
        foreach (var (opathAttr, displayName) in PlainNameMap)
        {
            plain = Regex.Replace(plain, $@"\b{Regex.Escape(opathAttr)}\b", displayName, RegexOptions.IgnoreCase);
        }
        plain = Regex.Replace(plain, @"\s+-eq\s+", " = ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-ne\s+", " != ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-and\s+", " AND ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-or\s+", " OR ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-not\s+", " NOT ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-like\s+", " LIKE ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"^\(([^()]*)\)$", "$1");
        return plain.Replace("'", "");
    }
}
