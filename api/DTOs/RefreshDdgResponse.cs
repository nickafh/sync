namespace AFHSync.Api.DTOs;

public record RefreshDdgResponse(
    string Message,
    string? GraphFilter,
    string? FilterPlain
);
