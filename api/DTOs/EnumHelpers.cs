namespace AFHSync.Api.DTOs;

using System.Reflection;
using NpgsqlTypes;

public static class EnumHelpers
{
    public static string ToPgName<T>(T value) where T : struct, Enum
    {
        var field = typeof(T).GetField(value.ToString());
        var attr = field?.GetCustomAttribute<PgNameAttribute>();
        return attr?.PgName ?? value.ToString().ToLowerInvariant();
    }
}
