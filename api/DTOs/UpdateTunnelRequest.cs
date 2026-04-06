namespace AFHSync.Api.DTOs;

using System.ComponentModel.DataAnnotations;

public record UpdateTunnelRequest(
    [Required][StringLength(100, MinimumLength = 1)] string Name,
    SourceInput[] Sources,
    int[] TargetListIds,
    int? FieldProfileId,
    string StalePolicy,
    int StaleDays = 14,
    bool PhotoSyncEnabled = true
);
