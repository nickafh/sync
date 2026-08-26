using Microsoft.Graph.Models.ODataErrors;

namespace AFHSync.Worker.Services;

/// <summary>
/// Phase 2 (§2.1): classifies the Graph error returned for an enabled Entra account that has no
/// REST-enabled mailbox (soft-deleted, on-prem/hybrid, unlicensed service accounts). Such a
/// mailbox is UNAVAILABLE, not failed: it is stamped on target_mailboxes, skipped for
/// <see cref="ReprobeInterval"/>, then re-probed — forever, weekly. IsActive is untouched.
/// </summary>
public static class MailboxAvailability
{
    public const string UnavailableErrorCode = "MailboxNotEnabledForRESTAPI";
    public const string UnavailableMessageFragment = "inactive, soft-deleted, or is hosted on-premise";
    public static readonly TimeSpan ReprobeInterval = TimeSpan.FromDays(7);

    public static bool IsUnavailable(Exception ex)
    {
        if (ex is ODataError odata)
        {
            if (string.Equals(odata.Error?.Code, UnavailableErrorCode, StringComparison.OrdinalIgnoreCase))
                return true;
            if (odata.Error?.Message?.Contains(UnavailableMessageFragment, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        return ex.Message.Contains(UnavailableMessageFragment, StringComparison.OrdinalIgnoreCase);
    }
}
