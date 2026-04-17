using NpgsqlTypes;

namespace AFHSync.Shared.Enums;

/// <summary>
/// Lifecycle states for a CleanupJob row. The Npgsql enum mapping (see api/Program.cs
/// and worker/Program.cs) translates between this CLR enum and the snake_case
/// PostgreSQL `cleanup_job_status` enum type.
/// </summary>
public enum CleanupJobStatus
{
    [PgName("queued")] Queued,
    [PgName("running")] Running,
    [PgName("completed")] Completed,
    [PgName("failed")] Failed,
    [PgName("cancelled")] Cancelled
}
