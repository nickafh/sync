namespace AFHSync.Api.DTOs;

public record FilterConversionResult(
    bool Success,
    string Filter,
    string? Warning = null
);
