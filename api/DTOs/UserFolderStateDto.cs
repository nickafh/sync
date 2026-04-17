namespace AFHSync.Api.DTOs;

public record UserFolderStateDto(
    string Email,
    string? EntraId,
    string? DisplayName,
    bool IsTrackedTargetMailbox,
    int? TargetMailboxId,
    UserFolderDto[] Folders,
    UserOrphanTunnelDto[] OrphanTunnels);

public record UserFolderDto(
    string FolderId,
    string FolderName,
    int GraphContactCount,
    int? MatchedTunnelId,
    string? MatchedTunnelName,
    int? ExpectedContactCount,
    DateTime? LastSyncedAt);

public record UserOrphanTunnelDto(
    int TunnelId,
    string TunnelName,
    int ExpectedContactCount,
    DateTime? LastSyncedAt);
