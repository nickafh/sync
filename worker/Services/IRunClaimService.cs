using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;

namespace AFHSync.Worker.Services;

public enum RunClaimOutcome
{
    /// <summary>The row is now Running and belongs to this job.</summary>
    Claimed,
    /// <summary>Another run is Running (any run type — one lane). A requested Pending row was marked Failed.</summary>
    Blocked,
    /// <summary>No sync_runs row with the requested id.</summary>
    NotFound,
    /// <summary>The requested row is not Pending (Running, Success, Warning, Failed or Cancelled). Returned untouched.</summary>
    AlreadyFinalized
}

public sealed record RunClaimResult(RunClaimOutcome Outcome, SyncRun? Run);

/// <summary>
/// Phase 2 (§2.7): the single place that decides whether a run may start. Serialises on the
/// Postgres advisory lock (key 1) so two Hangfire workers cannot both pass the "is anything
/// Running?" guard. Used by SyncEngine and PhotoSyncService — contact runs and photo runs
/// share one lane because they write the same contacts.
/// </summary>
public interface IRunClaimService
{
    Task<RunClaimResult> ClaimAsync(int? runId, RunType runType, bool isDryRun, CancellationToken ct);
}
