namespace AFHSync.Api.DTOs;

public record SyncRunItemDto(
    int Id,
    int? TunnelId,
    int? SourceUserId,
    string? SourceUserName,
    string Action,
    string? FieldChanges,
    string? ErrorMessage,
    DateTime CreatedAt
);
