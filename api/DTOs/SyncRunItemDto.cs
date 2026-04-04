namespace AFHSync.Api.DTOs;

public record SyncRunItemDto(
    int Id,
    int? TunnelId,
    int? SourceUserId,
    string Action,
    string? FieldChanges,
    string? ErrorMessage,
    DateTime CreatedAt
);
