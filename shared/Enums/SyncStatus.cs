using NpgsqlTypes;

namespace AFHSync.Shared.Enums;

public enum SyncStatus
{
    [PgName("pending")] Pending,
    [PgName("running")] Running,
    [PgName("success")] Success,
    [PgName("warning")] Warning,
    [PgName("failed")] Failed,
    [PgName("cancelled")] Cancelled
}
