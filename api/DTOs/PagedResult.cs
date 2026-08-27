using System.Text.Json.Serialization;

namespace AFHSync.Api.DTOs;

/// <summary>
/// Phase 3 (§3.3): paged envelope. <see cref="HasMore"/> is computed server-side by fetching
/// <c>pageSize + 1</c> rows, so clients never have to over-fetch. <see cref="Total"/> is only set
/// by endpoints whose count is cheap (phone-list contacts) and is omitted from the JSON otherwise.
/// </summary>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    bool HasMore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Total = null);
