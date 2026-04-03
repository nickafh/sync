using NpgsqlTypes;

namespace AFHSync.Shared.Enums;

public enum SyncBehavior
{
    [PgName("nosync")] Nosync,
    [PgName("add_missing")] AddMissing,
    [PgName("always")] Always,
    [PgName("remove_blank")] RemoveBlank
}
