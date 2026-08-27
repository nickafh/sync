namespace AFHSync.Shared.Services;

/// <summary>
/// Phase 3 (§3.8): the ONE Postgres advisory-lock key that serialises "may a run start?". The
/// API's trigger guard (check for Pending/Running, insert Pending) and the worker's
/// RunClaimService (claim or create) must take the same transaction-scoped lock — with different
/// keys a cron claim could slip between the API's check and its insert.
/// </summary>
public static class RunLocks
{
    public const int RunStartAdvisoryKey = 1;
    public const string AcquireRunStartLockSql = "SELECT pg_advisory_xact_lock(1)";
}
