namespace AFHSync.Api.Services;

/// <summary>Phase 3 (§3.4): small helpers for building Graph OData queries safely.</summary>
public static class GraphQuery
{
    /// <summary>Upper bound on security groups listed by GET /api/graph/security-groups.</summary>
    public const int SecurityGroupCap = 2000;

    /// <summary>Escapes a value for use inside an OData string literal ('...'): a single quote becomes two.</summary>
    public static string EscapeLiteral(string value) => value.Replace("'", "''");
}
