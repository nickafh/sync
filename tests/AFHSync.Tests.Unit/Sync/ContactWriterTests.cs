using System.Net;
using System.Text;
using System.Text.Json;
using AFHSync.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using GraphClientFactory = AFHSync.Worker.Graph.GraphClientFactory;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Tests for ContactWriter payload mapping logic.
/// ContactWriter.MapPayloadToContact is public static so it can be tested without
/// Graph SDK mocking — tests verify field mapping correctness in isolation.
/// </summary>
public class ContactWriterTests
{
    // ── Test 1: Core scalar fields mapped correctly ──────────────────────────

    [Fact]
    public void MapPayloadToContact_MapsGivenName_Surname_DisplayName_Correctly()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["GivenName"] = "John",
            ["Surname"] = "Smith",
            ["DisplayName"] = "John Smith"
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: true);

        Assert.Equal("John", contact.GivenName);
        Assert.Equal("Smith", contact.Surname);
        Assert.Equal("John Smith", contact.DisplayName);
    }

    // ── Test 2: EmailAddresses mapped to List<EmailAddress> ─────────────────

    [Fact]
    public void MapPayloadToContact_Maps_EmailAddresses_To_EmailAddressList()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Jane Doe",
            ["EmailAddresses"] = "jane.doe@atlantafinehomes.com"
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: true);

        Assert.NotNull(contact.EmailAddresses);
        Assert.Single(contact.EmailAddresses);
        Assert.Equal("jane.doe@atlantafinehomes.com", contact.EmailAddresses[0].Address);
    }

    // ── Test 3: BusinessPhones mapped to List<string> ────────────────────────

    [Fact]
    public void MapPayloadToContact_Maps_BusinessPhones_To_StringList()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Bob Brown",
            ["BusinessPhones"] = "+1 404-555-1234"
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: true);

        Assert.NotNull(contact.BusinessPhones);
        Assert.Single(contact.BusinessPhones);
        Assert.Equal("+1 404-555-1234", contact.BusinessPhones[0]);
    }

    // ── Test 4: Business address fields mapped to PhysicalAddress ─────────────

    [Fact]
    public void MapPayloadToContact_Maps_BusinessAddress_Fields_To_PhysicalAddress()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Alice Green",
            ["BusinessStreet"] = "3290 Northside Parkway NW",
            ["BusinessCity"] = "Atlanta",
            ["BusinessState"] = "GA",
            ["BusinessPostalCode"] = "30327"
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: true);

        Assert.NotNull(contact.BusinessAddress);
        Assert.Equal("3290 Northside Parkway NW", contact.BusinessAddress.Street);
        Assert.Equal("Atlanta", contact.BusinessAddress.City);
        Assert.Equal("GA", contact.BusinessAddress.State);
        Assert.Equal("30327", contact.BusinessAddress.PostalCode);
    }

    // ── Test 5: Optional fields mapped when present ──────────────────────────

    [Fact]
    public void MapPayloadToContact_Maps_OptionalFields_When_Present()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Carol White",
            ["JobTitle"] = "Advisor",
            ["CompanyName"] = "Atlanta Fine Homes",
            ["Department"] = "Buckhead",
            ["OfficeLocation"] = "Buckhead",
            ["MobilePhone"] = "+1 404-555-9999",
            ["PersonalNotes"] = "Test notes"
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: true);

        Assert.Equal("Advisor", contact.JobTitle);
        Assert.Equal("Atlanta Fine Homes", contact.CompanyName);
        Assert.Equal("Buckhead", contact.Department);
        Assert.Equal("Buckhead", contact.OfficeLocation);
        Assert.Equal("+1 404-555-9999", contact.MobilePhone);
        // PersonalNotes is prefixed with the office line (iOS has no dedicated office field).
        Assert.Equal("Office: Buckhead\nTest notes", contact.PersonalNotes);
    }

    // ── Test 6: Missing fields do not set properties (no exceptions) ──────────

    [Fact]
    public void MapPayloadToContact_Empty_Payload_Returns_Valid_Contact_With_No_Fields()
    {
        var payload = new SortedDictionary<string, string>();

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: true);

        // Must not throw — returns a valid (mostly-empty) Contact object
        Assert.NotNull(contact);
        Assert.Null(contact.GivenName);
        Assert.Null(contact.BusinessAddress);
        Assert.Null(contact.EmailAddresses);
        Assert.Null(contact.BusinessPhones);
    }

    // ── Phase 2 (2.2): a 2xx batch step without an id is NOT a success ───────

    [Fact]
    public void MapCreateResponse_NullOrIdLessContact_IsFailureWithNoIdError()
    {
        var fromNull = ContactWriter.MapCreateResponse(null);
        Assert.False(fromNull.Success);
        Assert.Equal("no contact id in response", fromNull.Error);

        var fromIdLess = ContactWriter.MapCreateResponse(new Contact { Id = null });
        Assert.False(fromIdLess.Success);
        Assert.Equal(ContactWriter.NoContactIdError, fromIdLess.Error);
    }

    [Fact]
    public void MapCreateResponse_ContactWithId_IsSuccess()
    {
        var result = ContactWriter.MapCreateResponse(new Contact { Id = "AAMkAG-abc" });

        Assert.True(result.Success);
        Assert.Equal("AAMkAG-abc", result.GraphContactId);
    }

    // ── Phase 2 (2.8): notes are written only when in the payload or on create ──

    [Fact]
    public void MapPayloadToContact_Update_WithoutNotesKey_LeavesPersonalNotesNull_EvenWithOffice()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Jane Doe",
            ["OfficeLocation"] = "Buckhead"
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: false);

        Assert.Equal("Buckhead", contact.OfficeLocation);
        Assert.Null(contact.PersonalNotes);   // phone-side notes survive an AddMissing update
    }

    [Fact]
    public void MapPayloadToContact_Create_WithOfficeAndNoNotes_SetsOfficePrefix()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Jane Doe",
            ["OfficeLocation"] = "Buckhead"
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: true);

        Assert.Equal("Office: Buckhead", contact.PersonalNotes);
    }

    [Fact]
    public void MapPayloadToContact_Update_WithNotesKey_PrefixesOffice()
    {
        var payload = new SortedDictionary<string, string>
        {
            ["DisplayName"] = "Jane Doe",
            ["OfficeLocation"] = "Buckhead",
            ["PersonalNotes"] = "Team lead"
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: false);

        Assert.Equal("Office: Buckhead\nTeam lead", contact.PersonalNotes);
    }

    [Fact]
    public void MapPayloadToContact_Update_WithEmptyNotesKey_ClearsToPrefixOnly()
    {
        // Nosync on an existing contact sends an explicit empty string to clear the field.
        var payload = new SortedDictionary<string, string>
        {
            ["OfficeLocation"] = "Buckhead",
            ["PersonalNotes"] = ""
        };

        var contact = ContactWriter.MapPayloadToContact(payload, isCreate: false);

        Assert.Equal("Office: Buckhead", contact.PersonalNotes);
    }

    // ── Important #1(a): shutdown cancellation must not become a per-key "failed" result ─────
    //
    // These build a REAL ContactWriter backed by a REAL GraphServiceClient whose HTTP transport
    // is a fake HttpMessageHandler (no network, no credential — see GraphClientFactory's
    // internal test-only constructor) so the production chunk-loop / retry code in
    // ExecuteBatchWithRetryAsync runs end to end, not a hand-written test double.

    [Fact]
    public async Task CreateContactsBatchAsync_TokenAlreadyCancelled_PropagatesOce_NotPerKeyFailure()
    {
        var (writer, handler) = BuildWriterWithFakeGraphTransport();
        var ops = new List<(string key, SortedDictionary<string, string> payload)>
        {
            ("k1", new SortedDictionary<string, string> { ["DisplayName"] = "A" }),
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => writer.CreateContactsBatchAsync("mbx1", "folder1", ops, onChunkCompleted: null, cts.Token));

        Assert.Equal(0, handler.CallCount); // no HTTP call — never posted, never swallowed
    }

    [Fact]
    public async Task UpdateContactsBatchAsync_TokenAlreadyCancelled_PropagatesOce_NotPerKeyFailure()
    {
        var (writer, handler) = BuildWriterWithFakeGraphTransport();
        var ops = new List<(string key, string graphContactId, SortedDictionary<string, string> payload)>
        {
            ("k1", "graph-1", new SortedDictionary<string, string> { ["DisplayName"] = "A" }),
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => writer.UpdateContactsBatchAsync("mbx1", ops, onChunkCompleted: null, cts.Token));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task DeleteContactsBatchAsync_TokenAlreadyCancelled_PropagatesOce_NotPerKeyFailure()
    {
        var (writer, handler) = BuildWriterWithFakeGraphTransport();
        var ops = new List<(string key, string graphContactId)> { ("k1", "graph-1") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => writer.DeleteContactsBatchAsync("mbx1", ops, cts.Token));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CreateContactsBatchAsync_TokenCancelledBetweenChunks_FirstChunkPersists_SecondNeverSent()
    {
        CancellationTokenSource? cts = null;
        var (writer, handler) = BuildWriterWithFakeGraphTransport(onBatchHandled: () => cts!.Cancel());
        cts = new CancellationTokenSource();

        // 25 ops => chunk 1 = 20 (MaxBatchSize), chunk 2 = 5. The fake transport cancels the
        // token as chunk 1's response comes back — simulating the Hangfire shutdown token
        // firing while that HTTP round trip was already in flight.
        var ops = Enumerable.Range(0, 25)
            .Select(i => ($"k{i}", new SortedDictionary<string, string> { ["DisplayName"] = $"Contact {i}" }))
            .ToList();

        var chunkResults = new List<IReadOnlyDictionary<string, BatchOperationResult>>();
        Task OnChunkCompleted(IReadOnlyDictionary<string, BatchOperationResult> results)
        {
            chunkResults.Add(results);
            return Task.CompletedTask;
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => writer.CreateContactsBatchAsync("mbx1", "folder1", ops, OnChunkCompleted, cts.Token));

        Assert.Equal(1, handler.CallCount); // chunk 2 never reached the transport
        var chunk1 = Assert.Single(chunkResults); // onChunkCompleted ran exactly once, for chunk 1
        Assert.Equal(20, chunk1.Count);
        Assert.All(chunk1.Values, r => Assert.True(r.Success)); // no per-key "canceled" failures
    }

    // ── Phase 3 (3.7): a transport failure means Graph MAY have applied the chunk ──────────────

    [Fact]
    public async Task CreateContactsBatchAsync_TransportThrows_MarksEveryKeyOutcomeUnknown()
    {
        var (writer, handler) = BuildWriterWithFakeGraphTransport(throwOnSend: new HttpRequestException("connection reset"));
        var ops = new List<(string key, SortedDictionary<string, string> payload)>
        {
            ("k1", new SortedDictionary<string, string> { ["DisplayName"] = "A" }),
            ("k2", new SortedDictionary<string, string> { ["DisplayName"] = "B" }),
        };

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", ops, onChunkCompleted: null, CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(2, results.Count);
        Assert.All(results.Values, r =>
        {
            Assert.False(r.Success);
            Assert.True(r.OutcomeUnknown);
            Assert.Contains("connection reset", r.Error);
        });
    }

    [Fact]
    public async Task CreateContactsBatchAsync_StepFailsWithStatus_IsNotOutcomeUnknown()
    {
        var (writer, _) = BuildWriterWithFakeGraphTransport();
        var ops = new List<(string key, SortedDictionary<string, string> payload)>
        {
            ("k1", new SortedDictionary<string, string> { ["DisplayName"] = "A" }),
        };

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", ops, onChunkCompleted: null, CancellationToken.None);

        Assert.True(results["k1"].Success);
        Assert.False(results["k1"].OutcomeUnknown);   // a definite answer from Graph is never "unknown"
    }

    // ── Fake Graph SDK transport (no network, no credential) ─────────────────────────────────

    private static (ContactWriter writer, FakeBatchHandler handler) BuildWriterWithFakeGraphTransport(
        Action? onBatchHandled = null, Exception? throwOnSend = null)
    {
        var handler = new FakeBatchHandler(onBatchHandled, throwOnSend);
        var httpClient = new HttpClient(handler);
        var client = new GraphServiceClient(httpClient, new NoOpAuthenticationProvider());
        var factory = new GraphClientFactory(client);
        var writer = new ContactWriter(factory, NullLogger<ContactWriter>.Instance);
        return (writer, handler);
    }

    private sealed class NoOpAuthenticationProvider : IAuthenticationProvider
    {
        public Task AuthenticateRequestAsync(
            RequestInformation request,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Fake $batch transport: echoes a 201-with-id success for every step in the incoming
    /// request, so ContactWriter's real batch/retry code runs end to end without hitting Graph.
    /// </summary>
    private sealed class FakeBatchHandler(Action? onBatchHandled, Exception? throwOnSend = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (throwOnSend is not null)
                throw throwOnSend;
            var bodyStr = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(bodyStr!);
            var ids = doc.RootElement.GetProperty("requests").EnumerateArray()
                .Select(r => r.GetProperty("id").GetString()!)
                .ToList();

            var responses = ids.Select((id, i) =>
                new { id, status = 201, body = new { id = $"graph-contact-{CallCount}-{i}" } });
            var json = JsonSerializer.Serialize(new { responses });

            onBatchHandled?.Invoke();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
