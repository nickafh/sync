using System.Text.Json;

namespace AFHSync.Api.Services;

/// <summary>
/// Phase 3 (§3.2): the target-scope rules the wizard enforces, applied on the server so the edit
/// page (or any client) cannot save a tunnel that is scoped to nobody. Rules, in order:
/// a present-but-blank group id; both scopes at once; emails that are not a JSON string array;
/// an emails array with no non-blank entry.
/// </summary>
public static class TargetScopeValidation
{
    public const string EmptyUsersMessage = "Select at least one user, or switch scope to All Users.";
    public const string EmptyGroupMessage = "Select a security group, or switch scope to All Users.";
    public const string BothScopesMessage = "A tunnel can be scoped to specific users or to a security group, not both.";
    public const string InvalidEmailsJsonMessage = "targetUserEmails must be a JSON array of email addresses.";

    /// <summary>Returns the error message for an invalid combination, or null when it is valid.</summary>
    public static string? Validate(string? targetUserEmails, string? targetGroupId)
    {
        var hasGroup = targetGroupId is not null;
        var hasEmails = targetUserEmails is not null;

        if (hasGroup && string.IsNullOrWhiteSpace(targetGroupId))
            return EmptyGroupMessage;
        if (hasGroup && hasEmails)
            return BothScopesMessage;
        if (!hasEmails)
            return null;

        string?[] emails;
        try
        {
            emails = JsonSerializer.Deserialize<string?[]>(targetUserEmails!) ?? [];
        }
        catch (JsonException)
        {
            return InvalidEmailsJsonMessage;
        }

        return emails.All(string.IsNullOrWhiteSpace) ? EmptyUsersMessage : null;
    }
}
