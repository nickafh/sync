using AFHSync.Shared.Data;
using AFHSync.Shared.Entities;
using AFHSync.Worker.Graph;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph.Models;

namespace AFHSync.Worker.Services;

/// <summary>A Graph contact as seen by the reconciler's Graph seam.</summary>
public sealed record GraphContactStub(string Id, string? DisplayName, string? Email);

/// <summary>
/// Phase 3 (§3.7). For every stray in the folder: compute the deterministic key (primary email
/// lower-cased, else display name lower-cased); if a current source user has that key and no
/// state row yet, ADOPT the contact (state row with the Graph id, data_hash NULL so the next
/// classification PATCHes it into shape); otherwise REMOVE it. Known contacts — including stale
/// ones — are never touched; that is the stale handler's job. A contact is "known" when ANY
/// contact_sync_state row in this mailbox references its Graph id, regardless of which tunnel
/// (or no tunnel, for legacy rows) owns that row — tunnel names are not unique, so two tunnels
/// can share one Graph folder and must not steal or delete each other's contacts.
///
/// Graph listing is a <c>protected virtual</c> seam so unit tests can subclass this class.
/// </summary>
public class FolderReconciler : IFolderReconciler
{
    public const string AdoptedResult = "adopted";

    private readonly GraphClientFactory? _graphClientFactory;
    private readonly IDbContextFactory<AFHSyncDbContext> _dbContextFactory;
    private readonly IContactWriter _contactWriter;
    private readonly ILogger<FolderReconciler> _logger;

    public FolderReconciler(
        GraphClientFactory graphClientFactory,
        IDbContextFactory<AFHSyncDbContext> dbContextFactory,
        IContactWriter contactWriter,
        ILogger<FolderReconciler> logger)
    {
        _graphClientFactory = graphClientFactory;
        _dbContextFactory = dbContextFactory;
        _contactWriter = contactWriter;
        _logger = logger;
    }

    /// <summary>Deterministic identity shared by SourceUser and Graph Contact: email, else display name; trimmed, lower-cased; null when neither is set.</summary>
    public static string? ContactKey(string? email, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(email))
            return email.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName.Trim().ToLowerInvariant();
        return null;
    }

    /// <inheritdoc />
    public async Task<FolderReconcileResult> ReconcileAsync(
        Tunnel tunnel,
        TargetMailbox mailbox,
        string folderId,
        int canonicalPhoneListId,
        IReadOnlyList<SourceUser> sourceUsers,
        CancellationToken ct)
    {
        var graphContacts = await ListFolderContactsAsync(mailbox.EntraId, folderId, ct);

        // Bookkeeping writes use CancellationToken.None: an adopted row must not be lost to a shutdown.
        await using var db = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        var mailboxStates = await db.ContactSyncStates
            .Where(s => s.TargetMailboxId == mailbox.Id)
            .ToListAsync(CancellationToken.None);
        // Known = any state row in this mailbox, from any tunnel (including legacy rows with
        // tunnel_id IS NULL) — two tunnels can share one Graph folder (tunnel names are not
        // unique), so a contact another tunnel owns must never look like a stray here.
        var knownIds = mailboxStates
            .Where(s => !string.IsNullOrEmpty(s.GraphContactId))
            .Select(s => s.GraphContactId!)
            .ToHashSet(StringComparer.Ordinal);
        // Adoption eligibility is still scoped to THIS tunnel: a user with a state row under a
        // different tunnel may still need one adopted for this tunnel's own folder.
        var usersWithState = mailboxStates
            .Where(s => s.TunnelId == tunnel.Id)
            .Select(s => s.SourceUserId)
            .ToHashSet();

        var usersByKey = new Dictionary<string, SourceUser>(StringComparer.Ordinal);
        foreach (var user in sourceUsers)
        {
            var key = ContactKey(user.Email, user.DisplayName);
            if (key is not null)
                usersByKey.TryAdd(key, user);
        }

        var toRemove = new List<(string key, string graphContactId)>();
        var adopted = 0;
        var now = DateTime.UtcNow;
        foreach (var contact in graphContacts)
        {
            if (knownIds.Contains(contact.Id))
                continue;

            var key = ContactKey(contact.Email, contact.DisplayName);
            if (key is not null && usersByKey.TryGetValue(key, out var user) && !usersWithState.Contains(user.Id))
            {
                db.ContactSyncStates.Add(new ContactSyncState
                {
                    SourceUserId = user.Id,
                    PhoneListId = canonicalPhoneListId,
                    TargetMailboxId = mailbox.Id,
                    TunnelId = tunnel.Id,
                    GraphContactId = contact.Id,
                    DataHash = null,
                    LastSyncedAt = now,
                    LastResult = AdoptedResult,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                usersWithState.Add(user.Id);
                adopted++;
                _logger.LogInformation(
                    "Reconcile: adopted stray Graph contact {ContactId} ({Key}) for SourceUserId={SourceUserId} in mailbox {Email}",
                    contact.Id, key, user.Id, mailbox.Email);
            }
            else
            {
                toRemove.Add((contact.Id, contact.Id));
            }
        }

        if (adopted > 0)
            await db.SaveChangesAsync(CancellationToken.None);

        var removed = 0;
        if (toRemove.Count > 0)
        {
            var results = await _contactWriter.DeleteContactsBatchAsync(mailbox.EntraId, toRemove, ct);
            foreach (var (key, _) in toRemove)
            {
                if (results.TryGetValue(key, out var r) && (r.Success || r.NotFound))
                    removed++;
                else
                    _logger.LogWarning("Reconcile: could not remove stray Graph contact {ContactId} in mailbox {Email}: {Error}",
                        key, mailbox.Email, r?.Error ?? "no result");
            }
        }

        _logger.LogInformation(
            "Reconcile: tunnel {TunnelName} / mailbox {Email}: {Examined} Graph contact(s), {Adopted} adopted, {Removed} removed",
            tunnel.Name, mailbox.Email, graphContacts.Count, adopted, removed);

        return new FolderReconcileResult(graphContacts.Count, adopted, removed);
    }

    /// <summary>GET /users/{mailbox}/contactFolders/{id}/contacts (id, displayName, emailAddresses), all pages.</summary>
    protected virtual async Task<List<GraphContactStub>> ListFolderContactsAsync(string mailboxEntraId, string folderId, CancellationToken ct)
    {
        var client = _graphClientFactory?.Client
            ?? throw new InvalidOperationException("GraphClientFactory is required for Graph operations");

        var contacts = new List<GraphContactStub>();
        var response = await client.Users[mailboxEntraId].ContactFolders[folderId].Contacts.GetAsync(config =>
        {
            config.QueryParameters.Select = ["id", "displayName", "emailAddresses"];
            config.QueryParameters.Top = 999;
        }, ct);

        if (response?.Value is null)
            return contacts;

        var iterator = Microsoft.Graph.PageIterator<Contact, ContactCollectionResponse>
            .CreatePageIterator(client, response, c =>
            {
                if (c.Id is not null)
                    contacts.Add(new GraphContactStub(c.Id, c.DisplayName, c.EmailAddresses?.FirstOrDefault()?.Address));
                return true;
            });
        await iterator.IterateAsync(ct);
        return contacts;
    }
}
