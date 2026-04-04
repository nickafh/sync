namespace AFHSync.Api.DTOs;

public record TunnelRunSummaryDto(
    int? TunnelId,
    string TunnelName,
    int ContactsCreated,
    int ContactsUpdated,
    int ContactsRemoved,
    int ContactsSkipped,
    int ContactsFailed,
    int PhotosUpdated,
    string[] Errors
);
