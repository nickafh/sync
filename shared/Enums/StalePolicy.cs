using NpgsqlTypes;

namespace AFHSync.Shared.Enums;

public enum StalePolicy
{
    [PgName("auto_remove")] AutoRemove,
    [PgName("flag_hold")] FlagHold,
    [PgName("leave")] Leave
}
