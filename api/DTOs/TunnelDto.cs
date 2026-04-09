namespace AFHSync.Api.DTOs;

public record TunnelDto(
    int Id,
    string Name,
    TunnelSourceDto[] Sources,
    string Status,
    string StalePolicy,
    int StaleHoldDays,
    int? FieldProfileId,
    string? FieldProfileName,
    TunnelTargetListDto[] TargetLists,
    int EstimatedContacts,
    int EstimatedTargetUsers,
    TunnelLastSyncDto? LastSync,
    bool PhotoSyncEnabled,
    string? TargetGroupId,
    string? TargetGroupName,
    string? TargetUserEmails
);

public record TunnelSourceDto(
    int Id,
    string SourceType,
    string SourceIdentifier,
    string? SourceDisplayName,
    string? SourceSmtpAddress,
    string? SourceFilterPlain
);

public record TunnelTargetListDto(int Id, string Name);
public record TunnelLastSyncDto(string Status, DateTime? CompletedAt, int ContactsUpdated);
