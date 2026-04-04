namespace AFHSync.Api.DTOs;

public record FieldProfileDetailDto(
    int Id,
    string Name,
    string? Description,
    bool IsDefault,
    FieldSectionDto[] Sections
);

public record FieldSectionDto(
    string Name,
    FieldSettingDto[] Fields
);

public record FieldSettingDto(
    string FieldName,
    string DisplayName,
    string Behavior
);
