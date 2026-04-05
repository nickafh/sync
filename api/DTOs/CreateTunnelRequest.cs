namespace AFHSync.Api.DTOs;

using System.ComponentModel.DataAnnotations;

public record CreateTunnelRequest(
    [Required][StringLength(100, MinimumLength = 1)] string Name,
    string SourceType,
    [Required][StringLength(500)] string SourceIdentifier,
    string? SourceDisplayName,
    string? SourceSmtpAddress,
    string? SourceFilterPlain,
    string TargetScope,
    int[] TargetListIds,
    int? FieldProfileId,
    string StalePolicy,
    int StaleDays = 14,
    bool PhotoSyncEnabled = true
);
