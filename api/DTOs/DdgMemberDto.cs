namespace AFHSync.Api.DTOs;

public record DdgMemberDto(
    string Id,
    string DisplayName,
    string? Email,
    string? JobTitle,
    string? Department,
    string? OfficeLocation
);
