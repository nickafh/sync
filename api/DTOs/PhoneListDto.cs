namespace AFHSync.Api.DTOs;

public record PhoneListDto(
    int Id,
    string Name,
    int ContactCount,
    int UserCount,
    string TargetScope,
    string? TargetUserFilter,
    PhoneListSourceTunnelDto[] SourceTunnels,
    string? LastSyncStatus
);

public record PhoneListSourceTunnelDto(int Id, string Name);
