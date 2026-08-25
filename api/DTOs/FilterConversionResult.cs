namespace AFHSync.Api.DTOs;

/// <summary>
/// Outcome of converting an Exchange OPATH RecipientFilter to a Graph OData $filter.
/// <see cref="Success"/> is false when the filter could not be parsed, when any attribute
/// with no Graph equivalent remains after folding Exchange-only predicates, or when the
/// filter collapses to a constant (matches all / no users). A false result must never be
/// stored as a source filter or sent to Graph.
/// </summary>
public record FilterConversionResult(
    bool Success,
    string Filter,
    string? Warning = null,
    IReadOnlyList<string>? UnknownAttributes = null
);
