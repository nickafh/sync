using AFHSync.Shared.Services;

namespace AFHSync.Tests.Unit.Sync;

public class RunLocksTests
{
    [Fact]
    public void AcquireRunStartLockSql_UsesTheSharedKey()
        => Assert.Equal($"SELECT pg_advisory_xact_lock({RunLocks.RunStartAdvisoryKey})", RunLocks.AcquireRunStartLockSql);
}
