using NpgsqlTypes;

namespace AFHSync.Shared.Enums;

public enum TargetScope
{
    [PgName("all_users")] AllUsers,
    [PgName("specific_users")] SpecificUsers
}
