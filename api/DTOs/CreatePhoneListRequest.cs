namespace AFHSync.Api.DTOs;

public record CreatePhoneListRequest(
    string Name,
    string? Description,
    string TargetScope = "all_users",
    string? TargetUserFilter = null
);
