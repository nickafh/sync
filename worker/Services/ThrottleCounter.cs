namespace AFHSync.Worker.Services;

/// <summary>
/// Thread-safe throttle event counter shared between the singleton
/// GraphResilienceHandler and scoped SyncEngine instances.
/// Registered as a singleton. SyncEngine calls Reset() at run start
/// and reads Count at run end.
/// </summary>
public sealed class ThrottleCounter
{
    private int _count;

    /// <summary>Current throttle event count since last reset.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Atomically increment the counter. Thread-safe for parallel mailbox processing.</summary>
    public void Increment() => Interlocked.Increment(ref _count);

    /// <summary>Reset counter to zero. Called at the start of each sync run.</summary>
    public void Reset() => Interlocked.Exchange(ref _count, 0);
}
