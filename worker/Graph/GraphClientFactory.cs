using Azure.Identity;
using Microsoft.Graph;

namespace AFHSync.Worker.Graph;

/// <summary>
/// Singleton factory that creates and exposes a <see cref="GraphServiceClient"/> configured
/// with application credentials (client credentials flow). Supports optional
/// <see cref="DelegatingHandler"/> injection so Plan 02's GraphResilienceHandler can be
/// plugged in via DI without modifying this class.
///
/// When DelegatingHandlers are provided, the Graph HTTP pipeline is built using
/// <see cref="Microsoft.Graph.GraphClientFactory.Create"/> which injects the handlers
/// into the request pipeline. When no handlers are provided, the simple credential-only
/// path is used.
/// </summary>
public class GraphClientFactory
{
    public GraphServiceClient Client { get; }

    /// <summary>
    /// Per-request timeout applied to the underlying HttpClient. Caps how long a single
    /// Graph call (including Polly retries in GraphResilienceHandler) can hang before
    /// HttpClient force-cancels it — otherwise a stuck TCP read could silently block
    /// a sync until the 2h / 6h StaleRunCleanupService cutoff. 10 min accommodates
    /// large photo writes and long DDG pagination runs with retry backoff.
    /// </summary>
    private static readonly TimeSpan GraphHttpClientTimeout = TimeSpan.FromMinutes(10);

    public GraphClientFactory(IConfiguration config, IEnumerable<DelegatingHandler>? handlers = null)
    {
        var tenantId = config["Graph:TenantId"]
            ?? throw new InvalidOperationException("Graph:TenantId configuration is required");
        var clientId = config["Graph:ClientId"]
            ?? throw new InvalidOperationException("Graph:ClientId configuration is required");
        var clientSecret = config["Graph:ClientSecret"]
            ?? throw new InvalidOperationException("Graph:ClientSecret configuration is required");

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);

        // Always construct via the SDK factory so we can own the HttpClient and
        // enforce a finite Timeout. The no-handler branch (production startup before
        // Plan 02 shipped, and tests) used to build the client from the credential
        // alone, which meant no outer timeout at all.
        var defaults = Microsoft.Graph.GraphClientFactory.CreateDefaultHandlers()
            .Where(h => h is not Microsoft.Kiota.Http.HttpClientLibrary.Middleware.RetryHandler)
            .ToList();

        var allHandlers = new List<DelegatingHandler>();
        if (handlers?.Any() == true)
        {
            // Prepend our custom handlers (e.g. GraphResilienceHandler) before SDK defaults.
            allHandlers.AddRange(handlers);
        }
        allHandlers.AddRange(defaults);

        var httpClient = Microsoft.Graph.GraphClientFactory.Create(handlers: allHandlers.ToArray());
        httpClient.Timeout = GraphHttpClientTimeout;
        Client = new GraphServiceClient(httpClient, credential);
    }
}
