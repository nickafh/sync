using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AFHSync.Shared.Entities;
using AFHSync.Shared.Enums;

namespace AFHSync.Worker.Services;

/// <summary>
/// Builds normalized contact payloads from SourceUser fields filtered by FieldProfileField behaviors,
/// and computes deterministic SHA-256 hashes for delta comparison.
///
/// Per D-04: reflective field mapper uses a static dictionary of FieldName -> SourceUser accessor.
/// Per D-05: SHA-256 hash computed from JSON serialization of SortedDictionary (sorted keys, null exclusion).
/// Per D-06: Nosync excludes; AddMissing includes for new contacts only; Always always includes; RemoveBlank clears empty.
/// </summary>
public class ContactPayloadBuilder : IContactPayloadBuilder
{
    /// <summary>
    /// Maps Graph/field profile field names to SourceUser property accessors.
    /// These names match the FieldProfileField.FieldName values stored in the database.
    /// </summary>
    private static readonly Dictionary<string, Func<SourceUser, string?>> FieldAccessors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = s => s.DisplayName,
            ["GivenName"] = s => s.FirstName,
            ["Surname"] = s => s.LastName,
            ["JobTitle"] = s => s.JobTitle,
            ["CompanyName"] = s => s.CompanyName,
            ["EmailAddresses"] = s => s.Email,
            ["BusinessPhones"] = s => s.BusinessPhone,
            ["MobilePhone"] = s => s.MobilePhone,
            ["OfficeLocation"] = s => s.OfficeLocation,
            ["Department"] = s => s.Department,
            ["BusinessStreet"] = s => s.StreetAddress,
            ["BusinessCity"] = s => s.City,
            ["BusinessState"] = s => s.State,
            ["BusinessPostalCode"] = s => s.PostalCode,
            ["PersonalNotes"] = s => s.Notes
        };

    /// <summary>
    /// Builds a normalized contact payload and computes a deterministic SHA-256 hash.
    ///
    /// Behavior per field:
    /// - Nosync: field is excluded entirely from payload and hash
    /// - Always: field included with trimmed value if non-null and non-empty
    /// - AddMissing: field included only when existingState is null (new contact);
    ///   excluded when existingState exists (preserve existing contact value)
    /// - RemoveBlank: if source value is null/empty/whitespace, adds empty string "";
    ///   if source value is present, adds trimmed value
    /// </summary>
    public ContactPayloadResult BuildPayload(
        SourceUser source,
        IReadOnlyList<FieldProfileField> fieldSettings,
        ContactSyncState? existingState)
    {
        var payload = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in fieldSettings)
        {
            switch (field.Behavior)
            {
                case SyncBehavior.Nosync:
                {
                    // For existing contacts: include as empty string to clear the field in Graph.
                    // Graph PATCH ignores omitted fields, so we must explicitly send empty string.
                    // For new contacts: exclude entirely (no need to clear what doesn't exist).
                    if (existingState is not null)
                    {
                        payload[field.FieldName] = string.Empty;
                    }
                    break;
                }

                case SyncBehavior.Always:
                {
                    var value = GetFieldValue(source, field.FieldName);
                    if (value is not null)
                    {
                        payload[field.FieldName] = value;
                    }
                    break;
                }

                case SyncBehavior.AddMissing:
                {
                    // Include only for new contacts (no existing sync state).
                    // When a contact already exists, the existing value is preserved.
                    if (existingState is null)
                    {
                        var value = GetFieldValue(source, field.FieldName);
                        if (value is not null)
                        {
                            payload[field.FieldName] = value;
                        }
                    }
                    break;
                }

                case SyncBehavior.RemoveBlank:
                {
                    var rawValue = GetRawFieldValue(source, field.FieldName);
                    if (string.IsNullOrWhiteSpace(rawValue))
                    {
                        // Explicit empty string signals target field should be cleared
                        payload[field.FieldName] = string.Empty;
                    }
                    else
                    {
                        payload[field.FieldName] = rawValue.Trim();
                    }
                    break;
                }
            }
        }

        var hash = ComputeHash(payload);
        return new ContactPayloadResult(payload, hash);
    }

    /// <summary>
    /// Gets the trimmed, non-null-or-empty field value for a given field name.
    /// Returns null if the field is unknown, null, or whitespace-only.
    /// Used by Always and AddMissing behaviors (which exclude null/empty).
    /// </summary>
    private static string? GetFieldValue(SourceUser source, string fieldName)
    {
        if (!FieldAccessors.TryGetValue(fieldName, out var accessor))
            return null;

        var raw = accessor(source);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim();
    }

    /// <summary>
    /// Gets the raw field value for a given field name.
    /// Returns null if the field is unknown or the accessor returns null.
    /// Used by RemoveBlank behavior which needs to distinguish null from empty string.
    /// </summary>
    private static string? GetRawFieldValue(SourceUser source, string fieldName)
    {
        if (!FieldAccessors.TryGetValue(fieldName, out var accessor))
            return null;

        return accessor(source);
    }

    /// <summary>
    /// Computes a deterministic SHA-256 hash of the payload.
    ///
    /// Determinism is guaranteed by:
    /// 1. SortedDictionary with StringComparer.Ordinal — keys always in alphabetical order
    /// 2. System.Text.Json serialization — consistent output for same key-value pairs
    /// 3. UTF-8 encoding before hashing — byte-level consistency
    ///
    /// Per D-05 and Pattern 5 (RESEARCH.md):
    /// - Null fields are excluded (SortedDictionary never contains null values)
    /// - Empty string is only present for RemoveBlank fields when source is empty
    /// - Hash is lowercase hex per convention
    /// </summary>
    private static string ComputeHash(SortedDictionary<string, string> payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
