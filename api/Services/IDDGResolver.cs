namespace AFHSync.Api.Services;

/// <summary>
/// Resolves Dynamic Distribution Groups from Exchange Online.
/// Per D-01: Uses Exchange Online PowerShell to read DDG definitions and RecipientFilters.
/// Per D-03: Called during tunnel setup only, not during sync runs.
/// </summary>
public interface IDDGResolver
{
    Task<IReadOnlyList<DdgInfo>> ListDdgsAsync(CancellationToken ct = default);
    Task<DdgInfo?> GetDdgAsync(string identity, CancellationToken ct = default);
}

/// <summary>
/// Represents a Dynamic Distribution Group from Exchange Online.
/// </summary>
public record DdgInfo(
    string Id,
    string DisplayName,
    string PrimarySmtpAddress,
    string RecipientFilter
);
