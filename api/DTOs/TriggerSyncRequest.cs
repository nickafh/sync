namespace AFHSync.Api.DTOs;

public record TriggerSyncRequest(
    string RunType = "manual",   // "manual" or "dry_run"
    bool IsDryRun = false,
    int[]? TunnelIds = null      // null = all active tunnels
);
