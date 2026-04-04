using System.Net;
using System.Net.Http.Headers;
using Polly;
using Polly.Retry;

namespace AFHSync.Worker.Graph;

/// <summary>
/// <see cref="DelegatingHandler"/> that wraps all Graph HTTP calls with a Polly 8
/// resilience pipeline. Retries on 429 (TooManyRequests) and 503 (ServiceUnavailable)
/// with exponential backoff + jitter, honours the <c>Retry-After</c> header when present,
/// stops after 5 retries, and invokes an optional <paramref name="onThrottle"/> callback
/// on each retry attempt (used by the SyncEngine to increment SyncRun.ThrottleEvents).
///
/// The handler is wired into <see cref="GraphClientFactory"/>'s HTTP pipeline via DI so
/// every GraphServiceClient call benefits from retry logic automatically.
/// </summary>
public class GraphResilienceHandler : DelegatingHandler
{
    private readonly ILogger<GraphResilienceHandler> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public GraphResilienceHandler(
        ILogger<GraphResilienceHandler> logger,
        Action<int>? onThrottle = null)
    {
        _logger = logger;

        _pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = args =>
                {
                    var statusCode = args.Outcome.Result?.StatusCode;
                    if (statusCode == HttpStatusCode.TooManyRequests)
                        return ValueTask.FromResult(true);
                    if (statusCode == HttpStatusCode.ServiceUnavailable)
                        return ValueTask.FromResult(true);
                    // Also retry on transient exceptions (network failures, etc.)
                    return ValueTask.FromResult(args.Outcome.Exception is not null);
                },
                MaxRetryAttempts = 5,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(2),
                MaxDelay = TimeSpan.FromMinutes(5),
                // Override delay when Retry-After header is present on a 429 response.
                // Returning null tells Polly to use the calculated exponential backoff instead.
                DelayGenerator = args =>
                {
                    if (args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests
                        && args.Outcome.Result.Headers.RetryAfter?.Delta is TimeSpan retryAfter)
                    {
                        return new ValueTask<TimeSpan?>(retryAfter);
                    }

                    return new ValueTask<TimeSpan?>((TimeSpan?)null);
                },
                OnRetry = args =>
                {
                    onThrottle?.Invoke(args.AttemptNumber);
                    var statusLabel = args.Outcome.Result is not null
                        ? args.Outcome.Result.StatusCode.ToString()
                        : args.Outcome.Exception?.GetType().Name ?? "Unknown";
                    _logger.LogWarning(
                        "Graph throttled. Retry {AttemptNumber}/5 after {DelayMs}ms. Status: {StatusCode}",
                        args.AttemptNumber,
                        args.RetryDelay.TotalMilliseconds,
                        statusLabel);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return await _pipeline.ExecuteAsync(
            async token =>
            {
                // Clone the request for each retry attempt — HttpRequestMessage cannot be
                // sent more than once (the content stream is consumed on the first send).
                var clone = await CloneRequestAsync(request);
                return await base.SendAsync(clone, token);
            },
            cancellationToken);
    }

    /// <summary>
    /// Creates a deep copy of an <see cref="HttpRequestMessage"/> including all headers
    /// and content. Required because <see cref="HttpRequestMessage"/> instances (and their
    /// content streams) can only be sent once — retries need fresh copies.
    /// </summary>
    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        // Copy headers (does not include Content-Type — that lives on the content object)
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        // Copy properties / options
        foreach (var prop in original.Options)
            clone.Options.TryAdd(prop.Key, prop.Value);

        // Clone content if present
        if (original.Content is not null)
        {
            var contentBytes = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(contentBytes);

            // Copy content headers (Content-Type, Content-Length, etc.)
            foreach (var header in original.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        clone.Version = original.Version;

        return clone;
    }
}
