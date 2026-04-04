namespace AFHSync.Api.DTOs;

public record SyncRunDetailDto(
    int Id,
    string RunType,
    string Status,
    bool IsDryRun,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int? DurationMs,
    int TunnelsProcessed,
    int TunnelsWarned,
    int ContactsCreated,
    int ContactsUpdated,
    int ContactsRemoved,
    int ContactsSkipped,
    int ContactsFailed,
    int PhotosUpdated,
    int ThrottleEvents,
    string? ErrorSummary
);
