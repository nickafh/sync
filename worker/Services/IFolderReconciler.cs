using AFHSync.Shared.Entities;

namespace AFHSync.Worker.Services;

/// <param name="Examined">Graph contacts found in the folder.</param>
/// <param name="Adopted">Strays matched to a current source user and given a state row.</param>
/// <param name="Removed">Strays deleted from Graph.</param>
public sealed record FolderReconcileResult(int Examined, int Adopted, int Removed);

/// <summary>
/// Phase 3 (§3.7): reconciles a tunnel's contact folder in one mailbox against contact_sync_state.
/// A "stray" is a Graph contact whose id no state row references — the residue of a create chunk
/// whose outcome was lost (transport failure, crash, shutdown between the POST and the persist).
/// </summary>
public interface IFolderReconciler
{
    Task<FolderReconcileResult> ReconcileAsync(
        Tunnel tunnel,
        TargetMailbox mailbox,
        string folderId,
        int canonicalPhoneListId,
        IReadOnlyList<SourceUser> sourceUsers,
        CancellationToken ct);
}
