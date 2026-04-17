namespace AFHSync.Shared.Services;

/// <summary>
/// Hangfire-invokable runner for tenant-wide contact-folder cleanups.
/// The interface lives in shared/ so the API project can reference it for
/// `BackgroundJob.Enqueue&lt;ICleanupJobRunner&gt;(...)` without taking a project
/// dependency on the worker. The worker registers the concrete implementation
/// in its own DI container; Hangfire resolves it from there at job execution time.
/// </summary>
public interface ICleanupJobRunner
{
    /// <summary>
    /// Executes the deletion of all <paramref name="items"/> against Microsoft Graph.
    /// Updates the matching CleanupJob row with progress every 25 items and on
    /// terminal status (Completed | Failed).
    /// </summary>
    /// <param name="jobId">CleanupJob row id (created by the API before enqueue).</param>
    /// <param name="items">Folders to delete — serialized into the Hangfire job payload.</param>
    /// <param name="ct">Cancellation token (Hangfire passes a default token; cancellation is not surfaced in v1).</param>
    Task RunAsync(Guid jobId, CleanupJobItem[] items, CancellationToken ct);
}

/// <summary>
/// Folder-deletion target. Mirrors the legacy CleanupDeleteItem in the API
/// controller so the worker doesn't need to take a project reference on api/.
/// </summary>
public record CleanupJobItem(string EntraId, string Email, string FolderId, string FolderName);
