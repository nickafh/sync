namespace AFHSync.Api.DTOs;

public record TunnelDetailDto(
    int Id,
    string Name,
    TunnelSourceDto[] Sources,
    string Status,
    string StalePolicy,
    int StaleHoldDays,
    int? FieldProfileId,
    string? FieldProfileName,
    TunnelTargetListDto[] TargetLists,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool PhotoSyncEnabled,
    string? TargetGroupId,
    string? TargetGroupName,
    string? TargetUserEmails
);
