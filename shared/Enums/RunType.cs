using NpgsqlTypes;

namespace AFHSync.Shared.Enums;

public enum RunType
{
    [PgName("manual")] Manual,
    [PgName("scheduled")] Scheduled,
    [PgName("dry_run")] DryRun,
    [PgName("photo_sync")] PhotoSync
}
