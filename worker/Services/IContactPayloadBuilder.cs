using AFHSync.Shared.Entities;
using System.Collections.Generic;

namespace AFHSync.Worker.Services;

/// <summary>
/// Builds a normalized contact payload from a SourceUser filtered by FieldProfile field settings,
/// and computes a deterministic SHA-256 hash for delta comparison.
/// </summary>
public interface IContactPayloadBuilder
{
    /// <summary>
    /// Builds a normalized contact payload and computes a SHA-256 hash.
    /// </summary>
    /// <param name="source">The source user data from Graph/database.</param>
    /// <param name="fieldSettings">The field profile settings controlling which fields sync and how.</param>
    /// <param name="existingState">The existing sync state (null for new contacts). Used for AddMissing behavior.</param>
    /// <returns>A <see cref="ContactPayloadResult"/> containing the ordered payload and hex hash.</returns>
    ContactPayloadResult BuildPayload(
        SourceUser source,
        IReadOnlyList<FieldProfileField> fieldSettings,
        ContactSyncState? existingState);
}

/// <summary>
/// Immutable result of <see cref="IContactPayloadBuilder.BuildPayload"/>.
/// </summary>
/// <param name="Payload">
/// Sorted dictionary of field name -> string value for fields that should be written to the contact.
/// Keys are sorted by <see cref="StringComparer.Ordinal"/> to ensure consistent serialization.
/// </param>
/// <param name="DataHash">Lowercase hex string of the SHA-256 hash of the serialized payload.</param>
public record ContactPayloadResult(
    SortedDictionary<string, string> Payload,
    string DataHash);
