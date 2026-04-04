using AFHSync.Worker.Services;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Unit tests for ThrottleCounter — thread-safe singleton counter shared between
/// GraphResilienceHandler (singleton) and SyncEngine (scoped) instances.
/// </summary>
public class ThrottleCounterTests
{
    // ── Test 1: Initial count is 0 ──────────────────────────────────────────

    [Fact]
    public void ThrottleCounter_InitialCount_IsZero()
    {
        var counter = new ThrottleCounter();
        Assert.Equal(0, counter.Count);
    }

    // ── Test 2: Increment increments Count ──────────────────────────────────

    [Fact]
    public void ThrottleCounter_Increment_IncreasesCount()
    {
        var counter = new ThrottleCounter();

        counter.Increment();
        counter.Increment();
        counter.Increment();

        Assert.Equal(3, counter.Count);
    }

    // ── Test 3: Reset sets Count back to 0 ──────────────────────────────────

    [Fact]
    public void ThrottleCounter_Reset_SetsCountToZero()
    {
        var counter = new ThrottleCounter();
        counter.Increment();
        counter.Increment();

        counter.Reset();

        Assert.Equal(0, counter.Count);
    }

    // ── Test 4: Increment is thread-safe under concurrent access ────────────

    [Fact]
    public void ThrottleCounter_Increment_IsThreadSafe()
    {
        var counter = new ThrottleCounter();
        const int iterations = 100;

        Parallel.For(0, iterations, _ => counter.Increment());

        Assert.Equal(iterations, counter.Count);
    }

    // ── Test 5: Reset after concurrent increments returns to 0 ─────────────

    [Fact]
    public void ThrottleCounter_Reset_AfterConcurrentIncrements_ReturnsToZero()
    {
        var counter = new ThrottleCounter();
        Parallel.For(0, 50, _ => counter.Increment());

        counter.Reset();

        Assert.Equal(0, counter.Count);
    }
}
