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

    /// <summary>
    /// Parses an enum from either its PgName (snake_case) or its C# name (PascalCase).
    /// </summary>
    public static bool TryFromPgName<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        if (string.IsNullOrEmpty(value)) return false;

        // Try matching by PgName attribute first
        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attr = field.GetCustomAttribute<PgNameAttribute>();
            if (attr != null && string.Equals(attr.PgName, value, StringComparison.OrdinalIgnoreCase))
            {
                result = (T)field.GetValue(null)!;
                return true;
            }
        }

        // Fall back to standard Enum.TryParse (handles PascalCase names)
        return Enum.TryParse(value, ignoreCase: true, out result);
    }
}
