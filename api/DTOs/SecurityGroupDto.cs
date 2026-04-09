namespace AFHSync.Api.DTOs;

public record SecurityGroupDto(
    string Id,
    string DisplayName,
    string? Description,
    string? MembershipRule
);
