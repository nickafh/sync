using AFHSync.Shared.Entities;

namespace AFHSync.Worker.Services;

/// <param name="Examined">Graph contacts found in the folder.</param>
/// <param name="Adopted">Strays matched to a current source user and given a state row.</param>
/// <param name="Removed">Strays deleted from Graph.</param>
/// <param name="MissingSourceUserIds">§5.2: source users whose row referenced a contact that is no longer in the folder; the row was dropped so classification recreates the contact.</param>
public sealed record FolderReconcileResult(int Examined, int Adopted, int Removed, IReadOnlyList<int> MissingSourceUserIds)
{
    public FolderReconcileResult(int Examined, int Adopted, int Removed)
        : this(Examined, Adopted, Removed, Array.Empty<int>()) { }

    public int Missing => MissingSourceUserIds.Count;

    public bool Equals(FolderReconcileResult? other) =>
        other is not null
        && Examined == other.Examined
        && Adopted == other.Adopted
        && Removed == other.Removed
        && MissingSourceUserIds.SequenceEqual(other.MissingSourceUserIds);

    public override int GetHashCode() => HashCode.Combine(Examined, Adopted, Removed, MissingSourceUserIds.Count);
}

/// <summary>
/// Phase 3 (§3.7) and §5.2: reconciles a tunnel's contact folder in one mailbox against
/// contact_sync_state in both directions. A "stray" is a Graph contact whose id no state row references
/// (adopted or removed); a "missing" row is one of this tunnel's rows whose contact is no longer in the
/// folder (dropped so the classification that follows recreates the contact).
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
