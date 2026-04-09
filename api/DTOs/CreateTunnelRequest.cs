namespace AFHSync.Api.DTOs;

using System.ComponentModel.DataAnnotations;

public record CreateTunnelRequest(
    [Required][StringLength(100, MinimumLength = 1)] string Name,
    SourceInput[] Sources,
    int[] TargetListIds,
    int? FieldProfileId,
    string StalePolicy,
    int StaleDays = 14,
    bool PhotoSyncEnabled = true,
    string? TargetGroupId = null,
    string? TargetGroupName = null,
    string? TargetUserEmails = null
);

public record SourceInput(
    string SourceType,
    [Required] string SourceIdentifier,
    string? SourceDisplayName,
    string? SourceSmtpAddress,
    string? SourceFilterPlain
);
