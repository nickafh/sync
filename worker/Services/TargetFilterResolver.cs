using System.Text.Json;
using AFHSync.Api.Services;
using Microsoft.Extensions.Logging;

namespace AFHSync.Worker.Services;

/// <summary>
/// Pure parser/resolver for PhoneList.TargetUserFilter JSON. Extracted from SyncEngine
/// to give the parsing + DDG-union logic a clean unit-test seam (mirrors the Phase 02
/// ApplySourceFilters / MapGraphUserToSourceUser extraction pattern).
///
/// JSON shape (per quick-260417-2lb):
///   { "emails": ["a@x.com", ...], "ddgs": [{"id":"...","displayName":"..."}, ...] }
///
/// Both keys are optional. Missing or null = empty list. Old rows with only `emails`
/// continue to work unchanged.
///
/// The Graph query itself is parameterised as a delegate (ddgIdToEmails) so this
/// resolver can be unit-tested without mocking GraphServiceClient.
/// </summary>
internal static class TargetFilterResolver
{
    /// <summary>
    /// Maximum number of DDGs accepted per filter — defence in depth against
    /// pathological JSON (T-2lb-01, T-2lb-03 in the threat register).
    /// </summary>
    internal const int MaxDdgs = 50;

    /// <summary>
    /// Parses the targetUserFilter JSON, calls the supplied DDG resolver for each DDG entry,
    /// and returns the union of explicit emails + all resolved DDG members
    /// (case-insensitive dedupe).
    /// </summary>
    /// <param name="targetUserFilterJson">The raw JSON string from phone_lists.target_user_filter (may be null/empty).</param>
    /// <param name="ddgResolver">Used to translate a DDG id into a DdgInfo (name + RecipientFilter).</param>
    /// <param name="filterConverter">Used to convert the OPATH RecipientFilter into a Graph $filter.</param>
    /// <param name="ddgMembersFromGraphFilter">
    /// Delegate that takes a converted Graph $filter and returns all matching member emails.
    /// Implemented in production by SyncEngine using GraphServiceClient. Tests inject a stub.
    /// </param>
    /// <param name="logger">For warnings / info.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<HashSet<string>> ResolveAsync(
        string? targetUserFilterJson,
        IDDGResolver ddgResolver,
        IFilterConverter filterConverter,
        Func<string, CancellationToken, Task<List<string>>> ddgMembersFromGraphFilter,
        ILogger logger,
        CancellationToken ct)
    {
        var plEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(targetUserFilterJson))
            return plEmails;

        JsonElement filterData;
        try
        {
            filterData = JsonSerializer.Deserialize<JsonElement>(targetUserFilterJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse targetUserFilter JSON; treating as empty");
            return plEmails;
        }

        if (filterData.ValueKind != JsonValueKind.Object)
            return plEmails;

        // 1) Explicit emails (back-compat path; behaviour unchanged from pre-2lb shape).
        if (filterData.TryGetProperty("emails", out var emailsArr) && emailsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in emailsArr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var email = el.GetString();
                    if (!string.IsNullOrWhiteSpace(email))
                        plEmails.Add(email.Trim());
                }
            }
        }

        // 2) DDG-resolved members (new for 2lb).
        if (filterData.TryGetProperty("ddgs", out var ddgsArr) && ddgsArr.ValueKind == JsonValueKind.Array)
        {
            int processed = 0;
            foreach (var ddgEl in ddgsArr.EnumerateArray())
            {
                if (processed >= MaxDdgs)
                {
                    logger.LogWarning(
                        "DDG list in targetUserFilter exceeds {Max}; remaining entries skipped (T-2lb-03)",
                        MaxDdgs);
                    break;
                }
                processed++;

                if (ddgEl.ValueKind != JsonValueKind.Object) continue;

                string? id = ddgEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                string? displayName = ddgEl.TryGetProperty("displayName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(id))
                {
                    logger.LogWarning("DDG entry in targetUserFilter is missing an id; skipping");
                    continue;
                }

                try
                {
                    var ddgInfo = await ddgResolver.GetDdgAsync(id, ct);
                    if (ddgInfo == null)
                    {
                        logger.LogWarning(
                            "DDG target {Id} ({Name}) not found at sync time, skipping",
                            id, displayName);
                        continue;
                    }

                    var conversion = filterConverter.Convert(ddgInfo.RecipientFilter);
                    if (!conversion.Success || string.IsNullOrWhiteSpace(conversion.Filter))
                    {
                        logger.LogWarning(
                            "DDG target {Id} ({Name}) RecipientFilter could not be converted to a Graph filter ({Warning}); skipping",
                            id, displayName, conversion.Warning ?? "unknown");
                        continue;
                    }

                    var members = await ddgMembersFromGraphFilter(conversion.Filter, ct);
                    int added = 0;
                    foreach (var email in members)
                    {
                        if (!string.IsNullOrWhiteSpace(email) && plEmails.Add(email.Trim()))
                            added++;
                    }

                    if (members.Count == 0)
                    {
                        logger.LogWarning(
                            "DDG target {Id} ({Name}) resolved to 0 members, contributing nothing",
                            id, displayName);
                    }
                    else
                    {
                        logger.LogInformation(
                            "DDG target {Name} resolved to {Count} member(s) ({Added} new after dedupe)",
                            displayName ?? id, members.Count, added);
                    }
                }
                catch (Exception ex)
                {
                    // Per CONTEXT.md "Empty resolution" rule: one bad DDG must not fail the whole sync.
                    logger.LogWarning(ex,
                        "DDG target {Id} ({Name}) resolution threw; skipping",
                        id, displayName);
                }
            }
        }

        return plEmails;
    }
}
