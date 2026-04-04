namespace AFHSync.Api.DTOs;

public record DdgDto(
    string Id,
    string DisplayName,
    string PrimarySmtpAddress,
    string RecipientFilter,
    string? RecipientFilterPlain,
    string? GraphFilter,
    bool GraphFilterSuccess,
    string? GraphFilterWarning,
    int MemberCount,
    string Type    // "Office", "Role", "Brand", or "Other"
);
