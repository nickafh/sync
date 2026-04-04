namespace AFHSync.Api.DTOs;

public record TunnelDetailDto(
    int Id,
    string Name,
    string SourceType,
    string SourceIdentifier,
    string? SourceDisplayName,
    string? SourceSmtpAddress,
    string TargetScope,
    string? TargetUserFilter,
    string Status,
    string StalePolicy,
    int StaleHoldDays,
    int? FieldProfileId,
    string? FieldProfileName,
    TunnelTargetListDto[] TargetLists,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
