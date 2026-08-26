using Npgsql;
using Xunit;

namespace AFHSync.Tests.Integration;

/// <summary>
/// A [Fact] that is skipped when no PostgreSQL server is reachable (dev laptops without Docker).
/// Point it at a server with AFHSYNC_TEST_PG (a connection string whose Database is the
/// maintenance DB, e.g. "Host=localhost;Port=5432;Username=afhsync;Password=…;Database=postgres").
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresTestServer.IsReachable)
            Skip = $"Postgres not reachable at {PostgresTestServer.HostDescription} — set AFHSYNC_TEST_PG to run";
    }
}

public static class PostgresTestServer
{
    public static string AdminConnectionString { get; } =
        Environment.GetEnvironmentVariable("AFHSYNC_TEST_PG")
        ?? "Host=localhost;Port=5432;Username=afhsync;Password=devpassword;Database=postgres;Timeout=3";

    public static string HostDescription { get; } =
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Password = null }.ToString();

    private static readonly Lazy<bool> Reachable = new(() =>
    {
        try
        {
            using var connection = new NpgsqlConnection(AdminConnectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    });

    public static bool IsReachable => Reachable.Value;
}
