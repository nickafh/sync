namespace AFHSync.Api.DTOs;

public record ContactDto(
    int SourceUserId,
    string? DisplayName,
    string? Email,
    string? JobTitle,
    string? Department,
    string? Office,
    string? Phone
);
