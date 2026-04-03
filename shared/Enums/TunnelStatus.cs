using NpgsqlTypes;

namespace AFHSync.Shared.Enums;

public enum TunnelStatus
{
    [PgName("active")] Active,
    [PgName("inactive")] Inactive
}
