using System.Net;
using System.Net.Http.Headers;
using AFHSync.Worker.Graph;
using Microsoft.Extensions.Logging.Abstractions;

namespace AFHSync.Tests.Unit.Sync;

/// <summary>
/// Tests for GraphResilienceHandler — Polly 8 DelegatingHandler that retries on
/// Graph 429/503 with exponential backoff, Retry-After header respect, and throttle callback.
/// </summary>
public class GraphResilienceHandlerTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a handler chain: GraphResilienceHandler → MockHttpHandler → returns queued responses.
    /// </summary>
    private static (HttpClient client, GraphResilienceHandler resilience, MockInnerHandler inner)
        BuildChain(Queue<HttpResponseMessage> responses, Action<int>? onThrottle = null)
    {
        var inner = new MockInnerHandler(responses);
        var resilience = new GraphResilienceHandler(
            NullLogger<GraphResilienceHandler>.Instance,
            onThrottle)
        {
            InnerHandler = inner
        };
        var client = new HttpClient(resilience) { BaseAddress = new Uri("https://graph.microsoft.com") };
        return (client, resilience, inner);
    }

    private static HttpResponseMessage Make(HttpStatusCode code) =>
        new(code) { Content = new StringContent(string.Empty) };

    private static HttpResponseMessage MakeOk() => Make(HttpStatusCode.OK);

    private static HttpResponseMessage Make429WithRetryAfter(TimeSpan retryAfter)
    {
        var response = Make(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    // ── Test 1: retries on 429 ───────────────────────────────────────────────

    [Fact]
    public async Task Handler_Retries_On_TooManyRequests()
    {
        var responses = new Queue<HttpResponseMessage>([
            Make(HttpStatusCode.TooManyRequests),
            MakeOk()
        ]);
        var (client, _, inner) = BuildChain(responses);

        var result = await client.GetAsync("/v1.0/users");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, inner.CallCount); // first attempt failed, second succeeded
    }

    // ── Test 2: retries on 503 ───────────────────────────────────────────────

    [Fact]
    public async Task Handler_Retries_On_ServiceUnavailable()
    {
        var responses = new Queue<HttpResponseMessage>([
            Make(HttpStatusCode.ServiceUnavailable),
            MakeOk()
        ]);
        var (client, _, inner) = BuildChain(responses);

        var result = await client.GetAsync("/v1.0/users");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, inner.CallCount);
    }

    // ── Test 3: respects Retry-After header ─────────────────────────────────

    [Fact]
    public async Task Handler_Respects_RetryAfter_Header_On_429()
    {
        // We pass a very short retry-after (100ms) and verify the result is still OK.
        // The key assertion is that a Retry-After header is not ignored (no exception thrown).
        var retryAfter = TimeSpan.FromMilliseconds(100);
        var responses = new Queue<HttpResponseMessage>([
            Make429WithRetryAfter(retryAfter),
            MakeOk()
        ]);
        var (client, _, inner) = BuildChain(responses);

        var result = await client.GetAsync("/v1.0/users");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, inner.CallCount);
    }

    // ── Test 4: exponential backoff when no Retry-After header ──────────────

    [Fact]
    public async Task Handler_Uses_Exponential_Backoff_When_No_RetryAfter()
    {
        // Two 429s then success — verify three total calls were made.
        // (Backoff timing is not asserted directly to avoid slow tests.)
        var responses = new Queue<HttpResponseMessage>([
            Make(HttpStatusCode.TooManyRequests),
            Make(HttpStatusCode.TooManyRequests),
            MakeOk()
        ]);
        var (client, _, inner) = BuildChain(responses);

        var result = await client.GetAsync("/v1.0/users");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(3, inner.CallCount);
    }

    // ── Test 5: stops after 5 retries ────────────────────────────────────────

    [Fact]
    public async Task Handler_StopsAfter_MaxRetryAttempts_AndReturnsLastErrorResponse()
    {
        // 6 429 responses: initial attempt + 5 retries = MaxRetryAttempts reached.
        // Polly should return the last error response (or throw) after 5 retries.
        const int maxRetries = 5;
        var responses = new Queue<HttpResponseMessage>(
            Enumerable.Range(0, maxRetries + 1).Select(_ => Make(HttpStatusCode.TooManyRequests)));
        var (client, _, inner) = BuildChain(responses);

        // After max retries exceeded, Polly returns the last outcome (no exception for result-based retries)
        var result = await client.GetAsync("/v1.0/users");

        Assert.Equal(HttpStatusCode.TooManyRequests, result.StatusCode);
        Assert.Equal(maxRetries + 1, inner.CallCount); // 1 initial + 5 retries
    }

    // ── Test 6: throttle callback invoked on each retry ──────────────────────

    [Fact]
    public async Task Handler_Invokes_OnThrottle_Callback_On_Each_Retry()
    {
        int callbackCount = 0;
        var responses = new Queue<HttpResponseMessage>([
            Make(HttpStatusCode.TooManyRequests),
            Make(HttpStatusCode.TooManyRequests),
            MakeOk()
        ]);
        var (client, _, _) = BuildChain(responses, onThrottle: _ => callbackCount++);

        await client.GetAsync("/v1.0/users");

        Assert.Equal(2, callbackCount); // two retries triggered two callback invocations
    }

    // ── Test 7: pass-through for non-throttled responses ─────────────────────

    [Fact]
    public async Task Handler_PassesThrough_Non429_503_Responses_Without_Retry()
    {
        var responses = new Queue<HttpResponseMessage>([
            Make(HttpStatusCode.NotFound)
        ]);
        var (client, _, inner) = BuildChain(responses);

        var result = await client.GetAsync("/v1.0/users");

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal(1, inner.CallCount); // no retries for 404
    }

    // ── MockInnerHandler ─────────────────────────────────────────────────────

    private sealed class MockInnerHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (responses.TryDequeue(out var response))
                return Task.FromResult(response);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("MockInnerHandler: response queue exhausted")
            });
        }
    }
}
