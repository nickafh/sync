using AFHSync.Shared.Entities;

namespace AFHSync.Worker.Services;

/// <summary>
/// Resolves source members for a tunnel by querying Microsoft Graph /users
/// with the tunnel's stored $filter, paginating with PageIterator, applying
/// post-query filtering, upserting to the database, and returning the filtered list.
/// </summary>
public interface ISourceResolver
{
    Task<List<SourceUser>> ResolveAsync(Tunnel tunnel, CancellationToken ct);
}
