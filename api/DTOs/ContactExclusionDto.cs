namespace AFHSync.Api.DTOs;

public record SourceContactDto(
    int Id,
    string EntraId,
    string? DisplayName,
    string? Email,
    string? CompanyName,
    string? JobTitle,
    string? Department,
    bool IsExcluded
);

public record ContactExclusionInput(
    string EntraId,
    string? DisplayName,
    string? Email
);

public record UpdateContactExclusionsRequest(ContactExclusionInput[] Exclusions);
