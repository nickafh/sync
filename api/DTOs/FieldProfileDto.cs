namespace AFHSync.Api.DTOs;

public record FieldProfileDto(
    int Id,
    string Name,
    string? Description,
    bool IsDefault,
    int FieldCount
);
