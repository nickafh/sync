namespace AFHSync.Api.DTOs;

public record DashboardDto(
    int ActiveTunnels,
    int TotalPhoneLists,
    int TotalTargetUsers,
    DashboardLastSyncDto? LastSync,
    DashboardWarningDto[] Warnings,
    DashboardRecentRunDto[] RecentRuns
);

public record DashboardLastSyncDto(
    int Id,
    string Status,
    DateTime? StartedAt,
    int? DurationMs,
    int ContactsCreated,
    int ContactsUpdated,
    int ContactsRemoved,
    int PhotosUpdated
);

public record DashboardWarningDto(string Type, int? TunnelId, string Message);

public record DashboardRecentRunDto(
    int Id,
    string RunType,
    string Status,
    DateTime? StartedAt,
    int? DurationMs,
    int ContactsUpdated
);
