namespace AFHSync.Api.DTOs;

public record OrgContactDto(
    string Id,
    string? DisplayName,
    string? Email,
    string? CompanyName,
    string? Department,
    string? JobTitle,
    string? BusinessPhone,
    bool IsExcluded
);

public record UpdateOrgContactFiltersRequest(
    OrgContactFilterInput[] Filters
);

public record OrgContactFilterInput(
    string OrgContactId,
    string? DisplayName,
    string? Email,
    string? CompanyName,
    bool IsExcluded
);
