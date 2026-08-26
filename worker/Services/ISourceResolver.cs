using AFHSync.Shared.Entities;

namespace AFHSync.Worker.Services;

/// <summary>Phase 2 (§2.3): one tunnel source that could not contribute members this run.</summary>
public sealed record SourceFailure(int SourceId, string DisplayName, string Reason);

/// <summary>
/// Resolved, deduplicated, upserted source users plus every source that failed. A non-empty
/// <see cref="FailedSources"/> means <see cref="Users"/> is INCOMPLETE — the engine must not run
/// the stale pass against it.
/// </summary>
public sealed record SourceResolution(List<SourceUser> Users, IReadOnlyList<SourceFailure> FailedSources);

/// <summary>
/// Resolves source members for a tunnel by querying Microsoft Graph /users
/// with the tunnel's stored $filter, paginating with PageIterator, applying
/// post-query filtering, upserting to the database, and returning the filtered list.
/// </summary>
public interface ISourceResolver
{
    Task<SourceResolution> ResolveAsync(Tunnel tunnel, CancellationToken ct);
}
