namespace AFHSync.Api.Services;

using System.Text.RegularExpressions;
using AFHSync.Api.DTOs;
using Microsoft.Extensions.Logging;

/// <summary>
/// Converts Exchange OPATH RecipientFilter syntax to Microsoft Graph OData $filter syntax.
/// Uses table-based conversion for known AFH OPATH patterns (D-04, D-05).
/// Unsupported attributes fall back gracefully with a warning (D-06).
/// </summary>
public class FilterConverter : IFilterConverter
{
    private readonly ILogger<FilterConverter> _logger;

    // AFH-specific OPATH attribute -> OData field mapping (case-insensitive)
    private static readonly Dictionary<string, string> AttributeMap = new(StringComparer.OrdinalIgnoreCase)
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
    private static readonly Dictionary<string, string> PlainNameMap = new(StringComparer.OrdinalIgnoreCase)
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

    // Regex pattern matching OPATH attribute names (word boundaries, case-insensitive)
    private static readonly Regex AttributePattern = new(
        @"\b(" + string.Join("|", AttributeMap.Keys) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public FilterConverter(ILogger<FilterConverter> logger)
    {
        _logger = logger;
    }

    public FilterConversionResult Convert(string opathFilter)
    {
        if (string.IsNullOrWhiteSpace(opathFilter))
        {
            return new FilterConversionResult(
                Success: false,
                Filter: opathFilter ?? string.Empty,
                Warning: "Filter is empty or null");
        }

        try
        {
            var filter = opathFilter.Trim();
            var warnings = new List<string>();

            // Strip Exchange system mailbox exclusions (no Graph equivalent, not needed)
            // Removes patterns like: -and (-not(RecipientTypeDetailsValue -eq 'MailboxPlan'))
            filter = Regex.Replace(filter,
                @"\s*-and\s+\(-not\(RecipientTypeDetailsValue\s+-eq\s+'[^']*'\)\)",
                string.Empty, RegexOptions.IgnoreCase);
            // Removes patterns like: -and (-not(Name -like 'SystemMailbox{*'))
            filter = Regex.Replace(filter,
                @"\s*-and\s+\(-not\(Name\s+-like\s+'[^']*'\)\)",
                string.Empty, RegexOptions.IgnoreCase);

            // Clean up extra wrapping parentheses left behind
            // Collapse (((...))) down to the inner content
            while (Regex.IsMatch(filter, @"^\(+\(([^()]+)\)\)+$"))
                filter = Regex.Replace(filter, @"^\(+\(([^()]+)\)\)+$", "($1)");
            // Strip single outer parens: (expr) -> expr
            filter = Regex.Replace(filter, @"^\(([^()]*)\)$", "$1");

            // Track which attributes in the filter are recognized
            DetectUnrecognizedAttributes(filter, warnings);

            // Replace OPATH operators with OData operators (case-insensitive)
            filter = Regex.Replace(filter, @"\s+-eq\s+", " eq ", RegexOptions.IgnoreCase);
            filter = Regex.Replace(filter, @"\s+-ne\s+", " ne ", RegexOptions.IgnoreCase);
            filter = Regex.Replace(filter, @"\s+-and\s+", " and ", RegexOptions.IgnoreCase);
            filter = Regex.Replace(filter, @"\s+-or\s+", " or ", RegexOptions.IgnoreCase);
            filter = Regex.Replace(filter, @"\s+-not\s+", " not ", RegexOptions.IgnoreCase);

            // Handle -like operator: convert to startsWith() for prefix matches
            filter = Regex.Replace(filter, @"(\w+)\s+-like\s+'([^*']+)\*'",
                m => $"startsWith({m.Groups[1].Value}, '{m.Groups[2].Value}')",
                RegexOptions.IgnoreCase);

            // Replace OPATH attribute names with OData field names
            filter = AttributePattern.Replace(filter, m =>
            {
                if (AttributeMap.TryGetValue(m.Value, out var odataField))
                    return odataField;
                return m.Value; // Should not happen due to regex, but safety fallback
            });

            string? warning = warnings.Count > 0
                ? $"Unrecognized attribute(s): {string.Join(", ", warnings)} -- manual review recommended"
                : null;

            return new FilterConversionResult(
                Success: true,
                Filter: filter,
                Warning: warning);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OPATH filter conversion failed for: {Filter}", opathFilter);
            return new FilterConversionResult(
                Success: false,
                Filter: opathFilter,
                Warning: $"Filter conversion failed -- manual review required: {ex.Message}");
        }
    }

    public string ToPlainLanguage(string opathFilter)
    {
        if (string.IsNullOrWhiteSpace(opathFilter))
            return string.Empty;

        var plain = opathFilter.Trim();

        // Replace attribute names with human-readable display names
        foreach (var (opathAttr, displayName) in PlainNameMap)
        {
            plain = Regex.Replace(plain, $@"\b{Regex.Escape(opathAttr)}\b", displayName,
                RegexOptions.IgnoreCase);
        }

        // Replace OPATH operators with readable symbols
        plain = Regex.Replace(plain, @"\s+-eq\s+", " = ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-ne\s+", " != ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-and\s+", " AND ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-or\s+", " OR ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-not\s+", " NOT ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+-like\s+", " LIKE ", RegexOptions.IgnoreCase);

        // Strip outer parentheses from simple expressions
        plain = Regex.Replace(plain, @"^\(([^()]*)\)$", "$1");

        // Strip single quotes from values
        plain = plain.Replace("'", "");

        return plain;
    }

    /// <summary>
    /// Detects attributes in the filter that are not in the known attribute map.
    /// Adds unrecognized attribute names to the warnings list.
    /// </summary>
    private static void DetectUnrecognizedAttributes(string filter, List<string> warnings)
    {
        // Match patterns like "(AttributeName -eq 'value')" to find attribute names
        // Attributes appear before OPATH operators
        var attrPattern = new Regex(@"\(?\s*(\w+)\s+-(eq|ne|like|gt|lt|ge|le)\b", RegexOptions.IgnoreCase);
        foreach (Match match in attrPattern.Matches(filter))
        {
            var attrName = match.Groups[1].Value;
            if (!AttributeMap.ContainsKey(attrName))
            {
                warnings.Add(attrName);
            }
        }
    }
}
