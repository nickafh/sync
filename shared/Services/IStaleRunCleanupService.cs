namespace AFHSync.Shared.Services;

/// <summary>
/// Safety-net job that marks sync runs stuck in "Running" for over 2 hours as Failed.
/// Interface in shared project so API can render the type name on the Hangfire dashboard
/// (dashboard is hosted in the API process, which doesn't reference the Worker assembly).
/// </summary>
public interface IStaleRunCleanupService
{
    Task CleanupAsync();
}
