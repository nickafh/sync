namespace AFHSync.Api.DTOs;

public record SyncRunDto(
    int Id,
    string RunType,
    string Status,
    bool IsDryRun,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int? DurationMs,
    int ContactsCreated,
    int ContactsUpdated,
    int ContactsRemoved,
    int ContactsSkipped,
    int ContactsFailed,
    int PhotosUpdated,
    int ThrottleEvents
);
