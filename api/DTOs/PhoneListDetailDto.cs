namespace AFHSync.Api.DTOs;

public record PhoneListDetailDto(
    int Id,
    string Name,
    string? Description,
    string? ExchangeFolderId,
    int ContactCount,
    int UserCount,
    PhoneListSourceTunnelDto[] SourceTunnels,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
