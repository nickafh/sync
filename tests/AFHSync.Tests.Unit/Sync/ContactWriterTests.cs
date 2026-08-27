using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // ── Phase 4 (4.2): throttled / timed-out steps are re-posted, honouring Retry-After ──────

    private static List<(string key, SortedDictionary<string, string> payload)> Ops(params string[] keys)
        => keys.Select(k => (k, new SortedDictionary<string, string> { ["DisplayName"] = k })).ToList();

    [Fact]
    public async Task CreateContactsBatchAsync_RetriesOnlyThrottledSteps_HonoursRetryAfter_CountsThrottle()
    {
        var throttle = new ThrottleCounter();
        var delays = new List<TimeSpan>();
        // The script needs the second step's id, which the SDK assigns — read it from the recorded
        // request (the fake records ids before it consults the script) instead of guessing the format.
        FakeBatchHandler? h = null;
        var (writer, handler) = BuildWriterWithFakeGraphTransport(
            script: (call, stepId) => call == 1 && stepId == h!.RequestStepIds[0][1]
                ? new FakeStep(429, RetryAfter: "7")      // first call: the second step is throttled
                : new FakeStep(201),
            throttle: throttle, delays: delays);
        h = handler;

        var chunkResults = new List<IReadOnlyDictionary<string, BatchOperationResult>>();
        Task OnChunkCompleted(IReadOnlyDictionary<string, BatchOperationResult> results)
        {
            chunkResults.Add(results);
            return Task.CompletedTask;
        }

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", Ops("k1", "k2", "k3"), OnChunkCompleted, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(3, handler.RequestStepIds[0].Count);
        Assert.Single(handler.RequestStepIds[1]);                               // only the throttled step was re-posted
        // step ids are reassigned per post; the mapping back to k2 is proven by the id below
        Assert.All(results.Values, r => Assert.True(r.Success));
        Assert.Equal("graph-contact-2-0", results["k2"].GraphContactId);        // k2's id came from the retry
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, delays);
        Assert.Equal(1, throttle.Count);

        // The chunk callback fires once for the whole chunk, after retries settle — proving
        // post-retry results reach the persistence seam, not just the returned dictionary.
        var chunk = Assert.Single(chunkResults);
        Assert.Equal(3, chunk.Count);
        Assert.All(chunk.Values, r => Assert.True(r.Success));
        Assert.Equal("graph-contact-2-0", chunk["k2"].GraphContactId);
    }

    [Fact]
    public async Task CreateContactsBatchAsync_GivesUpAfterThreeRetries_KeepsHttpStatusFailure()
    {
        var throttle = new ThrottleCounter();
        var delays = new List<TimeSpan>();
        var (writer, handler) = BuildWriterWithFakeGraphTransport(
            script: (_, _) => new FakeStep(503),                                  // never recovers, no Retry-After
            throttle: throttle, delays: delays);

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", Ops("k1"), onChunkCompleted: null, CancellationToken.None);

        Assert.Equal(4, handler.CallCount);                                      // 1 + 3 retries
        Assert.False(results["k1"].Success);
        Assert.Equal("HTTP 503", results["k1"].Error);
        Assert.False(results["k1"].OutcomeUnknown);                              // Graph answered every time
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6) }, delays);
        Assert.Equal(3, throttle.Count);
    }

    [Fact]
    public async Task CreateContactsBatchAsync_TokenCancelledBeforeRetry_AbandonsRetriesWithDefinitiveFailure()
    {
        var throttle = new ThrottleCounter();
        var delays = new List<TimeSpan>();
        using var cts = new CancellationTokenSource();
        var (writer, handler) = BuildWriterWithFakeGraphTransport(
            script: (call, _) => { cts.Cancel(); return new FakeStep(429); },   // cancel while the first post is being answered
            throttle: throttle, delays: delays);

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", Ops("k1"), onChunkCompleted: null, cts.Token);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal("HTTP 429", results["k1"].Error);
        Assert.False(results["k1"].OutcomeUnknown);
        Assert.False(results["k1"].Success);
        Assert.Empty(delays);
        Assert.Equal(1, throttle.Count);
    }

    [Fact]
    public async Task CreateContactsBatchAsync_DoesNotRetryNonTransientStatus()
    {
        var throttle = new ThrottleCounter();
        var delays = new List<TimeSpan>();
        var (writer, handler) = BuildWriterWithFakeGraphTransport(
            script: (_, _) => new FakeStep(400), throttle: throttle, delays: delays);

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", Ops("k1"), onChunkCompleted: null, CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal("HTTP 400", results["k1"].Error);
        Assert.Empty(delays);
        Assert.Equal(0, throttle.Count);
    }

    [Fact]
    public async Task UpdateContactsBatchAsync_RetriesGatewayTimeout()
    {
        var throttle = new ThrottleCounter();
        var (writer, handler) = BuildWriterWithFakeGraphTransport(
            script: (call, _) => call == 1 ? new FakeStep(504) : new FakeStep(200), throttle: throttle, delays: []);
        var ops = new List<(string key, string graphContactId, SortedDictionary<string, string> payload)>
        {
            ("k1", "graph-1", new SortedDictionary<string, string> { ["DisplayName"] = "A" }),
        };

        var results = await writer.UpdateContactsBatchAsync("mbx1", ops, onChunkCompleted: null, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.True(results["k1"].Success);
        Assert.Equal(1, throttle.Count);
    }

    [Fact]
    public async Task CreateContactsBatchAsync_TransportThrowsOnRetry_MarksOnlyRetriedStepsUnknown()
    {
        FakeBatchHandler? h = null;
        var (writer, handler) = BuildWriterWithFakeGraphTransport(
            script: (call, stepId) => call == 1 && stepId == h!.RequestStepIds[0][1] ? new FakeStep(429) : new FakeStep(201),
            throwOnSend: new HttpRequestException("connection reset"),
            throwOnCallNumber: 2,
            delays: []);
        h = handler;

        var results = await writer.CreateContactsBatchAsync("mbx1", "folder1", Ops("k1", "k2"), onChunkCompleted: null, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.True(results["k1"].Success);                                      // answered definitively on call 1
        Assert.False(results["k1"].OutcomeUnknown);
        Assert.False(results["k2"].Success);
        Assert.True(results["k2"].OutcomeUnknown);                               // the retry post never got an answer
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(400, false)]
    [InlineData(404, false)]
    [InlineData(500, false)]
    [InlineData(502, false)]
    public void IsRetryableStepStatus_Is429_503_504_Only(int status, bool expected)
        => Assert.Equal(expected, ContactWriter.IsRetryableStepStatus((HttpStatusCode)status));

    [Theory]
    [InlineData(1, null, 2)]
    [InlineData(2, null, 4)]
    [InlineData(3, null, 6)]
    [InlineData(1, 7, 7)]
    [InlineData(2, 600, 300)]     // clamped to 5 minutes
    [InlineData(1, -3, 0)]        // a Retry-After date already in the past ⇒ no wait
    public void RetryDelayFor_UsesRetryAfterElseLinearBackoff(int attempt, int? retryAfterSeconds, int expectedSeconds)
    {
        var retryAfter = retryAfterSeconds is int s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ContactWriter.RetryDelayFor(attempt, retryAfter));
    }

    // ── Fake Graph SDK transport (no network, no credential) ─────────────────────────────────

    /// <summary>What the fake transport answers for one batch step. Status 201/200 ⇒ a body with an id.</summary>
    private sealed record FakeStep(int Status, string? RetryAfter = null);

    private static (ContactWriter writer, FakeBatchHandler handler) BuildWriterWithFakeGraphTransport(
        Action? onBatchHandled = null,
        Exception? throwOnSend = null,
        int throwOnCallNumber = 1,
        Func<int, string, FakeStep>? script = null,
        ThrottleCounter? throttle = null,
        List<TimeSpan>? delays = null)
    {
        var handler = new FakeBatchHandler(onBatchHandled, throwOnSend, throwOnCallNumber, script);
        var httpClient = new HttpClient(handler);
        var client = new GraphServiceClient(httpClient, new NoOpAuthenticationProvider());
        var factory = new GraphClientFactory(client);
        var writer = new ContactWriter(
            factory,
            throttle ?? new ThrottleCounter(),
            NullLogger<ContactWriter>.Instance,
            delay: (d, _) => { delays?.Add(d); return Task.CompletedTask; });   // never actually sleep in tests
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
    /// Fake $batch transport. By default echoes a 201-with-id success for every step so
    /// ContactWriter's real batch/retry code runs end to end without hitting Graph. A
    /// <c>script(callNumber, stepId)</c> can answer individual steps with another status and an
    /// optional Retry-After header; <c>throwOnSend</c> throws on call number <c>throwOnCallNumber</c>.
    /// Every request's step ids are recorded in <see cref="RequestStepIds"/> (one list per call).
    /// </summary>
    private sealed class FakeBatchHandler(
        Action? onBatchHandled,
        Exception? throwOnSend = null,
        int throwOnCallNumber = 1,
        Func<int, string, FakeStep>? script = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public List<List<string>> RequestStepIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (throwOnSend is not null && CallCount == throwOnCallNumber)
                throw throwOnSend;
            var bodyStr = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(bodyStr!);
            var ids = doc.RootElement.GetProperty("requests").EnumerateArray()
                .Select(r => r.GetProperty("id").GetString()!)
                .ToList();
            RequestStepIds.Add(ids);

            var responses = new JsonArray();
            for (var i = 0; i < ids.Count; i++)
            {
                var step = script?.Invoke(CallCount, ids[i]) ?? new FakeStep(201);
                var node = new JsonObject { ["id"] = ids[i], ["status"] = step.Status };
                if (step.RetryAfter is not null)
                    node["headers"] = new JsonObject { ["Retry-After"] = step.RetryAfter };
                node["body"] = step.Status is 200 or 201
                    ? new JsonObject { ["id"] = $"graph-contact-{CallCount}-{i}" }
                    : new JsonObject { ["error"] = new JsonObject { ["code"] = $"status{step.Status}", ["message"] = "fake" } };
                responses.Add(node);
            }
            var json = new JsonObject { ["responses"] = responses }.ToJsonString();

            onBatchHandled?.Invoke();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
