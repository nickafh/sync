namespace AFHSync.Api.DTOs;

public record TriggerSyncRequest(
    string RunType = "manual",   // "manual" or "dry_run"
    bool IsDryRun = false,
    int[]? TunnelIds = null,     // null = all active tunnels
    bool AuditFolders = false    // §5.2: reconcile every folder this run touches (ignored by the engine in a dry run)
);
